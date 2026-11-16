using System;
using System.Numerics;
using System.Text.Json.Nodes;
using Dalamud.Game.ClientState.Objects.Types;

namespace AnimSync;

// Leg / Arm Rotations in GPose. There Brio does the posing (LivePose does nothing), so the swing is written into the
// pose Brio holds: its pose is read as JSON, the thigh / upper arm and the foot / hand get new rotations, and the pose
// is loaded back. The swing is the same as outside GPose: the whole limb turns about the line from hip / shoulder to
// foot / hand, so the foot / hand stays where it is, and it is turned back to keep facing the same way.
public sealed partial class AnimSync {
    private sealed class BrioLimb {
        public bool Captured;
        public Quaternion BaseTop, BaseEnd;        // the bones' own (local) rotations in Brio's pose before we touched them
        public Quaternion WorldTop, WorldEnd;      // their model-space rotations then
        public Vector3 Axis;                       // hip -> foot (shoulder -> hand), model space
        public Quaternion WrittenTop, WrittenEnd;  // what we last wrote (a different value there later means you posed it in Brio)
        public float Sent;
        public Vector3 EndAt;
    }
    private readonly BrioLimb[] brioLimbs = { new(), new(), new(), new() };
    private nint brioTwistActor;
    private long brioTwistAt;
    private readonly float[] brioSeen = new float[4];
    private long brioCheckAt;
    private bool brioChecking;

    private static string? BrioPoseGet(IGameObject actor) {
        try { return Interface.GetIpcSubscriber<IGameObject, string?>("Brio.GetPoseAsJson.V3").InvokeFunc(actor); }
        catch(Exception ex) { Logger.Debug(ex, "Brio.GetPoseAsJson isn't available."); return null; }
    }

    private static bool BrioPoseSet(IGameObject actor, string json) {
        try { return Interface.GetIpcSubscriber<IGameObject, bool, string, bool>("Brio.LoadPoseFromJson.V3").InvokeFunc(actor, false, json); }
        catch(Exception ex) { Logger.Debug(ex, "Brio.LoadPoseFromJson isn't available."); return false; }
    }

    private void ForgetBrioTwist() {
        foreach(var limb in brioLimbs) { limb.Captured = false; limb.Sent = 0; }
        brioTwistActor = 0;
        brioChecking = false;
    }

    private void UpdateBrioTwist() {
        if(!Client.IsGPosing) { if(brioTwistActor != 0) ForgetBrioTwist(); return; }
        var any = false;
        for(var i = 0; i < 4; i++) if(MathF.Abs(limbTwist[i]) >= 0.05f || brioLimbs[i].Captured) any = true;
        if(!any) return;
        var actor = LimbActor();
        if(actor == null) return;
        if(brioTwistActor != actor.Address) { ForgetBrioTwist(); brioTwistActor = actor.Address; }
        var now = Environment.TickCount64;
        // for the record: did the foot / hand stay where it was?
        if(brioChecking && now >= brioCheckAt) {
            brioChecking = false;
            var report = string.Empty;
            for(var i = 0; i < 4; i++) {
                if(!brioLimbs[i].Captured || !TryLimbBone(actor, TwistBones[i][1], out var at, out _)) continue;
                report += FormattableString.Invariant($" {TwistBones[i][1]}: moved {Vector3.Distance(at, brioLimbs[i].EndAt):0.0000} (swing {limbTwist[i]:0.0} deg);");
            }
            if(report.Length > 0) LimbLog("gpose swing:" + report);
        }
        var changed = false;
        for(var i = 0; i < 4; i++) if(MathF.Abs(limbTwist[i] - brioLimbs[i].Sent) > 0.05f) changed = true;
        // sent once the sliders have stopped moving for a moment (each load is an undo step in Brio)
        if(!changed || now < brioTwistAt) return;
        brioTwistAt = now + 250;   // a few a second at most while dragged (each load is an undo step in Brio)
        if(BrioPoseGet(actor) is not { } text || JsonNode.Parse(text) is not JsonObject root || root["Bones"] is not JsonObject bones) {
            limbLockStatus = "Brio didn't give the pose. Is it enabled?";
            brioTwistAt = now + 3000;
            return;
        }
        for(var i = 0; i < 4; i++) {
            var limb = brioLimbs[i];
            string topName = TwistBones[i][0], endName = TwistBones[i][1];
            if(bones[topName] is not JsonObject topBone || bones[endName] is not JsonObject endBone ||
                !LimbRules.TryReadQuaternion(topBone["Rotation"], out var topNow) || !LimbRules.TryReadQuaternion(endBone["Rotation"], out var endNow)) continue;
            // posed by hand in Brio since: that is the new starting point
            if(limb.Captured && (LimbRules.Angle(topNow, limb.WrittenTop) > 0.5f || LimbRules.Angle(endNow, limb.WrittenEnd) > 0.5f)) limb.Captured = false;
            var wanted = limbTwist[i];
            if(!limb.Captured) {
                if(MathF.Abs(wanted) < 0.05f) { limb.Sent = 0; continue; }
                if(!TryLimbBone(actor, topName, out var top, out var worldTop) || !TryLimbBone(actor, endName, out var end, out var worldEnd)) continue;
                var along = end - top;
                if(along.LengthSquared() < 1e-8f) continue;
                limb.Captured = true;
                limb.BaseTop = topNow; limb.BaseEnd = endNow;
                limb.WorldTop = worldTop; limb.WorldEnd = worldEnd;
                limb.Axis = Vector3.Normalize(along);
                limb.EndAt = end;
            }
            Quaternion newTop, newEnd;
            if(MathF.Abs(wanted) < 0.05f) { newTop = limb.BaseTop; newEnd = limb.BaseEnd; }
            else {
                var turn = Quaternion.CreateFromAxisAngle(limb.Axis, wanted * (i % 2 == 0 ? 1 : -1) * MathF.PI / 180);
                // each in its own frame, after what it has: the limb is swung, the foot / hand is turned back
                newTop = Quaternion.Normalize(limb.BaseTop * Quaternion.Inverse(limb.WorldTop) * turn * limb.WorldTop);
                newEnd = Quaternion.Normalize(limb.BaseEnd * Quaternion.Inverse(limb.WorldEnd) * Quaternion.Inverse(turn) * limb.WorldEnd);
            }
            topBone["Rotation"] = LimbRules.WriteQuaternion(newTop, topBone["Rotation"]);
            endBone["Rotation"] = LimbRules.WriteQuaternion(newEnd, endBone["Rotation"]);
            limb.WrittenTop = newTop; limb.WrittenEnd = newEnd;
            limb.Sent = wanted;
            if(MathF.Abs(wanted) < 0.05f) limb.Captured = false;
        }
        if(!BrioPoseSet(actor, root.ToJsonString())) { limbLockStatus = "Brio didn't take the pose."; brioTwistAt = now + 3000; return; }
        brioChecking = true;
        brioCheckAt = now + 400;
    }
}
