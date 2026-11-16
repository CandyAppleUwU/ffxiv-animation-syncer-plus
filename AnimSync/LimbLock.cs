using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnimSync;

// Feet lock / Hand lock: like an IK controller in Blender. While a lock is on, the feet (or hands) stay where they are
// in the world when the character is moved with the offsets - lower the body and the knees bend.
//
// It works through LivePose (the posing part of SimpleHeels): the foot / hand bone is given an offset back to where it
// was, with LivePose's own two-joint IK switched on, plus a turn so it keeps facing as it did. LivePose bends the limb
// to that every frame on top of the animation, so nothing here runs per bone per frame - only the character's root is
// read, and a new offset is sent when it has moved. Past the limb's reach the offset is cut short: the limb goes
// straight and the foot comes up with the body.
// What was in LivePose before is kept, and put back when the lock ends.
public sealed partial class AnimSync {
    // Two ways to hold a limb. IK (on): the foot / hand is given a place to be and LivePose bends the limb to it every
    // frame, so it stays put while the animation moves the hips. Turns only (off): fixed turns of thigh, calf and foot,
    // worked out once - tried in 1.4.4, and the feet wandered as soon as the animation moved the leg at all.
    private static readonly bool LimbUseIk = true;
    private bool lockFeet, lockHands;
    // a lock can be let go on single sliders (the foot / hand icons by them): height, forward / back, left / right, rotation
    private readonly bool[] lockFeetAxis = { true, true, true, true }, lockHandsAxis = { true, true, true, true };
    // Foot & Hand Control: each foot / hand moved by hand on top of its lock (left foot, right foot, left hand, right hand)
    private readonly Vector3[] limbNudge = new Vector3[4];       // X left / right, Y height, Z forward / backward
    private readonly float[] limbNudgeTurn = new float[4];       // degrees, about the upright
    private readonly bool[] limbPicked = { true, true, true, true };
    private static readonly string[] LimbNames = { "Left foot", "Right foot", "Left hand", "Right hand" };
    // Leg / Arm Rotations: the limb swung round the line from hip / shoulder to foot / hand (left leg, right leg, left arm, right arm),
    // degrees. Chained legs (and chained arms) move together, mirrored, so both turn out or both turn in.
    private readonly float[] limbTwist = new float[4];
    private readonly bool[] twistChained = { true, true };   // legs, arms
    private static readonly string[] TwistNames = { "L Leg", "R Leg", "L Arm", "R Arm" };

    private void ResetLimbNudges() {
        Array.Clear(limbNudge);
        Array.Clear(limbNudgeTurn);
        Array.Clear(limbTwist);
    }

    /// <summary>Leg / Arm Rotations: four sliders that swing the knee or elbow round the hip-to-foot (shoulder-to-hand) line (the knee or elbow
    /// swings round, the foot / hand stays put). The chain buttons tie a left and right slider together.</summary>
    private void DrawTwistControl(float font) {
        ImGui.SetNextItemOpen(false, ImGuiCond.Appearing);   // advanced: shut every time the window opens
        if(!ImGui.CollapsingHeader("Leg / Arm Rotations")) return;
        for(var pair = 0; pair < 2; pair++) {
            for(var side = 0; side < 2; side++) {
                var i = pair * 2 + side;
                var value = limbTwist[i];
                ImGui.SetNextItemWidth(font * 10f);
                if(ImGui.DragFloat($"{TwistNames[i]}##Twist{i}", ref value, 0.1f, -180f, 180f, "%.1f deg")) {
                    limbTwist[i] = value;
                    if(twistChained[pair]) limbTwist[i ^ 1] = value;
                }
                if(side == 0) {
                    // the chain sits between the two sliders it ties together
                    ImGui.SameLine();
                    var on = twistChained[pair];
                    ImGui.PushStyleColor(ImGuiCol.Button, on ? new Vector4(1f, 0.45f, 0.75f, 0.55f) : new Vector4(1, 1, 1, 0.06f));
                    if(ImGui.Button(on ? $"Linked##Chain{pair}" : $"Unlinked##Chain{pair}")) {
                        twistChained[pair] = !on;
                        if(twistChained[pair]) limbTwist[pair * 2 + 1] = limbTwist[pair * 2];   // tying them takes the left one's value
                    }
                    ImGui.PopStyleColor();
                    if(ImGui.IsItemHovered()) ImGui.SetTooltip(on ? "Linked: both move together. Click to move them one at a time."
                        : "Unlinked: they move one at a time. Click to move them together.");
                }
            }
        }
        if(ImGui.Button("Reset##Twist")) Array.Clear(limbTwist);
    }

    // thigh / upper arm, then the foot / hand that has to stay where it is
    private static readonly string[][] TwistBones = {
        new[] { "j_asi_a_l", "j_asi_d_l" }, new[] { "j_asi_a_r", "j_asi_d_r" }, new[] { "j_ude_a_l", "j_te_l" }, new[] { "j_ude_a_r", "j_te_r" } };

