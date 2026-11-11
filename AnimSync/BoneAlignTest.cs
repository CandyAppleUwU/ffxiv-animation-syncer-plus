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
        (nint Address, Vector3 Position, Quaternion Rotation)? BrioBase, string Status);
    private readonly System.Collections.Generic.Dictionary<ulong, MoverState> parkedMovers = new();

    private MoverState CurrentMoverState() => new(heelsOffset, heelsRotation, alignAuto, alignAutoRotation, alignTweak,
        alignTweakRotation, alignPartnerId, alignLooks, alignAnimation, alignToTarget, alignPickId, gposeBase, brioBase, boneAlignStatus);

    private void LoadMoverState(MoverState? state) {
        heelsOffset = state?.HeelsOffset; heelsRotation = state?.HeelsRotation ?? 0;
        alignAuto = state?.Auto ?? Vector3.Zero; alignAutoRotation = state?.AutoRotation ?? 0;
        alignTweak = state?.Tweak ?? Vector3.Zero; alignTweakRotation = state?.TweakRotation ?? 0;
        alignPartnerId = state?.PartnerId ?? 0; alignLooks = state?.Looks ?? default; alignAnimation = state?.Animation;
        alignToTarget = state?.ToTarget ?? false; alignPickId = state?.PickId;
        gposeBase = state?.GposeBase; brioBase = state?.BrioBase;
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

    private bool BoneAlignBusy => alignStage != AlignStage.None;

    // In GPose, SimpleHeels' /heels temp only changes your real (hidden) character, so the
    // GPose copy is moved directly instead: where it stood when aligning started + the offset.
    private (nint Address, Vector3 Position, float Rotation)? gposeBase;

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
            boneAlignStatus = Client.IsGPosing ? "Your GPose character isn't ready." : "SimpleHeels is needed for Bone Align. Is it installed and enabled?";
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
        heelsOffset = null;
        heelsPending = false;
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
        if(!automatic) {
            alignToTarget = toTarget;
            alignPickId = pick;
        }
        ReleaseAlignPlayer();
        // Measure from a clean SimpleHeels offset so nothing old is mixed into the result.
        if(!SendHeelsCommand(Vector3.Zero, 0)) {
            boneAlignStatus = Client.IsGPosing ? "Your GPose character isn't ready." : "SimpleHeels is needed for Bone Align. Is it installed and enabled?";
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
        Logger.Warning($"[BoneAlign] start ({(automatic ? "auto" : "manual")}): SimpleHeels zeroed, keeping tweak {alignTweak} rot {alignTweakRotation}");
        boneAlignStatus = automatic ? "Appearance changed - aligning again..." : "Aligning...";
    }

    private void FailAlignJob(string message) {
        Logger.Warning($"[BoneAlign] failed: {message}");
        StopAlignJob();
        boneAlignStatus = message;
        // Put back what was there before, so a failed re-run doesn't leave the player out of place.
        if(heelsOffset is { } previous) SendHeelsCommand(previous, heelsRotation);
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
            if(BoneAlignBusy || boneAlignSession != null) FailAlignJob(timelineWindow.IsOpen
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
        if(BoneAlignBusy) RunAlignJob();
        else {
            if(heelsOffset != null) WatchAnimation();
            if(heelsOffset != null && config.BoneAlignAuto) WatchAppearance();
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
        if(alignPasses > 10) { FailAlignJob("Couldn't line the characters up. Press Align to try again."); return; }
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
        if(delta.Length() > BoneAlignTolerance) {
            alignMoveSample = (delta, from);
            alignAuto += delta;
            SendHeelsCommand(alignAuto, alignAutoRotation);
            alignWait = BoneAlignSettleFrames;
            return;
        }
        heelsOffset = alignAuto + alignTweak;
        heelsRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + alignTweakRotation);
        SendHeels(0);
        alignPartnerId = partner.GameObjectId;
        alignLooks = (Looks(self), Looks(partner));
        alignAnimation = AnimationSampling.TryReadTimeline(self, out var alignedOn, out _) ? alignedOn.Timeline : null;
        alignAnimationMisses = 0;
        alignLooksChangedAt = 0;
        var name = pair.Name;
        Logger.Warning($"[BoneAlign] done with {name}: measured {alignAuto}, SimpleHeels {heelsOffset} rot {heelsRotation}, looks {alignLooks}");
        StopAlignJob();
        boneAlignStatus = $"Aligned with {name}.";
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
    private void WatchAnimation() {
        var now = Environment.TickCount64;
        if(now < alignAnimationCheckAt) return;
        alignAnimationCheckAt = now + 250;
        var self = AlignMover();
        if(self == null) return;
        ushort? current = AnimationSampling.TryReadTimeline(self, out var sample, out _) ? sample.Timeline : null;
        if(current == alignAnimation) { alignAnimationMisses = 0; return; }
        // a stopped animation can read as nothing for a moment while loading: wait for a second reading
        if(current == null && ++alignAnimationMisses < 3) return;
        Logger.Warning($"[BoneAlign] animation changed {alignAnimation} -> {current}: offsets reset to 0");
        TurnOffBoneAlign();
        boneAlignStatus = "Animation changed - alignment reset. Press Align for the new one.";
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

    private sealed class BoneAlignWindow(AnimSync plugin) : Window("Bone Align###AnimSyncBoneAlign", ImGuiWindowFlags.AlwaysAutoResize) {
        public override void Draw() {
            var font = ImGui.GetFontSize();
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
            if(ImGui.Checkbox("Reverse", ref plugin.config.BoneAlignReverse)) {
                plugin.Save();
                if(plugin.heelsOffset != null) plugin.boneAlignStatus = "Press Align Again to use the new direction.";
            }
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Swap who moves to whom: you bring your partner's part of the preset to theirs.");
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
            if(plugin.heelsOffset != null && !plugin.BoneAlignBusy) {
                ImGui.Spacing();
                ImGui.TextDisabled("Fine-tune (added on top of the alignment)");
                var tweak = plugin.alignTweak;
                var height = tweak.Y; var forward = tweak.Z; var side = tweak.X; var rotation = plugin.alignTweakRotation;
                var changed = false;
                ImGui.SetNextItemWidth(font * 10f);
                changed |= ImGui.DragFloat("Height", ref height, 0.0005f, -2f, 2f, "%.4f");
                ImGui.SetNextItemWidth(font * 10f);
                changed |= ImGui.DragFloat("Forward / Backward", ref forward, 0.0005f, -2f, 2f, "%.4f");
                ImGui.SetNextItemWidth(font * 10f);
                changed |= ImGui.DragFloat("Left / Right", ref side, 0.0005f, -2f, 2f, "%.4f");
                ImGui.SetNextItemWidth(font * 10f);
                changed |= ImGui.DragFloat("Rotation", ref rotation, 0.1f, -180f, 180f, "%.1f deg");
                if(ImGui.Button("Reset Fine-tune")) {
                    height = forward = side = rotation = 0;
                    changed = true;
                }
                if(changed) plugin.SetAlignTweak(new Vector3(side, height, forward), rotation);
                if(BoneAlignPresets.ShouldSend(plugin.heelsPending, now, plugin.heelsSentAt)) plugin.SendHeels(now);
                if(ImGui.Checkbox("Re-align when race, gender or height changes", ref plugin.config.BoneAlignAuto)) plugin.Save();
            }
            if(ImGui.CollapsingHeader("Custom")) {
                ImGui.TextDisabled("Paste a line from Blender's Copy Align Offset.");
                ImGui.SetNextItemWidth(font * 20f);
                if(ImGui.InputTextWithHint("##BoneAlignBlender", "AnimSyncAlign ...", ref plugin.config.BoneAlignBlenderOffset, 256)) plugin.Save();
            }
        }
    }
}
