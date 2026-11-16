using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnimSync;

// Bone Align: lines your character up with the closest player playing the same animation,
// using a preset made in Blender (Bone Offset > Copy Align Offset), and drives SimpleHeels'
// temp offset with the result. Flow: zero SimpleHeels -> freeze the pair at frame 0 ->
// measure in-game bones -> set SimpleHeels (+ re-check) -> unfreeze. Re-runs by itself when
// either character's race, gender, body type, height or clan changes.
public sealed partial class AnimSync {
    private const float BoneAlignTime = 0;
    private const int BoneAlignSettleFrames = 3;
    private const float BoneAlignTolerance = 0.001f;
    private BoneAlignWindow boneAlignWindow = null!;
    private AlignOffsetWindow alignOffsetWindow = null!;
    private static readonly bool ShowCustomAlign = false;
    // Self/Partner playbacks hold each character still while measuring; null = that character has no
    // animation we can hold, so it's measured as it stands.
    private sealed record BoneAlignSession(TimelinePlayback? Self, TimelinePlayback? Partner, ulong PartnerId, nint PartnerAddress,
        uint Territory, bool Gpose, string Name);
    private BoneAlignSession? boneAlignSession;
    private bool boneAlignRequested, boneAlignTargetRequested;
    private ulong? boneAlignPickRequested;     // a GPose actor picked from the list
    private ulong? alignPickId;
    // GPose only: the character that gets moved (null = you).
    private ulong? alignMoverId;

    /// <summary>The character Bone Align moves: you, or in GPose the actor picked under "Move".</summary>
    private IGameObject? AlignMover() {
        if(!Client.IsGPosing || alignMoverId is not { } id) return ManualSelf();
        return Objects.FirstOrDefault(o => o.GameObjectId == id && SyncScopeRules.IncludesPlayer(o.ObjectIndex, true));
    }

    // GPose: every moved character keeps its own alignment. The fields above always hold the
    // character picked under "Move"; the others are parked here while you work on another one.
    private sealed record MoverState(Vector3? HeelsOffset, float HeelsRotation, Vector3 Auto, float AutoRotation,
        Vector3 Tweak, float TweakRotation, ulong PartnerId, (long Self, long Partner) Looks, ushort? Animation,
        bool ToTarget, ulong? PickId, (nint Address, Vector3 Position, float Rotation)? GposeBase,
        (nint Address, Vector3 Position, Quaternion Rotation)? BrioBase, string Status, bool Surface, SurfaceProfile? Profile);
    private readonly System.Collections.Generic.Dictionary<ulong, MoverState> parkedMovers = new();

    private MoverState CurrentMoverState() => new(heelsOffset, heelsRotation, alignAuto, alignAutoRotation, alignTweak,
        alignTweakRotation, alignPartnerId, alignLooks, alignAnimation, alignToTarget, alignPickId, gposeBase, brioBase, boneAlignStatus,
        alignIsSurface, surfaceProfile);

    private void LoadMoverState(MoverState? state) {
        heelsOffset = state?.HeelsOffset; heelsRotation = state?.HeelsRotation ?? 0;
        alignAuto = state?.Auto ?? Vector3.Zero; alignAutoRotation = state?.AutoRotation ?? 0;
        alignTweak = state?.Tweak ?? Vector3.Zero; alignTweakRotation = state?.TweakRotation ?? 0;
        alignPartnerId = state?.PartnerId ?? 0; alignLooks = state?.Looks ?? default; alignAnimation = state?.Animation;
        alignToTarget = state?.ToTarget ?? false; alignPickId = state?.PickId;
        gposeBase = state?.GposeBase; brioBase = state?.BrioBase;
        alignIsSurface = state?.Surface ?? false; surfaceProfile = state?.Profile; surfaceAutoTried = state?.Animation;
        heelsPending = false; alignLooksChangedAt = 0; alignAnimationMisses = 0;
        boneAlignStatus = state?.Status ?? "Now pick who to align to.";
    }

    private bool IsMoverAligned(ulong? id) => id == alignMoverId ? heelsOffset != null : parkedMovers.ContainsKey(id ?? 0);

    private void SetAlignMover(ulong? id) {
        if(id == alignMoverId || BoneAlignBusy) return;
        var key = alignMoverId ?? 0;
        if(heelsOffset != null) parkedMovers[key] = CurrentMoverState();
        else parkedMovers.Remove(key);
        alignMoverId = id;
        LoadMoverState(parkedMovers.TryGetValue(id ?? 0, out var saved) ? saved : null);
        parkedMovers.Remove(id ?? 0);
    }

    /// <summary>Puts every moved GPose character back where it stood.</summary>
    private void TurnOffAllMovers() {
        var current = alignMoverId;
        TurnOffBoneAlign();
        foreach(var (key, state) in parkedMovers.ToArray()) {
            alignMoverId = key == 0 ? null : key;
            LoadMoverState(state);
            TurnOffBoneAlign();
        }
        parkedMovers.Clear();
        alignMoverId = current;
        LoadMoverState(null);
        boneAlignStatus = "All alignments turned off.";
    }
    private string boneAlignStatus = "Pick a preset, then press Align.";

    // The running alignment job.
    private enum AlignStage { None, Zeroing, Measuring }
    private AlignStage alignStage;
    private int alignWait, alignPasses, alignSettleTries;
    private Vector3? alignLastDelta;        // previous reading in this pass, to know the pose has settled
    private Vector3 alignAuto;              // what the measurement says SimpleHeels needs
    private float alignAutoRotation;        // ... and its rotation (degrees)
    private float? alignLastRotationError;
    // Learned on the spot: how SimpleHeels' rotation and offset actually move the character.
    private (float Command, float Error)? alignRotationSample;   // last rotation command and the error it left
    private float alignRotationGain;                             // facing change per degree of command (+1 or -1 expected)
    private float? alignOffsetYaw;                               // which way SimpleHeels' Forward really points
    private (Vector3 Command, Vector3 Bone)? alignMoveSample;    // offset sent and where the bone was before it
    private (Vector3 Auto, float Rotation, float Error)? alignBest; // closest result so far (used if it can't settle)
    private Vector3 alignTweak;             // the user's manual fine-tuning on top of it
    private float alignTweakRotation;
    private bool alignAutomatic;