    /// <summary>The turns for LivePose. The whole limb swings round the straight line from its top joint (hip /
    /// shoulder) to its end (foot / hand): the knee / elbow goes round, the foot / hand stays exactly where it is
    /// (it is on that line), and it is turned back so it also keeps facing the same way. The line doesn't change
    /// as the limb swings, so it can be read from the pose as it is now; what is already in the pose from the last
    /// turn sent (<paramref name="sent"/>) is taken out first. The right side is mirrored so equal numbers look alike.</summary>
    private List<LimbStack> TwistStacks(IGameObject self, float[] sent) {
        var stacks = new List<LimbStack>();
        for(var i = 0; i < 4; i++) {
            if(MathF.Abs(limbTwist[i]) < 0.05f) continue;
            if(!TryLimbBone(self, TwistBones[i][0], out var top, out var topRotation) || !TryLimbBone(self, TwistBones[i][1], out var end, out var endRotation)) continue;
            var along = end - top;
            if(along.LengthSquared() < 1e-8f) continue;
            var axis = Vector3.Normalize(along);
            var sign = i % 2 == 0 ? 1 : -1;
            var turn = Quaternion.CreateFromAxisAngle(axis, limbTwist[i] * sign * MathF.PI / 180);
            var before = Quaternion.CreateFromAxisAngle(axis, sent[i] * sign * MathF.PI / 180);
            var baseTop = Quaternion.Normalize(Quaternion.Inverse(before) * topRotation);   // the thigh / upper arm without our turn
            // in each bone's own frame, as the other turns sent to LivePose are
            stacks.Add(new LimbStack(TwistBones[i][0], Vector3.Zero, Quaternion.Normalize(Quaternion.Inverse(baseTop) * turn * baseTop), i < 2, false));
            // the foot / hand is carried round with the limb: turn it back (it already shows our counter-turn, so it is its own base)
            stacks.Add(new LimbStack(TwistBones[i][1], Vector3.Zero, Quaternion.Normalize(Quaternion.Inverse(endRotation) * Quaternion.Inverse(turn) * endRotation), i < 2, false));
        }
        return stacks;
    }

    // Leg / Arm Rotations on their own (no lock holding): LivePose gets the turns, and its old data back afterwards
    private sealed class TwistState {
        public ushort Index;
        public nint Address;
        public (ushort, ushort) Timelines;
        public string BaseJson = string.Empty;
        public readonly float[] Sent = new float[4], Seen = new float[4];
        public long SentAt, ChangeAt;
    }
    private TwistState? twistHold;

    /// <summary>The character the limb tools work on: you, or in GPose the one picked under "Move" (the same as Auto Align).</summary>
    private IGameObject? LimbActor() => Client.IsGPosing ? AlignMover() : Objects.LocalPlayer;

    /// <summary>How long the sliders must stay still before anything is sent to LivePose / Brio (one send, no more).</summary>
    private const long SettleMs = 1500;   // (not used for the locks any more: they follow the sliders live)

    private ushort limbHandTimeline;   // the animation the hand-made limb changes were made on

    /// <summary>The Foot &amp; Hand Control and Leg / Arm Rotations values belong to the animation they were made on:
    /// switching to another animation puts them back to zero.</summary>
    private void WatchLimbAnimation() {
        var any = false;
        for(var i = 0; i < 4; i++) if(limbNudge[i] != Vector3.Zero || MathF.Abs(limbNudgeTurn[i]) > 0.05f || MathF.Abs(limbTwist[i]) > 0.05f) any = true;
        if(!any) { limbHandTimeline = 0; return; }
        if(Client.IsGPosing || LimbActor() is not { } actor) return;
        var main = LimbTimelines(actor).Item1;
        if(main == 0) return;
        if(limbHandTimeline == 0) limbHandTimeline = main;
        else if(limbHandTimeline != main) { ResetLimbNudges(); limbHandTimeline = 0; }
    }

    private void EndTwist() {
        if(twistHold is not { } held) return;
        twistHold = null;
        LivePoseSet(held.Index, held.BaseJson);
        limbRetryAt = Math.Max(limbRetryAt, Environment.TickCount64 + 250);
    }

    private void UpdateLimbTwist() {
        WatchLimbAnimation();
        var any = false;
        foreach(var t in limbTwist) if(MathF.Abs(t) >= 0.05f) any = true;
        // while a lock is holding, the turns go out with it (see SendLimbLock)
        var self = (any || twistHold != null) && !(lockFeet || lockHands) && !Client.IsGPosing && !BoneAlignBusy && !snapPending && !timelineWindow.IsOpen ? LimbActor() : null;
        if(self == null || !any) { EndTwist(); return; }
        var now = Environment.TickCount64;
        var timelines = LimbTimelines(self);
        if(twistHold is { } stale && (stale.Address != self.Address || stale.Timelines != timelines)) EndTwist();
        if(twistHold == null) {
            if(now < limbRetryAt || timelines.Item1 == 0 || LivePoseGet((ushort)self.ObjectIndex) is not { } baseJson) return;
            twistHold = new TwistState { Index = (ushort)self.ObjectIndex, Address = self.Address, Timelines = timelines, BaseJson = baseJson, SentAt = -1000, ChangeAt = now };
        }
        var held = twistHold!;
        var changed = false;
        for(var i = 0; i < 4; i++) if(MathF.Abs(limbTwist[i] - held.Sent[i]) > 0.05f) changed = true;
        // sent once the sliders have stopped moving for a moment
        for(var i = 0; i < 4; i++) if(MathF.Abs(limbTwist[i] - held.Seen[i]) > 0.05f) { held.Seen[i] = limbTwist[i]; held.ChangeAt = now; }
        if(!changed || now - held.SentAt < 80 || now < held.ChangeAt) return;
        if(!LivePoseSet(held.Index, LimbRules.PoseJson(held.BaseJson, timelines.Item1, timelines.Item2, TwistStacks(self, held.Sent)))) { held.ChangeAt = now + 3000; return; }
        Array.Copy(limbTwist, held.Sent, 4);
        held.SentAt = now;
    }