    // The SimpleHeels temp offset we manage once aligned (null = not aligned / turned off).
    private Vector3? heelsOffset;
    private float heelsRotation;
    private bool heelsPending;
    private double heelsSentAt;

    // Watching for appearance changes.
    private ulong alignPartnerId;
    private (long Self, long Partner) alignLooks;
    private long alignLooksChangedAt;
    private long alignWatchAt;
    private (long Self, long Partner) alignPendingLooks;
    // The animation you were playing when aligned; another one resets the offsets to 0.
    private ushort? alignAnimation;
    private int alignAnimationMisses;
    private long alignAnimationCheckAt;

    private bool BoneAlignOwns(ulong id, nint address) => boneAlignSession is { } pair &&
        (pair.Self is { } own && own.Id == id && own.Address == address || pair.Partner is { } other && other.Id == id && other.Address == address);

    private bool BoneAlignBusy => alignStage != AlignStage.None || SurfaceBusy;

    // In GPose, SimpleHeels' /heels temp only changes your real (hidden) character, so the
    // GPose copy is moved directly instead: where it stood when aligning started + the offset.
    private (nint Address, Vector3 Position, float Rotation)? gposeBase;

    /// <summary>No offset at all: "/heels temp reset" (or, in GPose, back where the character stood).</summary>
    private bool ClearAlignOffset() =>
        Client.IsGPosing ? MoveGposeActor(Vector3.Zero, 0) : Commands.ProcessCommand(BoneAlignPresets.SimpleHeelsResetCommand);

    private bool SendHeelsCommand(Vector3 offset, float degrees) =>
        Client.IsGPosing ? MoveGposeActor(offset, degrees) : Commands.ProcessCommand(BoneAlignPresets.SimpleHeelsSetCommand(offset, degrees));

    // With Brio installed, GPose characters are placed through Brio's own API
    // (Brio.SetModelTransform.V3): Brio then holds the spot when the game resets it.
    private (nint Address, Vector3 Position, Quaternion Rotation)? brioBase;