    /// <summary>Foot &amp; Hand Control: the same four sliders as the offsets, but they move the feet and hands that are
    /// lit in the row of icons above them, not the body. The limb bends to follow (it needs that limb's lock, which
    /// is switched on when you use this).</summary>
    private void DrawLimbControl(float font) {
        ImGui.SetNextItemOpen(false, ImGuiCond.Appearing);   // advanced: shut every time the window opens
        if(!ImGui.CollapsingHeader("Foot & Hand Control")) return;
        ImGui.BeginDisabled(Client.IsGPosing);
        var size = ImGui.GetFrameHeight() * 1.35f;
        for(var i = 0; i < 4; i++) {
            if(i > 0) ImGui.SameLine(0, i == 2 ? font * 0.8f : 4);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1, 1, 1, limbPicked[i] ? 0.10f : 0.04f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.16f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.24f));
            if(ImGui.Button($"##LimbPick{i}", new Vector2(size))) limbPicked[i] = !limbPicked[i];
            ImGui.PopStyleColor(3);
            var colour = ImGui.GetColorU32(limbPicked[i] ? new Vector4(1f, 0.45f, 0.75f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 0.7f));
            var centre = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            DrawLimbIcon(ImGui.GetWindowDrawList(), centre - new Vector2(0, size * 0.08f), size * 0.3f, colour, i < 2, i % 2 == 1);
            // which side it is: a small L or R in the corner
            ImGui.GetWindowDrawList().AddText(new Vector2(ImGui.GetItemRectMin().X + 2, ImGui.GetItemRectMax().Y - ImGui.GetFontSize() * 0.8f - 1), colour, i % 2 == 0 ? "L" : "R");
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Client.IsGPosing ? "Not available in GPose (Brio does the posing there)." : $"{LimbNames[i]}: {(limbPicked[i] ? "moved by the sliders below. Click to leave it out." : "left alone. Click to move it with the sliders below.")}");
        }
        var first = Array.IndexOf(limbPicked, true);
        var shown = first < 0 ? Vector3.Zero : limbNudge[first];
        var turn = first < 0 ? 0 : limbNudgeTurn[first];
        float height = shown.Y, forward = shown.Z, side = shown.X, turned = turn;
        var changed = false;
        ImGui.BeginDisabled(first < 0);
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Height##Limb", ref height, 0.0005f, -1f, 1f, "%.4f");
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Forward / Backward##Limb", ref forward, 0.0005f, -1f, 1f, "%.4f");
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Left / Right##Limb", ref side, 0.0005f, -1f, 1f, "%.4f");
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Rotation##Limb", ref turned, 0.1f, -180f, 180f, "%.1f deg");
        if(changed) {
            // what you dragged is added to every lit limb (each keeps what it already had)
            var delta = new Vector3(side - shown.X, height - shown.Y, forward - shown.Z);
            for(var i = 0; i < 4; i++) {
                if(!limbPicked[i]) continue;
                limbNudge[i] += delta;
                limbNudgeTurn[i] = Math.Clamp(limbNudgeTurn[i] + turned - turn, -180f, 180f);
                if(i < 2) lockFeet = true; else lockHands = true;   // a limb can only be moved while its lock holds it
            }
        }
        if(ImGui.Button("Reset##Limb"))
            for(var i = 0; i < 4; i++) if(limbPicked[i]) { limbNudge[i] = Vector3.Zero; limbNudgeTurn[i] = 0; }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Puts the lit feet / hands back where their lock holds them.");
        ImGui.EndDisabled();
        ImGui.EndDisabled();
    }
    private string limbLockStatus = string.Empty;
    private LimbLockState? limbLock;
    private Vector3 limbRestPosition;
    private float limbRestRotation;
    private long limbRestSince, limbRetryAt;

    private sealed class LockedLimb {
        public string Bone = string.Empty;
        public bool Foot;
        public Vector3 World;            // where it was when locked
        public Vector3 ModelAtLock;
        public Quaternion WorldRotation; // the way it faced when locked
        public string TopBone = string.Empty, MiddleBone = string.Empty;   // thigh + calf, or upper arm + forearm
        public Vector3 Top, Middle;      // where those were (model space, when locked)
        public Quaternion TopRotation, MiddleRotation, EndRotation;
        public Vector3 Wanted;           // the offset that would put the foot / hand exactly back
        public bool Reached;             // turning the limb can put it there (if not: left as the animation has it)
        public Quaternion TopTurn = Quaternion.Identity, MiddleTurn = Quaternion.Identity;
        public Quaternion Solved = Quaternion.Identity;   // the foot's / hand's own turn, so it keeps facing the same way
        public Quaternion Trim = Quaternion.Identity;     // a correction on top, from looking at the result
        public Quaternion Rotation => Quaternion.Normalize(Solved * Trim);

        public bool KeepHeight;          // put on the floor: where it can't reach its spot it comes in under the body, not up off the floor
        public Vector3 Offset;           // IK way: how far the foot / hand is moved (cut short where the limb can't reach)

        public Quaternion Facing;        // the way it should face now (world): WorldRotation, or turned with the body

        public int Index;                // 0 left foot, 1 right foot, 2 left hand, 3 right hand
        public float Turned;             // the Foot & Hand Control rotation that went into Facing

        public void Solve(Vector3 wanted, Quaternion rootRotation, Quaternion facing, float turned) {
            Wanted = wanted;
            Facing = facing;
            Turned = turned;
            if(LimbUseIk) {
                Reached = wanted.Length() >= 0.0004f || MathF.Abs(turned) > 0.05f;
                (Offset, Solved) = LimbRules.Solve(Top, Middle, ModelAtLock, MiddleRotation, EndRotation, Foot ? -Vector3.UnitZ : Vector3.UnitZ,
                    wanted, LimbRules.DesiredModel(rootRotation, facing), KeepHeight);
                return;
            }
            Reached = wanted.Length() >= 0.0004f && LimbRules.SolveTurns(Top, Middle, ModelAtLock, TopRotation, MiddleRotation, EndRotation,
                Foot ? -Vector3.UnitZ : Vector3.UnitZ, wanted, LimbRules.DesiredModel(rootRotation, facing), out TopTurn, out MiddleTurn, out Solved);
        }
    }

    private sealed class LimbLockState {
        public ushort Index;                     // the character's object index (LivePose's way of naming it)
        public nint Address;
        public (ushort Main, ushort Second) Timelines;
        public Vector3 GamePosition;
        public float GameRotation;
        public bool Feet, Hands;
        public Vector3 RootPosition;             // the character's root when the lock took hold
        public Quaternion RootRotation = Quaternion.Identity;
        public bool Reported;
        public string BaseJson = string.Empty;   // LivePose's data without the lock
        public readonly List<LockedLimb> Limbs = new();
        public bool Sent;
        public List<LimbStack> Twist = new();     // Leg / Arm Rotations that go out with the lock
        public readonly float[] TwistSent = new float[4];
        public long SentAt, FixAt, WatchAt, ReportAt, ChangeAt;
        public float[] Seen = Array.Empty<float>();   // the values as of the last look, to tell when they stop changing
        public int FixesLeft;
    }

    private static unsafe (ushort, ushort) LimbTimelines(IGameObject actor) {
        var character = (Character*)actor.Address;
        return (character->Timeline.TimelineSequencer.GetSlotTimeline(0), character->Timeline.TimelineSequencer.GetSlotTimeline(1));
    }

    /// <summary>The character's root (what model space hangs off), with the SimpleHeels offset in it.</summary>
    private static unsafe bool TryLimbRoot(IGameObject actor, out Vector3 position, out Quaternion rotation, out Vector3 scale) {
        position = default; rotation = Quaternion.Identity; scale = Vector3.One;
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if(skeleton == null) return false;
        var transform = skeleton->Transform;
        position = (Vector3)transform.Position;
        Quaternion turn = transform.Rotation;
        rotation = turn;
        scale = (Vector3)transform.Scale;
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) && float.IsFinite(rotation.W);
    }

    /// <summary>A bone's position and rotation in model space.</summary>
    private static unsafe bool TryLimbBone(IGameObject actor, string name, out Vector3 position, out Quaternion rotation) {
        position = default; rotation = Quaternion.Identity;
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount < 1) return false;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);   // hands and feet are in the body's own skeleton
        if(pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Length > 4096) return false;
        var bones = pose->Skeleton->Bones;
        for(var i = 0; i < bones.Length; i++) {
            if(!string.Equals(bones[i].Name.String, name, StringComparison.Ordinal)) continue;
            var modelPose = pose->GetSyncedPoseModelSpace();
            if(modelPose == null || i >= modelPose->Length) return false;
            var bone = (*modelPose)[i];
            position = new Vector3(bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
            rotation = new Quaternion(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
            return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) && float.IsFinite(rotation.W);
        }
        return false;
    }

    private string? LivePoseGet(ushort index) {
        try { return Interface.GetIpcSubscriber<ushort, string>("LivePose.GetPose").InvokeFunc(index); }
        catch(Exception ex) { Logger.Debug(ex, "LivePose.GetPose isn't available."); return null; }
    }

    private bool LivePoseSet(ushort index, string json) {
        try { Interface.GetIpcSubscriber<ushort, string, bool>("LivePose.SetPose").InvokeAction(index, json); return true; }
        catch(Exception ex) { Logger.Debug(ex, "LivePose.SetPose isn't available."); return false; }
    }

    /// <summary>Lets go: LivePose gets back what it had before the lock.</summary>
    private void EndLimbLock() {
        if(limbLock is not { } held) return;
        limbLock = null;
        if(!held.Sent) return;
        LivePoseSet(held.Index, held.BaseJson);
        // the limbs take a frame or two to go back: don't take hold again while they still show the old lock
        limbRetryAt = Math.Max(limbRetryAt, Environment.TickCount64 + 250);
    }

    private void SendLimbLock(LimbLockState held, long now) {
        var stacks = new List<LimbStack>();
        // IK way: one entry on the foot / hand - where to, and its own turn. Turns-only way: thigh, calf, foot.
        foreach(var limb in held.Limbs) {
            if(!limb.Reached) continue;
            if(LimbUseIk) { stacks.Add(new LimbStack(limb.Bone, limb.Offset, limb.Rotation, limb.Foot)); continue; }
            stacks.Add(new LimbStack(limb.TopBone, Vector3.Zero, limb.TopTurn, limb.Foot, false));
            stacks.Add(new LimbStack(limb.MiddleBone, Vector3.Zero, limb.MiddleTurn, limb.Foot, false));
            stacks.Add(new LimbStack(limb.Bone, Vector3.Zero, limb.Rotation, limb.Foot, false));
        }
        stacks.AddRange(held.Twist);
        if(!LivePoseSet(held.Index, LimbRules.PoseJson(held.BaseJson, held.Timelines.Main, held.Timelines.Second, stacks))) {
            limbLockStatus = "LivePose didn't answer. Is it enabled in SimpleHeels?";
            held.ChangeAt = now + 3000;   // try again in a few seconds, not every frame
            LimbLog("LivePose.SetPose failed");
            return;
        }
        held.Sent = true;
        held.SentAt = now;
    }

    /// <summary>Every frame. Takes hold once you stand still, follows the offsets while held, lets go when you walk,
    /// change animation or switch the locks off.</summary>
    private void UpdateLimbLock() {
        var now = Environment.TickCount64;
        // how long you've stood still is kept up all the time, so a lock can take hold the moment it's asked for
        if(LimbActor() is { } you && (Vector3.Distance(you.Position, limbRestPosition) > 0.002f || MathF.Abs(PositionRules.AngleDelta(you.Rotation, limbRestRotation)) > 0.002f)) {
            limbRestPosition = you.Position;
            limbRestRotation = you.Rotation;
            limbRestSince = now;
        }
        var self = (lockFeet || lockHands) && !Client.IsGPosing && !BoneAlignBusy && !snapPending && !timelineWindow.IsOpen ? LimbActor() : null;
        if(self == null) {
            EndLimbLock();
            if(!lockFeet && !lockHands) limbLockStatus = string.Empty;
            return;
        }
        var timelines = LimbTimelines(self);
        if(limbLock is { } stale && (stale.Address != self.Address || stale.Timelines != timelines || stale.Feet != lockFeet || stale.Hands != lockHands ||
            Vector3.Distance(self.Position, stale.GamePosition) > 0.002f ||
            // (in GPose the alignment turns the character itself, which the lock follows through the root's turn)
            (!Client.IsGPosing && MathF.Abs(PositionRules.AngleDelta(self.Rotation, stale.GameRotation)) > 0.002f))) EndLimbLock();
        if(limbLock == null) {
            // take hold only once you've stood still for a moment (walking would drag the feet behind you)
            if(now - limbRestSince < 300 || now < limbRetryAt || timelines.Item1 == 0) return;
            BeginLimbLock(self, timelines, now);
            return;
        }
        var held = limbLock!;
        if(!TryLimbRoot(self, out var position, out var rotation, out var scale)) return;
        // the offset each limb wants now - leaving out the sliders its lock has been let go on (the icons by them)
        // plus what Foot & Hand Control has added for that limb (in the character's own directions, like the sliders)
        Vector3 Wanted(LockedLimb limb) {
            var axes = limb.Foot ? lockFeetAxis : lockHandsAxis;
            return LimbRules.Wanted(limb.World, limb.ModelAtLock, held.RootPosition, held.RootRotation, position, rotation, scale, axes[0], axes[1], axes[2], axes[3]) +
                LimbRules.ModelPoint(limbNudge[limb.Index], Vector3.Zero, Quaternion.Identity, scale);
        }
        // The limbs follow the sliders live. Once they stop, nothing more is sent (no follow-up corrections).
        var moved = false;
        for(var i = 0; i < 4; i++) if(MathF.Abs(limbTwist[i] - held.TwistSent[i]) > 0.05f) moved = true;
        foreach(var limb in held.Limbs)
            if(Vector3.Distance(Wanted(limb), limb.Wanted) > 0.0004f || MathF.Abs(limbNudgeTurn[limb.Index] - limb.Turned) > 0.05f) moved = true;
        // are the values still changing? (compared with what they were a moment ago)
        var seen = new List<float>();
        foreach(var limb in held.Limbs) { var w = Wanted(limb); seen.AddRange(new[] { w.X, w.Y, w.Z, limbNudgeTurn[limb.Index] }); }
        seen.AddRange(limbTwist);
        var steady = held.Seen.Length == seen.Count;
        if(steady) for(var i = 0; i < seen.Count; i++) if(MathF.Abs(seen[i] - held.Seen[i]) > 0.0004f) { steady = false; break; }
        if(!steady) { held.Seen = seen.ToArray(); held.ChangeAt = now; }
        if(moved) {
            if(now - held.SentAt < 80 || now < held.ChangeAt) return;   // follows the sliders live (about a dozen sends a second at most)
            held.Twist = TwistStacks(self, held.TwistSent);
            Array.Copy(limbTwist, held.TwistSent, 4);
            foreach(var limb in held.Limbs)
                limb.Solve(Wanted(limb), rotation, Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, limbNudgeTurn[limb.Index] * MathF.PI / 180) *
                    LimbRules.Facing(limb.WorldRotation, held.RootRotation, rotation, (limb.Foot ? lockFeetAxis : lockHandsAxis)[3])), limbNudgeTurn[limb.Index]);
            SendLimbLock(held, now);
            held.ReportAt = now + 1000;
            held.Reported = false;
            return;
        }
        // (a read-only look, once, at how it came out - for the log)
        if(!held.Sent || held.Reported || now < held.ReportAt) return;
        // for the record: where each limb ended up, against where it was meant to be
        held.Reported = true;
        var report = string.Empty;
        foreach(var limb in held.Limbs) {
            if(!TryLimbBone(self, limb.Bone, out var at, out var facing)) continue;
            var world = LimbRules.WorldPoint(at, position, rotation, scale);
            var meant = LimbRules.WorldPoint(limb.ModelAtLock + limb.Offset, position, rotation, scale);
            report += FormattableString.Invariant(
                $" {limb.Bone}: {world.Y - self.Position.Y:0.000} high (meant {meant.Y - self.Position.Y:0.000}), {Vector3.Distance(world, meant):0.000} off, facing {LimbRules.Angle(facing, LimbRules.DesiredModel(rotation, limb.Facing)):0.0} deg off{(limb.Reached ? "" : ", not moved")};");
        }
        LimbLog("settled:" + report);
    }

    private void BeginLimbLock(IGameObject self, (ushort, ushort) timelines, long now) {
        if(LivePoseGet((ushort)self.ObjectIndex) is not { } baseJson) {
            limbRetryAt = now + 2000;
            limbLockStatus = "Locks need LivePose (it comes with SimpleHeels - enable it there).";
            LimbLog("LivePose.GetPose failed");
            return;
        }
        if(!TryLimbRoot(self, out var position, out var rotation, out var scale)) return;
        var held = new LimbLockState { Index = (ushort)self.ObjectIndex, Address = self.Address, Timelines = timelines, GamePosition = self.Position, GameRotation = self.Rotation,
            Feet = lockFeet, Hands = lockHands, BaseJson = baseJson, RootPosition = position, RootRotation = rotation };
        // the joints LivePose's IK bends for each: thigh + calf for a foot, upper arm + forearm for a hand
        foreach(var (prefix, top, middle, foot, wanted) in new[] {
            (LimbRules.FootBone, "j_asi_a_", "j_asi_c_", true, lockFeet), (LimbRules.HandBone, "j_ude_a_", "j_ude_b_", false, lockHands) }) {
            if(!wanted) continue;
            foreach(var side in new[] { "l", "r" }) {
                if(!TryLimbBone(self, prefix + side, out var model, out var turn) || !TryLimbBone(self, top + side, out var topAt, out var topTurn) ||
                    !TryLimbBone(self, middle + side, out var middleAt, out var middleTurn)) continue;
                held.Limbs.Add(new LockedLimb { Index = (foot ? 0 : 2) + (side == "l" ? 0 : 1), Bone = prefix + side, TopBone = top + side, MiddleBone = middle + side, Foot = foot,
                    ModelAtLock = model, Top = topAt, Middle = middleAt, TopRotation = topTurn, MiddleRotation = middleTurn, EndRotation = turn,
                    World = LimbRules.WorldPoint(model, position, rotation, scale), WorldRotation = Quaternion.Normalize(rotation * turn),
                    Facing = Quaternion.Normalize(rotation * turn) });
            }
        }
        if(held.Limbs.Count == 0) { limbRetryAt = now + 2000; limbLockStatus = "Couldn't find the bones to lock."; return; }
        SnapLimbsToTable(self, held, rotation);
        limbLock = held;
        limbLockStatus = string.Empty;
    }

    private bool limbSnapTable;   // lined up over a table / low table: feet go to the floor, hands onto its top

    /// <summary>Auto lock: an alignment (or offsetting by hand) switches both locks on by itself.</summary>
    private void AutoLimbLock(bool table, bool hands = true) {
        limbSnapTable = table;
        if(!config.AutoLimbLock) return;
        lockFeet = true;      // every alignment holds the feet
        lockHands = hands;    // hands only where they have something to rest on (a table) or when offsetting by hand
    }

    /// <summary>Bent over a table, where the lock takes hold isn't simply where the feet and hands are:
    ///  - feet lower than the table's top go down onto the floor, at the height they stand at (the shoes' heel height
    ///    included), wherever the animation or the alignment left them - the legs bend or straighten to suit;
    ///  - hands that are over the table, near its top, go onto the top.
    /// Feet up above the table (lying on it, legs in the air) and hands elsewhere are held where they are.</summary>
    private void SnapLimbsToTable(IGameObject self, LimbLockState held, Quaternion rootRotation) {
        if(!limbSnapTable || !alignIsSurface || surfaceProfile is not { } profile || !SnapRules.IsTop(profile.Kind) ||
            surfaceLockedTable is not { } table) return;
        var heel = MoverIsYou(self) ? SimpleHeelsHeight() : 0;
        var size = SkeletonScale(self);
        var into = new Vector3(table.Inward.X, 0, table.Inward.Y);
        var rest = table.Top + config.SurfaceHandThickness;
        var report = string.Empty;
        foreach(var limb in held.Limbs) {
            var was = limb.World.Y;
            if(limb.Foot) {
                // standing on it: the ankle at standing height, and the foot flat, as it is in the standing rest pose
                // (a foot left pointing down from a kneel would hang with its toes off the ground)
                if(limb.World.Y < table.Top - 0.05f && TryRestBone(self, limb.Bone, out var ankle, out var flat)) {
                    limb.World.Y = self.Position.Y + heel + ankle.Y * size;
                    limb.WorldRotation = Quaternion.Normalize(rootRotation * flat);
                    limb.KeepHeight = true;
                }
            } else if(Vector3.Dot(limb.World - table.Point, into) > 0.02f && MathF.Abs(limb.World.Y - rest) < 0.25f) limb.World.Y = rest;
            report += FormattableString.Invariant($" {limb.Bone} {was - self.Position.Y:0.000} -> {limb.World.Y - self.Position.Y:0.000};");
        }
        LimbLog(FormattableString.Invariant($"table lock (top {table.Top - self.Position.Y:0.000} above you, heel {heel:0.000}): heights above where you stand{report}"));
    }

    /// <summary>A bone in the skeleton's standing rest pose (model space).</summary>
    private static unsafe bool TryRestBone(IGameObject actor, string name, out Vector3 position, out Quaternion rotation) {
        position = default; rotation = Quaternion.Identity;
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount < 1) return false;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if(pose == null || pose->Skeleton == null) return false;
        var havok = pose->Skeleton;
        var count = havok->Bones.Length;
        if(count > 4096 || havok->ReferencePose.Length < count || havok->ParentIndices.Length < count) return false;
        var index = -1;
        for(var i = 0; i < count; i++) if(string.Equals(havok->Bones[i].Name.String, name, StringComparison.Ordinal)) { index = i; break; }
        if(index < 0) return false;
        var chain = new List<int>();
        for(var i = index; i >= 0 && chain.Count < 128; i = havok->ParentIndices[i]) chain.Add(i);
        for(var c = chain.Count - 1; c >= 0; c--) {
            var local = havok->ReferencePose[chain[c]];
            position += Vector3.Transform(new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z), rotation);
            rotation = Quaternion.Normalize(rotation * new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W));
        }
        return float.IsFinite(position.Y) && float.IsFinite(rotation.W);
    }

    /// <summary>A line in the alignment history (surface_log_history.txt), for looking into a lock that went wrong.</summary>
    private void LimbLog(string line) {
        try {
            var dir = System.IO.Path.GetDirectoryName(Interface.AssemblyLocation.FullName);
            if(dir != null) System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "surface_log_history.txt"), $"{DateTime.Now:HH:mm:ss.fff} [lock] {line}{Environment.NewLine}");
        } catch(Exception ex) { Logger.Debug(ex, "Couldn't write the lock log."); }
    }

    /// <summary>The foot and hand icons in front of an offset slider: lit = that lock holds while this slider is
    /// changed; click to grey it out and the feet (or hands) move along with the body on this slider only.</summary>
    private void DrawAxisLocks(int axis) {
        DrawAxisLock(true, axis);
        ImGui.SameLine(0, 2);
        DrawAxisLock(false, axis);
        ImGui.SameLine();
    }

    private static readonly string[] AxisNames = { "height", "forward / backward", "left / right", "rotation" };

    private void DrawAxisLock(bool foot, int axis) {
        var axes = foot ? lockFeetAxis : lockHandsAxis;
        var locked = foot ? lockFeet : lockHands;
        var size = ImGui.GetFrameHeight();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1, 1, 1, 0.04f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.22f));
        if(ImGui.Button($"##{(foot ? "foot" : "hand")}Axis{axis}", new Vector2(size))) axes[axis] = !axes[axis];
        ImGui.PopStyleColor(3);
        var on = axes[axis];
        // lit while it holds; grey when let go; dimmer when that lock isn't switched on at all
        var colour = ImGui.GetColorU32(on ? new Vector4(1f, 0.45f, 0.75f, locked ? 1f : 0.55f) : new Vector4(0.5f, 0.5f, 0.5f, locked ? 0.8f : 0.45f));
        DrawLimbIcon(ImGui.GetWindowDrawList(), (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2, size * 0.36f, colour, foot);
        if(!ImGui.IsItemHovered()) return;
        var what = foot ? "feet" : "hands";
        ImGui.SetTooltip((on ? $"Your {what} stay put when you change {AxisNames[axis]}. Click to let them move with you on this slider."
            : $"Your {what} move with you when you change {AxisNames[axis]}. Click to hold them in place on this slider.") +
            (locked ? string.Empty : $"\n(Lock {what} is off, so they aren't held at all right now.)"));
    }

    /// <summary>A footprint or an open hand, drawn from simple shapes (icon fonts shrink unpredictably here).</summary>
    private static void DrawLimbIcon(ImDrawListPtr draw, Vector2 centre, float half, uint colour, bool foot, bool mirror = false) {
        Vector2 Point(float x, float y) => centre + new Vector2(mirror ? -x : x, y) * half;
        if(foot) {
            draw.AddCircleFilled(Point(0.02f, -0.02f), half * 0.46f, colour, 16);                          // ball of the foot
            draw.AddCircleFilled(Point(0.08f, 0.62f), half * 0.33f, colour, 16);                           // heel
            draw.AddQuadFilled(Point(-0.44f, -0.02f), Point(0.48f, -0.02f), Point(0.41f, 0.62f), Point(-0.25f, 0.62f), colour);
            draw.AddCircleFilled(Point(-0.5f, -0.66f), half * 0.19f, colour, 12);                          // toes
            draw.AddCircleFilled(Point(-0.12f, -0.8f), half * 0.15f, colour, 12);
            draw.AddCircleFilled(Point(0.2f, -0.76f), half * 0.14f, colour, 12);
            draw.AddCircleFilled(Point(0.46f, -0.62f), half * 0.13f, colour, 12);
            draw.AddCircleFilled(Point(0.66f, -0.42f), half * 0.11f, colour, 12);
            return;
        }
        // (a rectangle's corners have to be given top-left then bottom-right, mirrored or not)
        void Box(float x0, float y0, float x1, float y1, float round) {
            Vector2 a = Point(x0, y0), b = Point(x1, y1);
            draw.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), colour, half * round);
        }
        Box(-0.42f, -0.05f, 0.62f, 0.9f, 0.25f);                                                         // palm
        var tops = new[] { -0.7f, -0.95f, -0.88f, -0.6f };
        for(var finger = 0; finger < 4; finger++) {
            var x = -0.42f + 0.27f * finger;
            Box(x, tops[finger], x + 0.21f, 0.2f, 0.1f);
        }
        draw.AddLine(Point(-0.34f, 0.6f), Point(-0.82f, 0.12f), colour, MathF.Max(1.5f, half * 0.26f));   // thumb
        draw.AddCircleFilled(Point(-0.82f, 0.12f), MathF.Max(0.75f, half * 0.13f), colour, 10);
    }

    /// <summary>The lock switches, shown with the offsets.</summary>
    private void DrawLimbLocks() {
        ImGui.BeginDisabled(Client.IsGPosing);
        ImGui.Checkbox("Lock feet", ref lockFeet);
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Client.IsGPosing ? "Not available in GPose (Brio does the posing there)." : "Your feet stay where they are while you move with the offsets:\nlower yourself and your knees bend. Stand still to take hold;\nwalking or changing animation lets go.");
        ImGui.SameLine();
        ImGui.Checkbox("Lock hands", ref lockHands);
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Your hands stay where they are while you move with the offsets:\nyour arms bend to keep them there.");
        if(ImGui.Checkbox("Auto lock", ref config.AutoLimbLock)) Save();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Switches both locks on by itself when you align to an object or start offsetting.\nAt a table it also puts your feet on the floor and your hands on the table top.");
        ImGui.EndDisabled();
        if(limbLockStatus.Length > 0 && (lockFeet || lockHands)) ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), limbLockStatus);
    }
}