    private bool TryBrioMove(IGameObject self, Vector3 offset, float degrees, float frameYaw) {
        try {
            if(brioBase is not { } start || start.Address != self.Address) {
                var (position, rotation, _) = Interface.GetIpcSubscriber<IGameObject, (Vector3?, Quaternion?, Vector3?)>("Brio.GetModelTransform.V3").InvokeFunc(self);
                if(position is not { } p || rotation is not { } r) return false;
                start = (self.Address, p, r);
                brioBase = start;
            }
            var world = start.Position + Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, frameYaw));
            var turned = Quaternion.Normalize(Quaternion.Concatenate(start.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180)));
            return Interface.GetIpcSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>("Brio.SetModelTransform.V3")
                .InvokeFunc(self, world, turned, null, false);
        } catch(Exception ex) {
            Logger.Debug(ex, "Brio isn't available for moving the GPose character.");
            brioBase = null;
            return false;
        }
    }

    private unsafe bool MoveGposeActor(Vector3 offset, float degrees) {
        var self = AlignMover();
        if(self == null) return false;
        if(gposeBase is not { } start || start.Address != self.Address) {
            start = (self.Address, self.Position, self.Rotation);
            gposeBase = start;
        }
        if(TryBrioMove(self, offset, degrees, start.Rotation)) return true;
        // Move through the game's draw offset (what SimpleHeels uses too): it isn't reset in
        // GPose, and SimpleHeels takes an outside draw offset as its base and adds its own on top.
        var drawOffset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, start.Rotation));
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)self.Address;
        native->SetDrawOffset(drawOffset.X, drawOffset.Y, drawOffset.Z);
        native->SetRotation(BoneAlignPresets.WrapDegrees((start.Rotation * 180 / MathF.PI) + degrees) * MathF.PI / 180);
        return true;
    }

    private void SendHeels(double now) {
        if(heelsOffset is not { } offset) return;
        heelsPending = false;
        heelsSentAt = now;
        if(!SendHeelsCommand(offset, heelsRotation))
            boneAlignStatus = Client.IsGPosing ? "Your GPose character isn't ready." : "SimpleHeels is needed for Auto Align. Is it installed and enabled?";
    }

    /// <summary>The user's fine-tuning, added on top of the measured alignment.</summary>
    private void SetAlignTweak(Vector3 tweak, float degrees) {
        alignTweak = tweak;
        alignTweakRotation = degrees;
        heelsOffset = alignAuto + tweak;
        heelsRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + degrees);
        heelsPending = true;
    }

    private void TurnOffBoneAlign() {
        StopAlignJob();
        alignManual = false;
        limbSnapTable = false;
        ResetLimbNudges();
        if(config.AutoLimbLock) lockFeet = lockHands = false;   // what Auto lock switched on goes off with the alignment
        heelsOffset = null;
        heelsPending = false;
        alignIsSurface = false;
        alignTweak = Vector3.Zero;
        alignTweakRotation = 0;
        if(Client.IsGPosing) {
            MoveGposeActor(Vector3.Zero, 0);   // back to where you stood
            gposeBase = null;
            brioBase = null;
        } else {
            Commands.ProcessCommand(BoneAlignPresets.SimpleHeelsResetCommand);
        }
        boneAlignStatus = "Alignment turned off.";
    }

    private void ReleaseBoneAlign() {
        var pair = boneAlignSession;
        boneAlignSession = null;
        if(pair == null) return;
        foreach(var playback in new[] { pair.Self, pair.Partner })
            if(playback != null && Objects.FirstOrDefault(playback.IsSameActor) is { } actor) playback.Release(actor);
    }

    private IGameObject? AlignPartnerObject(BoneAlignSession pair) =>
        Objects.FirstOrDefault(o => o.GameObjectId == pair.PartnerId && o.Address == pair.PartnerAddress);

    private void StopAlignJob() {
        alignStage = AlignStage.None;
        surfaceStage = SurfaceStage.None;
        ReleaseSurfaceHold();
        ReleaseBoneAlign();
    }

    /// <summary>Starts an alignment. Automatic re-runs keep the user's fine-tuning.</summary>
    private bool alignToTarget;              // this alignment uses the target, whatever it is doing

    private void BeginAlignJob(bool automatic, bool toTarget = false, ulong? pick = null) {
        if(!PositionRules.TryParseBlenderAlign(config.BoneAlignBlenderOffset, out _, out _, out _, out _)) {
            boneAlignStatus = "Pick a preset first.";
            return;
        }
        if(automatic && heelsOffset is { } current) {
            alignTweak = current - alignAuto;
            alignTweakRotation = heelsRotation - alignAutoRotation;
        } else if(!automatic) {
            alignTweak = Vector3.Zero;
            alignTweakRotation = 0;
        }
        alignAutomatic = automatic;
        alignIsSurface = false;
        alignManual = false;
        limbSnapTable = false;
        if(!automatic) {
            alignToTarget = toTarget;
            alignPickId = pick;
        }
        ReleaseAlignPlayer();
        // Measure from a clean SimpleHeels offset so nothing old is mixed into the result.
        if(!ClearAlignOffset()) {
            boneAlignStatus = Client.IsGPosing ? "Your GPose character isn't ready." : "SimpleHeels is needed for Auto Align. Is it installed and enabled?";
            return;
        }
        alignStage = AlignStage.Zeroing;
        alignWait = BoneAlignSettleFrames;
        alignPasses = 0;
        alignSettleTries = 0;
        alignLastDelta = null;
        alignAuto = Vector3.Zero;
        alignAutoRotation = 0;
        alignLastRotationError = null;
        alignRotationSample = null;
        alignRotationGain = 1;
        alignOffsetYaw = null;
        alignMoveSample = null;
        alignBest = null;
        Logger.Warning($"[BoneAlign] start ({(automatic ? "auto" : "manual")}): SimpleHeels zeroed, keeping tweak {alignTweak} rot {alignTweakRotation}");
        boneAlignStatus = automatic ? "Appearance changed - aligning again..." : "Aligning...";
    }

    private void FailAlignJob(string message) {
        Logger.Warning($"[BoneAlign] failed: {message}");
        StopAlignJob();
        boneAlignStatus = message;
        // Put back what was there before, so a failed re-run doesn't leave the player out of place.
        if(heelsOffset is { } previous) SendHeelsCommand(previous, heelsRotation);
        else ClearAlignOffset();
    }

    private void ProcessBoneAlign() {
        if(!Client.IsGPosing && (alignMoverId != null || parkedMovers.Count > 0)) {
            parkedMovers.Clear();
            alignMoverId = null;
            LoadMoverState(null);
            boneAlignStatus = "Pick a preset, then press Align.";
        }
        if(!Client.IsGPosing) { gposeBase = null; brioBase = null; }
        var requested = boneAlignRequested;
        boneAlignRequested = false;
        if(timelineWindow.IsOpen || !Client.IsLoggedIn || Conditions[ConditionFlag.BetweenAreas] ||
            Conditions[ConditionFlag.BetweenAreas51] || restartRequested || bodyRequested || nearYouRequested || nearbyRequested || targetSyncRequest.HasValue) {
            ReleaseAlignPlayer();
            if(SurfaceBusy) FailSurfaceJob("Alignment was interrupted. Try again.");
            else if(BoneAlignBusy || boneAlignSession != null) FailAlignJob(timelineWindow.IsOpen
                ? "Close Animation Timeline, then press Align again." : "Alignment was interrupted. Press Align to try again.");
            return;
        }
        var targetRequested = boneAlignTargetRequested;
        boneAlignTargetRequested = false;
        if(requested && !BoneAlignBusy) BeginAlignJob(false);
        else if(targetRequested && !BoneAlignBusy) BeginAlignJob(false, true);
        if(boneAlignPickRequested is { } pickId) {
            boneAlignPickRequested = null;
            if(!BoneAlignBusy) BeginAlignJob(false, true, pickId);
        }
        ProcessSnap();
        if(surfaceRequested) {
            surfaceRequested = false;
            if(!BoneAlignBusy && AlignMover() is { } mover && ProfileFor(mover) is { } wanted) BeginSurfaceJob(false, wanted);
        }
        if(SurfaceBusy) RunSurfaceJob();
        else if(BoneAlignBusy) RunAlignJob();
        else {
            if(heelsOffset != null) WatchAnimation();
            if(heelsOffset != null && !alignManual && config.BoneAlignAuto) { if(alignIsSurface) WatchSurfaceAppearance(); else WatchAppearance(); }
            if(heelsOffset != null && (alignIsSurface || alignManual) && !BoneAlignBusy) WatchSurfaceHeels();
            if(!BoneAlignBusy && (heelsOffset == null || alignIsSurface)) WatchSurfaceAuto();
            if(boneAlignWindow.IsOpen && !BoneAlignBusy) ProcessAlignPlayer();
            else { ReleaseAlignPlayer(); alignPlayerAvailable = false; }
        }
    }

    private void RunAlignJob() {
        if(alignWait > 0) {
            alignWait--;
            HoldAlignPair();
            return;
        }
        if(alignStage == AlignStage.Zeroing) {
            if(alignToTarget) StartTargetAlign(alignAutomatic ? alignPartnerId : alignPickId);
            else StartBoneAlign();
            if(boneAlignSession == null) { FailAlignJob(boneAlignStatus); return; }
            alignStage = AlignStage.Measuring;
            alignWait = BoneAlignSettleFrames;
            return;
        }
        if(boneAlignSession is not { } pair) { FailAlignJob("Alignment was interrupted. Press Align to try again."); return; }
        var self = AlignMover();
        var partner = AlignPartnerObject(pair);
        if(self == null || partner == null || !HoldAlignPair()) {
            FailAlignJob("The other player moved away or changed animation. Press Align to try again.");
            return;
        }
        PositionRules.TryParseBlenderAlign(config.BoneAlignBlenderOffset, out var selfBones, out var partnerBone, out var blenderOffset, out var blenderYaw);
        // Reverse: you own the preset's partner bone and the partner owns its "your" bones.
        var reverse = config.BoneAlignReverse;
        var mine = reverse ? new[] { partnerBone } : selfBones;
        var theirs = reverse ? selfBones : new[] { partnerBone };
        if(!TryBonesCenter(self, mine, out var from, out var yaw, out _) ||
            !TryBonesCenter(partner, theirs, out var to, out var partnerYaw, out _)) {
            FailAlignJob("This preset doesn't fit these characters' skeletons.");
            return;
        }
        var blenderYawRad = blenderYaw * MathF.PI / 180;
        var targetYaw = reverse ? partnerYaw - blenderYawRad : partnerYaw + blenderYawRad;
        // Facing: you should face the way Blender has you relative to your partner.
        var rotationError = PositionRules.AngleDelta(targetYaw, yaw) * 180 / MathF.PI;
        var alignPoint = reverse
            ? PositionRules.ReverseAlignTarget(to, blenderOffset, targetYaw)
            : PositionRules.AlignTarget(to, blenderOffset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, partnerYaw));
        // Only used to know the pose has stopped changing.
        var settleDelta = alignPoint - from;
        // After a race/gender change the new model and its animation load in the background.
        // Only trust a reading once two in a row agree (the pose has settled).
        if(!BoneAlignPresets.Settled(alignLastDelta, settleDelta) || alignLastRotationError is not { } lastRotation ||
            MathF.Abs(lastRotation - rotationError) > 0.05f) {
            alignLastDelta = settleDelta;
            alignLastRotationError = rotationError;
            var live = pair.Self == null || pair.Partner == null;
            if(++alignSettleTries > (live ? 15 : 60)) {
                if(!live) { FailAlignJob("The characters didn't hold still long enough to measure. Press Align to try again."); return; }
            } else {
                alignWait = BoneAlignSettleFrames;
                return;
            }
        }
        alignLastDelta = null;
        alignLastRotationError = null;
        alignSettleTries = 0;
        alignPasses++;
        if(alignPasses > 10) {
            // They kept moving (e.g. someone who can't be held still): use the closest result found.
            if(alignBest is { } best && best.Error < 0.05f) {
                alignAuto = best.Auto;
                alignAutoRotation = best.Rotation;
                FinishAlign(self, partner, pair, FormattableString.Invariant($" (within {best.Error * 1000:0} mm - they kept moving)"));
            } else {
                FailAlignJob("Couldn't line the characters up - one of them keeps moving. Press Align to try again.");
            }
            return;
        }
        // Learn how much (and which way) a rotation command really turns us.
        if(alignRotationSample is { } turn && MathF.Abs(alignAutoRotation - turn.Command) > 0.05f) {
            var gain = (turn.Error - rotationError) / (alignAutoRotation - turn.Command);
            if(float.IsFinite(gain) && MathF.Abs(gain) > 0.2f && MathF.Abs(gain) < 5f) alignRotationGain = gain;
            Logger.Warning($"[BoneAlign] rotation response {gain:0.000} (using {alignRotationGain:0.000})");
        }
        if(MathF.Abs(rotationError) > 0.1f) {
            // Turn first: turning moves every bone, so the position is measured after.
            alignRotationSample = (alignAutoRotation, rotationError);
            alignAutoRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + rotationError / alignRotationGain);
            alignMoveSample = null;
            Logger.Warning($"[BoneAlign] pass {alignPasses}: facing off by {rotationError:0.00} deg, rotation now {alignAutoRotation:0.00}");
            SendHeelsCommand(alignAuto, alignAutoRotation);
            alignWait = BoneAlignSettleFrames;
            return;
        }
        // Learn which way SimpleHeels' Forward/Left really point from how far the last offset moved us.
        if(alignMoveSample is { } move) {
            var moved = from - move.Bone;
            var commanded = move.Command;
            if(new Vector2(moved.X, moved.Z).Length() > 0.005f && new Vector2(commanded.X, commanded.Z).Length() > 0.005f) {
                alignOffsetYaw = MathF.Atan2(moved.X, moved.Z) - MathF.Atan2(commanded.X, commanded.Z);
                Logger.Warning($"[BoneAlign] offset frame {alignOffsetYaw.Value * 180 / MathF.PI:0.0} deg (facing {yaw * 180 / MathF.PI:0.0}, object {self.Rotation * 180 / MathF.PI:0.0})");
            }
        }
        var target = alignPoint;
        var offsetYaw = alignOffsetYaw ?? (Client.IsGPosing && gposeBase is { } start ? start.Rotation : self.Rotation);
        var delta = PositionRules.LocalOffset(from, target, offsetYaw);
        Logger.Warning($"[BoneAlign] pass {alignPasses}: delta {delta} (|{delta.Length():0.0000}|), total {alignAuto + delta}, facing off by {rotationError:0.00} deg");
        if(alignBest is not { } bestSoFar || delta.Length() < bestSoFar.Error) alignBest = (alignAuto, alignAutoRotation, delta.Length());
        if(delta.Length() > BoneAlignTolerance) {
            alignMoveSample = (delta, from);
            alignAuto += delta;
            SendHeelsCommand(alignAuto, alignAutoRotation);
            alignWait = BoneAlignSettleFrames;
            return;
        }
        FinishAlign(self, partner, pair, "");
    }

    private void FinishAlign(IGameObject self, IGameObject partner, BoneAlignSession pair, string note) {
        AutoLimbLock(false, false);   // partner alignments hold the feet
        heelsOffset = alignAuto + alignTweak;
        heelsRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + alignTweakRotation);
        SendHeels(0);
        alignPartnerId = partner.GameObjectId;
        alignLooks = (Looks(self), Looks(partner));
        alignAnimation = AnimationSampling.TryReadTimeline(self, out var alignedOn, out _) ? alignedOn.Timeline : null;
        alignAnimationMisses = 0;
        alignLooksChangedAt = 0;
        var name = pair.Name;
        Logger.Warning($"[BoneAlign] done with {name}{note}: measured {alignAuto}, SimpleHeels {heelsOffset} rot {heelsRotation}, looks {alignLooks}");
        StopAlignJob();
        boneAlignStatus = $"Aligned with {name}{note}.";
    }

    private bool HoldAlignPair() {
        if(boneAlignSession is not { } pair) return true;
        var self = AlignMover();
        var partner = AlignPartnerObject(pair);
        if(self == null || partner == null || pair.Territory != Client.TerritoryType || pair.Gpose != Client.IsGPosing) return false;
        if(pair.Self is { } own) {
            if(!AnimationSampling.TryReadTimeline(self, out var ownSample, out _) || !own.Valid(self, ownSample)) return false;
            own.HoldPause(CanSyncSound(self));
        }
        if(pair.Partner is { } other) {
            if(!AnimationSampling.TryReadTimeline(partner, out var otherSample, out _) || !other.Valid(partner, otherSample)) return false;
            other.HoldPause(CanSyncSound(partner));
        }
        return true;
    }

    /// <summary>Race, gender, body type, height and clan, plus the loaded model itself (draw object,
    /// skeleton, model scale), so a re-align waits until the redrawn model is really there.</summary>
    private static unsafe long Looks(IGameObject actor) {
        var character = (Character*)actor.Address;
        var customize = &character->DrawData.CustomizeData;
        var looks = BoneAlignPresets.LooksKey(customize->Race, customize->Sex, customize->BodyType, customize->Height, customize->Tribe);
        var draw = character->GameObject.DrawObject;
        long model = 0, skeleton = 0, scale = 0;
        if(draw != null && draw->Object.GetObjectType() == ObjectType.CharacterBase) {
            model = (long)draw;
            var skel = ((CharacterBase*)draw)->Skeleton;
            if(skel != null) {
                scale = (long)MathF.Round(skel->Transform.Scale.Y * 10000);
                if(skel->PartialSkeletonCount > 0 && skel->PartialSkeletons != null)
                    skeleton = (long)skel->PartialSkeletons[0].SkeletonResourceHandle;
            }
        }
        return BoneAlignPresets.ModelKey(looks, model, skeleton, scale);
    }

    /// <summary>When your animation changes (or stops), the alignment no longer fits: put the
    /// offsets back to 0 (SimpleHeels, or your GPose character back where it stood).</summary>
    private ushort? alignIgnoredAnimation;

    /// <summary>Your animation changed while aligned: pose emotes re-align, idle/walking cancels,
    /// "start" animations and anything else are ignored (see BoneAlignPresets.ClassifyAnimation).</summary>
    private const bool ResetOnAnimationChange = true;   // false: the old behaviour (re-align / switch to the new animation's surface)

    private void WatchAnimation() {
        var now = Environment.TickCount64;
        if(now < alignAnimationCheckAt) return;
        alignAnimationCheckAt = now + 250;
        var self = AlignMover();
        if(self == null) return;
        ushort? current = AnimationSampling.TryReadTimeline(self, out var sample, out _) ? sample.Timeline : null;
        if(current == alignAnimation) { alignAnimationMisses = 0; alignIgnoredAnimation = null; return; }
        // nothing readable = standing/walking (or loading for a moment): cancel after a few readings
        if(current == null) {
            // a redraw (e.g. Glamourer changing gear) hides the animation for a moment: wall alignments wait ~3 s
            if(++alignAnimationMisses < (alignIsSurface ? 12 : 3)) return;
            Logger.Warning($"[BoneAlign] animation stopped ({alignAnimation} -> none): alignment cancelled");
            var wasSurface = alignIsSurface;
            TurnOffBoneAlign();
            if(wasSurface) surfaceAutoTried = null;   // if the same animation comes back, align again
            boneAlignStatus = "You stopped the animation - alignment cancelled.";
            return;
        }
        alignAnimationMisses = 0;
        if(current == alignIgnoredAnimation) return;
        // any other animation: every offset and the heels offset go back to nothing
        if(ResetOnAnimationChange) {
            Logger.Warning($"[BoneAlign] animation changed ({alignAnimation} -> {current}): offsets reset");
            TurnOffBoneAlign();
            surfaceAutoTried = current;   // and it isn't aligned again by itself
            boneAlignStatus = "The animation changed - offsets reset.";
            return;
        }
        var key = ResolveTimelineKey(current.Value);
        if(!alignIsSurface && config.SurfaceAuto && ProfileFor(self) is { } wall) {
            // partner-aligned, but the new animation is one made for a wall/floor: switch to that
            Logger.Warning($"[Surface] animation changed to {key}: {wall.Name} (was partner-aligned)");
            alignIgnoredAnimation = null;
            surfaceAutoTried = current;
            AutoLog($"{key} -> aligning: {wall.Name} (was lined up with a partner)");
            BeginSurfaceJob(false, wall);
            return;
        }
        if(alignIsSurface) {
            // another animation with a profile: align to its surface; idle/pose without one: off
            if(ProfileFor(self) is { } profile) {
                Logger.Warning($"[Surface] animation changed to {key}: {profile.Name}");
                alignIgnoredAnimation = null;
                surfaceAutoTried = current;
                BeginSurfaceJob(true, profile);
                return;
            }
            // snapped to a table for that one animation: any other animation ends it
            if(surfaceOneOff || surfaceProfile is { } snapped && SnapRules.IsSnap(snapped.Kind) || BoneAlignPresets.ClassifyAnimation(key) != BoneAlignPresets.AnimationChange.Ignore) {
                var wasTable = surfaceOneOff || surfaceProfile is { } was && SnapRules.IsSnap(was.Kind);
                surfaceOneOff = false;
                TurnOffBoneAlign();
                surfaceAutoTried = current;
                boneAlignStatus = wasTable ? "Animation changed - no longer snapped in place." : "Animation changed - wall/floor alignment turned off.";
            } else alignIgnoredAnimation = current;
            return;
        }
        switch(BoneAlignPresets.ClassifyAnimation(key)) {
            case BoneAlignPresets.AnimationChange.Realign:
                Logger.Warning($"[BoneAlign] pose changed to {key}: re-aligning");
                alignIgnoredAnimation = null;
                BeginAlignJob(true);
                break;
            case BoneAlignPresets.AnimationChange.Cancel:
                Logger.Warning($"[BoneAlign] {key}: alignment cancelled");
                alignIgnoredAnimation = null;
                TurnOffBoneAlign();
                boneAlignStatus = "Back to idle - alignment cancelled.";
                break;
            default:
                alignIgnoredAnimation = current;   // e.g. a pose's "start": wait for what comes next
                break;
        }
    }


    private void WatchAppearance() {
        var now = Environment.TickCount64;
        if(now < alignWatchAt) return;
        alignWatchAt = now + 250;
        var self = AlignMover();
        var partner = Objects.FirstOrDefault(o => o.GameObjectId == alignPartnerId);
        if(self == null || partner == null) return;
        var looks = (Looks(self), Looks(partner));
        if(looks == alignLooks) { alignLooksChangedAt = 0; return; }
        // Wait until the new look has loaded and stayed the same for a moment.
        // Restart the wait on every further change (the model redraws after the setting changes).
        if(alignLooksChangedAt == 0 || looks != alignPendingLooks) { alignLooksChangedAt = now; alignPendingLooks = looks; return; }
        if(now - alignLooksChangedAt < 2000) return;
        Logger.Warning($"[BoneAlign] appearance changed {alignLooks} -> {looks}");
        alignLooks = looks;
        alignLooksChangedAt = 0;
        BeginAlignJob(true);
    }

    private void StartBoneAlign() {
        var self = AlignMover();
        if(self == null || !AnimationSampling.TryReadTimeline(self, out var own, out _)) {
            boneAlignStatus = "Start the animation first, then press Align.";
            return;
        }
        IGameObject? partner = null;
        AnimationSample matched = default;
        foreach(var candidate in Objects.Where(IsManualPlayer).Where(actor => actor.Address != self.Address)
            .OrderBy(actor => Vector3.DistanceSquared(actor.Position, self.Position)).ThenBy(actor => actor.GameObjectId)) {
            if(!AnimationSampling.TryReadTimeline(candidate, out var sample, out _) || !TimelineRules.Matches(own, sample, config.Distance)) continue;
            partner = candidate;
            matched = sample;
            break;
        }
        if(partner == null) { boneAlignStatus = "No one close by is playing the same animation."; return; }
        var first = new TimelinePlayback(self, own, FindTimelinePropModels(self));
        var second = new TimelinePlayback(partner, matched, FindTimelinePropModels(partner));
        boneAlignSession = new(first, second, partner.GameObjectId, partner.Address, Client.TerritoryType, Client.IsGPosing, partner.Name.TextValue);
        first.Apply(BoneAlignTime, own.LocalTime, false, first.PlayRate, 1, CanSyncSound(self));
        second.Apply(BoneAlignTime, matched.LocalTime, false, second.PlayRate, 1, CanSyncSound(partner));
    }

    /// <summary>Align to your target (or, for an automatic re-run, the same character again) whatever
    /// animation it's playing: both of you are held still on your current frames while measuring.</summary>
    private void StartTargetAlign(ulong? partnerId) {
        var self = AlignMover();
        if(self == null) { boneAlignStatus = "Your character isn't ready."; return; }
        var partner = partnerId is { } id
            ? Objects.FirstOrDefault(o => o.GameObjectId == id)
            : Client.IsGPosing ? Targets.GPoseTarget : Targets.Target;
        if(partner == null || partner.Address == self.Address) { boneAlignStatus = "Target someone first, then press Align to Target."; return; }
        TimelinePlayback? first = null, second = null;
        if(AnimationSampling.TryReadTimeline(self, out var own, out _)) {
            first = new TimelinePlayback(self, own, FindTimelinePropModels(self));
            first.Apply(own.LocalTime, own.LocalTime, false, first.PlayRate, 1, CanSyncSound(self));
        }
        if(AnimationSampling.TryReadTimeline(partner, out var theirs, out _)) {
            second = new TimelinePlayback(partner, theirs, FindTimelinePropModels(partner));
            second.Apply(theirs.LocalTime, theirs.LocalTime, false, second.PlayRate, 1, CanSyncSound(partner));
        }
        boneAlignSession = new(first, second, partner.GameObjectId, partner.Address, Client.TerritoryType, Client.IsGPosing, partner.Name.TextValue);
    }

    /// <summary>Average world position of several bones (e.g. the 4 middle lip bones = mouth centre).</summary>
    private static bool TryBonesCenter(IGameObject actor, string[] names, out Vector3 center, out float yaw, out string missing) {
        center = Vector3.Zero;
        yaw = 0;
        missing = string.Empty;
        foreach(var name in names) {
            if(!TryBonePosition(actor, name, out var point, out yaw, out _)) { missing = name; return false; }
            center += point;
        }
        center /= names.Length;
        return names.Length > 0;
    }

    private static unsafe bool TryBonePosition(IGameObject actor, string name, out Vector3 position, out float yaw, out Quaternion facing) {
        position = default;
        yaw = 0;
        facing = Quaternion.Identity;
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount > 64) return false;
        var transform = skeleton->Transform;
        Quaternion rotation = transform.Rotation;
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        yaw = MathF.Atan2(forward.X, forward.Z);
        if(!float.IsFinite(yaw)) return false;
        facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        for(var partial = 0; partial < skeleton->PartialSkeletonCount; partial++) {
            var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
            if(pose == null) pose = skeleton->PartialSkeletons[partial].GetHavokPose(1);
            if(pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Length > 4096) continue;
            var bones = pose->Skeleton->Bones;
            for(var i = 0; i < bones.Length; i++) {
                if(!string.Equals(bones[i].Name.String, name, StringComparison.Ordinal)) continue;
                var modelPose = pose->GetSyncedPoseModelSpace();
                if(modelPose == null || i >= modelPose->Length) return false;
                var point = (*modelPose)[i].Translation;
                position = (Vector3)transform.Position + Vector3.Transform(new Vector3(point.X, point.Y, point.Z) * (Vector3)transform.Scale, rotation);
                return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
            }
        }
        return false;
    }

    /// <summary>Characters in GPose you could align to, closest first.</summary>
    /// <summary>GPose people (players, their GPose copies, NPCs) - not minions, mounts or fashion accessories.</summary>
    private static bool IsGposeAlignActor(IGameObject o) =>
        SyncScopeRules.IncludesPlayer(o.ObjectIndex, true) && o is Dalamud.Game.ClientState.Objects.Types.ICharacter &&
        o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc
            or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
            or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc;

    private (IGameObject Actor, float Distance)[] GposeAlignCandidates() {
        var self = AlignMover();
        if(!Client.IsGPosing || self == null) return Array.Empty<(IGameObject, float)>();
        return Objects.Where(o => IsGposeAlignActor(o) && o.Address != self.Address)
            .Select(o => (o, Vector3.Distance(o.Position, self.Position)))
            .OrderBy(c => c.Item2).Take(12).ToArray();
    }

    private bool alignManual;   // the offsets were set by hand on the Offset tab: no alignment behind them, nothing to re-do

    /// <summary>Offsets with nothing aligned (the Offset tab): start from where you stand.</summary>
    private void StartManualOffset() {
        StopAlignJob();
        alignAuto = Vector3.Zero;
        alignAutoRotation = 0;
        alignTweak = Vector3.Zero;
        alignTweakRotation = 0;
        alignIsSurface = false;
        surfaceOneOff = false;
        alignManual = true;
        AutoLimbLock(false);
        alignAnimation = AlignMover() is { } mover && AnimationSampling.TryReadTimeline(mover, out var manualOn, out _) ? manualOn.Timeline : null;
        alignAnimationMisses = 0;
        // the offset sent to SimpleHeels replaces the shoes' own heel height, so that goes in as the starting point:
        // 0 on the sliders is then exactly how you stood
        var heel = AlignMover() is { } self && MoverIsYou(self) ? SimpleHeelsHeight() : 0;
        surfaceHeel = heel;
        surfaceHeelApplied = heel;
        alignAuto = new Vector3(0, heel, 0);
        heelsOffset = alignAuto;
        heelsRotation = 0;
        boneAlignStatus = "Offset by hand.";
    }

    /// <summary>The offsets, added on top of the current alignment (partner or object) - or, with nothing aligned
    /// (the Offset tab), on top of where you stand. A pole has its own rotation: round the pole.
    /// The feet / hand locks sit under them.</summary>
    private void DrawFineTune(float font, double now, bool options = true) {
        ImGui.Spacing();
        ImGui.TextDisabled("Offsets");
        var active = heelsOffset != null;
        var tweak = active ? alignTweak : Vector3.Zero;
        var height = tweak.Y; var forward = tweak.Z; var side = tweak.X; var rotation = active ? alignTweakRotation : 0;
        var changed = false;
        var pole = active && PoleAligned;
        DrawAxisLocks(0);
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Height", ref height, 0.0005f, -2f, 2f, "%.4f");
        DrawAxisLocks(1);
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Forward / Backward", ref forward, 0.0005f, -2f, 2f, "%.4f");
        DrawAxisLocks(2);
        ImGui.SetNextItemWidth(font * 10f);
        changed |= ImGui.DragFloat("Left / Right", ref side, 0.0005f, -2f, 2f, "%.4f");
        DrawAxisLocks(3);
        if(pole) DrawPoleTurn(font);
        else {
            ImGui.SetNextItemWidth(font * 10f);
            changed |= ImGui.DragFloat("Rotation", ref rotation, 0.1f, -180f, 180f, "%.1f deg");
        }
        DrawLimbControl(font);
        DrawTwistControl(font);
        var turnBack = false;
        ImGui.BeginDisabled(!active);
        if(ImGui.Button("Reset Offsets")) {
            height = forward = side = rotation = 0;
            changed = true;
            ResetLimbNudges();   // the feet / hands and Leg / Arm Rotations go back too
            if(pole && surfacePoleTurn != 0) { surfacePoleTurn = 0; turnBack = true; }
        }
        ImGui.EndDisabled();
        if(changed) {
            if(!active) StartManualOffset();
            SetAlignTweak(new Vector3(side, height, forward), rotation);
        }
        if(turnBack) ApplyPoleTurn();
        if(BoneAlignPresets.ShouldSend(heelsPending, now, heelsSentAt)) SendHeels(now);
        DrawLimbLocks();
        if(options && active && !alignIsSurface && !alignManual && ImGui.Checkbox("Re-align when race, gender or height changes", ref config.BoneAlignAuto)) Save();
    }

    /// <summary>The Offset tab: move yourself by hand (on top of whatever is aligned), with the feet / hand locks.</summary>
    private void DrawOffsetTab(float font) {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 22);
        ImGui.TextWrapped(heelsOffset == null ? "Move your character by hand. Nothing is aligned right now." : boneAlignStatus);
        ImGui.PopTextWrapPos();
        ImGui.BeginDisabled(BoneAlignBusy);
        DrawFineTune(font, ImGui.GetTime(), false);
        ImGui.EndDisabled();
        if(heelsOffset != null && !BoneAlignBusy) {
            ImGui.Spacing();
            if(ImGui.Button("Turn Off", new Vector2(font * 6, 0))) TurnOffBoneAlign();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Puts you back where the game has you: no offsets, no alignment.");
        }
    }

    /// <summary>The small window a command brings up: just the offsets for what was aligned, and Turn Off.</summary>
    private sealed class AlignOffsetWindow(AnimSync plugin) : Window("Align Offsets###AnimSyncAlignOffsets", ImGuiWindowFlags.AlwaysAutoResize) {
        public bool SawAligned;
        public override void Draw() {
            if(plugin.boneAlignWindow.IsOpen) { IsOpen = false; return; }   // the full window has the same sliders
            var font = ImGui.GetFontSize();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 16);
            ImGui.TextWrapped(plugin.boneAlignStatus);
            ImGui.PopTextWrapPos();
            if(plugin.BoneAlignBusy || plugin.snapPending) return;
            if(plugin.heelsOffset == null) {
                if(SawAligned) IsOpen = false;   // it ended (another animation, Turn Off): nothing left to offset
                return;
            }
            SawAligned = true;
            plugin.DrawFineTune(font, ImGui.GetTime(), false);
            ImGui.SameLine();
            if(ImGui.Button("Turn Off")) { plugin.TurnOffBoneAlign(); IsOpen = false; }
        }
    }

    private void ShowOffsetWindow() {
        if(boneAlignWindow.IsOpen) return;
        alignOffsetWindow.SawAligned = false;
        alignOffsetWindow.IsOpen = true;
    }

    /// <summary>"/animsync ass" and the like: a partner preset by name. Top / bottom sets your role (and saves it);
    /// without either, the role stays as it was last set.</summary>
    private void RequestPartner(string name, bool? top) {
        var preset = BoneAlignPresets.BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if(preset == null) return;
        config.BoneAlignBlenderOffset = preset.Line;
        if(top is { } role) config.BoneAlignReverse = role;
        Save();
        boneAlignRequested = true;
        ShowOffsetWindow();
    }

    private sealed class BoneAlignWindow(AnimSync plugin) : Window("Auto Align###AnimSyncBoneAlign", ImGuiWindowFlags.AlwaysAutoResize) {
        public override void Draw() {
            var font = ImGui.GetFontSize();
            if(!ImGui.BeginTabBar("##AlignTabs")) return;
            if(ImGui.BeginTabItem("Partner")) {
                DrawPartner(font);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Objects")) {
                plugin.DrawObjectsTab(font);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Offset")) {
                plugin.DrawOffsetTab(font);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        private void DrawPartner(float font) {
            if(ImGui.BeginChild("##BoneAlignPresets", new Vector2(font * 9, font * 14), true)) {
                foreach(var preset in BoneAlignPresets.BuiltIn) {
                    var selected = string.Equals(plugin.config.BoneAlignBlenderOffset.Trim(), preset.Line, StringComparison.Ordinal);
                    if(ImGui.Selectable(preset.Name, selected) && !selected) {
                        plugin.config.BoneAlignBlenderOffset = preset.Line;
                        plugin.Save();
                    }
                }
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginGroup();
            DrawMain(font);
            ImGui.EndGroup();
        }

        private void DrawMain(float font) {
            var now = ImGui.GetTime();
            var preset = BoneAlignPresets.BuiltIn.FirstOrDefault(p => string.Equals(plugin.config.BoneAlignBlenderOffset.Trim(), p.Line, StringComparison.Ordinal));
            var hasLine = PositionRules.TryParseBlenderAlign(plugin.config.BoneAlignBlenderOffset, out _, out _, out _, out _);
            ImGui.TextUnformatted(preset?.Name ?? (hasLine ? "Custom" : "No preset selected"));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 20);
            ImGui.TextWrapped(plugin.boneAlignStatus);
            ImGui.PopTextWrapPos();
            ImGui.BeginDisabled(plugin.BoneAlignBusy);
            ImGui.SetNextItemWidth(font * 6f);
            if(ImGui.BeginCombo("##AlignRole", plugin.config.BoneAlignReverse ? "Top" : "Bottom")) {
                foreach(var top in new[] { false, true })
                    if(ImGui.Selectable(top ? "Top" : "Bottom", plugin.config.BoneAlignReverse == top) && plugin.config.BoneAlignReverse != top) {
                        plugin.config.BoneAlignReverse = top;
                        plugin.Save();
                        if(plugin.heelsOffset != null) plugin.boneAlignStatus = "Press Align Again to use the new role.";
                    }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Your role. It stays as you set it, for every preset.");
            ImGui.BeginDisabled(plugin.BoneAlignBusy || !hasLine || plugin.timelineWindow.IsOpen);
            if(ImGui.Button(plugin.heelsOffset == null ? "Align" : "Align Again", new Vector2(font * 8, 0))) plugin.boneAlignRequested = true;
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Align to the closest player playing the same animation.");
            ImGui.SameLine();
            if(ImGui.Button("Align to Target", new Vector2(font * 8, 0))) plugin.boneAlignTargetRequested = true;
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Align to your current target, whatever animation they're doing.");
            ImGui.EndDisabled();
            if(plugin.heelsOffset != null) {
                ImGui.SameLine();
                if(ImGui.Button("Turn Off", new Vector2(font * 6, 0))) plugin.TurnOffBoneAlign();
            }
            if(Client.IsGPosing) {
                ImGui.Spacing();
                var mover = plugin.AlignMover();
                var you = ManualSelf();
                ImGui.SetNextItemWidth(font * 16);
                var moverName = mover == null ? "(gone)" : you != null && mover.Address == you.Address ? $"You ({mover.Name.TextValue})" : mover.Name.TextValue;
                ImGui.BeginDisabled(plugin.BoneAlignBusy);
                if(ImGui.BeginCombo("Move", moverName)) {
                    if(you != null && ImGui.Selectable($"You ({you.Name.TextValue}){(plugin.IsMoverAligned(null) ? "  - aligned" : "")}##AlignMoverYou",
                        plugin.alignMoverId == null)) plugin.SetAlignMover(null);
                    foreach(var actor in Objects.Where(o => IsGposeAlignActor(o) && (you == null || o.Address != you.Address)))
                        if(ImGui.Selectable($"{actor.Name.TextValue}{(plugin.IsMoverAligned(actor.GameObjectId) ? "  - aligned" : "")}##AlignMover{actor.GameObjectId}",
                            plugin.alignMoverId == actor.GameObjectId))
                            plugin.SetAlignMover(actor.GameObjectId);
                    ImGui.EndCombo();
                }
                ImGui.EndDisabled();
                if(plugin.parkedMovers.Count > 0 || plugin.heelsOffset != null) {
                    ImGui.SameLine();
                    if(ImGui.Button("Turn Off All")) plugin.TurnOffAllMovers();
                    if(ImGui.IsItemHovered()) ImGui.SetTooltip("Put every moved GPose character back where it stood.");
                }
                if(ImGui.IsItemHovered()) ImGui.SetTooltip("Which GPose character gets moved.");
                ImGui.TextDisabled("Align it to - click one:");
                var candidates = plugin.GposeAlignCandidates();
                if(candidates.Length == 0) ImGui.TextDisabled("No other actors in GPose.");
                ImGui.BeginDisabled(plugin.BoneAlignBusy || !hasLine);
                foreach(var (actor, distance) in candidates) {
                    var aligned = plugin.heelsOffset != null && actor.GameObjectId == plugin.alignPartnerId;
                    var label = FormattableString.Invariant($"{actor.Name.TextValue}  ({distance:0.0} y){(aligned ? "  - aligned" : "")}##AlignPick{actor.GameObjectId}");
                    if(ImGui.Selectable(label, aligned, ImGuiSelectableFlags.None, new Vector2(font * 20, 0)))
                        plugin.boneAlignPickRequested = actor.GameObjectId;
                }
                ImGui.EndDisabled();
            }
            if(!plugin.BoneAlignBusy) {
                ImGui.Spacing();
                plugin.DrawAlignPlayer(font * 20f);
            }
            if(plugin.heelsOffset != null && !plugin.BoneAlignBusy && !plugin.alignIsSurface) plugin.DrawFineTune(font, now);
            if(ShowCustomAlign && ImGui.CollapsingHeader("Custom")) {   // hidden: pasting a Blender align line

                ImGui.TextDisabled("Paste a line from Blender's Copy Align Offset.");
                ImGui.SetNextItemWidth(font * 20f);
                if(ImGui.InputTextWithHint("##BoneAlignBlender", "AnimSyncAlign ...", ref plugin.config.BoneAlignBlenderOffset, 256)) plugin.Save();
            }
        }
    }
}
