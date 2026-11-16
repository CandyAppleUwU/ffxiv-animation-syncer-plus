using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace AnimSync;

// Surface Align: puts a character against the world for animations made for it - e.g. back and arms
// on a wall with the feet on the floor. Which animations: profiles matched on the Penumbra mod that
// replaces the animation + its ActionTimeline key. How: hold the pose at frame 0, measure the contact
// bones, raycast the real wall/floor, then turn and move the character (SimpleHeels temp offset, or
// Brio / draw offset in GPose - the same movers Bone Align uses, so fine-tune and turn-off work too).
public sealed partial class AnimSync {
    private enum SurfaceStage { None, Zeroing, Measuring }
    private SurfaceStage surfaceStage;
    private bool surfaceRequested;
    private SurfaceProfile? surfaceProfile;         // the profile being / last aligned
    private bool alignIsSurface;                    // the current alignment is a surface one (not Bone Align)
    private TimelinePlayback? surfaceHold;           // holds the mover still on frame 0 while measuring
    private ushort? surfaceAutoTried;                // animation already checked for an automatic surface align
    private long surfaceAutoCheckAt;
    private float surfaceHeel;                       // SimpleHeels heel height used in the last alignment
    private string surfaceNote = string.Empty;
    private (Vector3 Point, Vector2 Out)? surfaceLockedWall;   // the wall picked at the start of an alignment
    private string surfaceWallSupport = string.Empty;
    // After lining up on frame 0: step through the loop and keep every frame out of the wall and the floor.
    private const int SurfaceSweepFrames = 12;
    private int surfaceSweep = -1;                   // -1 = not sweeping, else the sample being measured
    private float surfaceSweepPush, surfaceSweepLift;
    private Vector3 surfaceWallOut;                  // the real wall's outward normal from the last reading
    private float surfacePush, surfaceLift;          // the last reading's wall push / floor lift
    private readonly List<string> surfaceLog = new();

    private bool SurfaceBusy => surfaceStage != SurfaceStage.None;

    // Profiles (an animation mod lined up by itself) are switched off: the Objects tab's four presets are all there is.
    // The old list was: SurfaceRules.BuiltIn.Where(p => !config.SurfaceDisabledBuiltIn.Contains(p.Name)).Concat(config.SurfaceProfiles)
    private static IEnumerable<SurfaceProfile> SurfaceProfiles() => Enumerable.Empty<SurfaceProfile>();

    private static readonly bool SurfaceProfilesOn = false;

    /// <summary>The shoes' heel height is always added (walls and poles).</summary>
    private static readonly bool SurfaceHeelsOn = true;
    /// <summary>The debug view (walls, floor, the area map): the tick box on the Objects tab.</summary>
    private bool SurfaceDebugOn => config.SurfaceDebug;

    // ---- which animation and which mod ----
    private readonly Dictionary<(nint, ushort), (string? Mod, long At)> surfaceModCache = new();

    /// <summary>The character's animation key and the Penumbra mod folder that supplies it (null = unmodded / unknown).</summary>
    private unsafe bool TryReadAnimationMod(IGameObject actor, out ushort timeline, out string key, out string? mod) {
        timeline = 0;
        key = string.Empty;
        mod = null;
        if(!AnimationSampling.TryReadTimeline(actor, out var sample, out _)) return false;
        timeline = sample.Timeline;
        key = ResolveTimelineKey(timeline);
        if(key.Length == 0) return false;
        var now = Environment.TickCount64;
        if(surfaceModCache.TryGetValue((actor.Address, timeline), out var cached) && now - cached.At < 3000) { mod = cached.Mod; return true; }
        try {
            var customize = &((Character*)actor.Address)->DrawData.CustomizeData;
            var root = Interface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
            var resolve = Interface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath");
            // the race's own file first, then the Midlander file the game falls back to
            foreach(var code in new[] { SurfaceRules.RaceCode(customize->Race, customize->Sex, customize->Tribe), "c0101" }.Distinct()) {
                if(code == null) continue;
                mod = SurfaceRules.ModFolder(resolve.InvokeFunc(SurfaceRules.AnimationPath(code, key), actor.ObjectIndex), root);
                if(mod != null) break;
            }
        } catch(Exception ex) {
            Logger.Debug(ex, "Penumbra isn't available to tell which mod plays this animation.");
        }
        if(surfaceModCache.Count > 64) surfaceModCache.Clear();
        surfaceModCache[(actor.Address, timeline)] = (mod, now);
        return true;
    }

    private SurfaceProfile? ProfileFor(IGameObject actor) =>
        SurfaceProfilesOn && TryReadAnimationMod(actor, out _, out var key, out var mod) ? SurfaceRules.Find(SurfaceProfiles(), mod, key) : null;

    // ---- the job ----
    private void BeginSurfaceJob(bool automatic, SurfaceProfile profile) {
        // a table has to be found first (that needs the area map): come back here once it is
        if(SnapRules.IsSnap(profile.Kind) && !SnapLockValid(profile.Kind)) { RequestSnap(profile); return; }
        if(!ReferenceEquals(profile, surfaceProfile)) surfaceOneOff = false;
        alignManual = false;
        if(automatic && alignIsSurface && heelsOffset is { } current) {
            alignTweak = current - alignAuto;
            alignTweakRotation = heelsRotation - alignAutoRotation;
        } else {
            alignTweak = Vector3.Zero;
            alignTweakRotation = 0;
        }
        ReleaseAlignPlayer();
        if(!ClearAlignOffset()) {
            surfaceStage = SurfaceStage.None;
            boneAlignStatus = Client.IsGPosing ? "Your GPose character isn't ready." : "SimpleHeels is needed to move your character. Is it installed and enabled?";
            return;
        }
        surfaceProfile = profile;
        surfaceStage = SurfaceStage.Zeroing;
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
        surfaceNote = string.Empty;
        surfaceLockedWall = null;
        surfaceSweep = -1;
        surfaceLog.Clear();
        Logger.Warning($"[Surface] start {profile.Name} ({profile.Kind}, {(automatic ? "auto" : "manual")})");
        boneAlignStatus = $"{profile.Name}: aligning...";
    }

    private void ReleaseSurfaceHold() {
        var hold = surfaceHold;
        surfaceHold = null;
        if(hold != null && Objects.FirstOrDefault(hold.IsSameActor) is { } actor) hold.Release(actor);
    }

    private bool HoldSurface() {
        if(surfaceHold is not { } hold) return true;
        var self = AlignMover();
        if(self == null || !AnimationSampling.TryReadTimeline(self, out var sample, out _) || !hold.Valid(self, sample)) return false;
        hold.HoldPause(CanSyncSound(self));
        return true;
    }

    private void FailSurfaceJob(string message) {
        Logger.Warning($"[Surface] failed: {message}");
        SurfaceLog("failed: " + message);
        WriteSurfaceLog();
        if(surfaceAutoRetry) {
            surfaceAutoRetry = false;
            surfaceAutoTried = null;                                  // look again in a second
            surfaceAutoCheckAt = Environment.TickCount64 + 1000;
            AutoLog("failed (" + message + ") - trying once more");
        }
        surfaceSweep = -1;
        surfaceStage = SurfaceStage.None;
        ReleaseSurfaceHold();
        boneAlignStatus = message;
        if(heelsOffset is { } previous) SendHeelsCommand(previous, heelsRotation);
        else ClearAlignOffset();
    }

    private void RunSurfaceJob() {
        if(alignWait > 0) {
            alignWait--;
            if(!HoldSurface()) FailSurfaceJob("You changed animation while it was measuring. It will try again on the next one.");
            return;
        }
        var self = AlignMover();
        if(self == null || surfaceProfile is not { } profile) { FailSurfaceJob("Your character isn't ready."); return; }
        if(surfaceStage == SurfaceStage.Zeroing) {
            if(!AnimationSampling.TryReadTimeline(self, out var own, out _)) { FailSurfaceJob("Start the animation first."); return; }
            surfaceHold = new TimelinePlayback(self, own, FindTimelinePropModels(self));
            surfaceHold.Apply(BoneAlignTime, own.LocalTime, false, surfaceHold.PlayRate, 1, CanSyncSound(self));
            surfaceHeel = MoverIsYou(self) ? SimpleHeelsHeight() : 0;
            surfaceStage = SurfaceStage.Measuring;
            alignWait = BoneAlignSettleFrames;
            return;
        }
        if(!HoldSurface()) { FailSurfaceJob("You changed animation while it was measuring. It will try again on the next one."); return; }
        if(surfaceSweep >= 0) { SweepSurface(self, profile); return; }
        if(!MeasureSurface(self, profile, out var world, out var rotationError, out var reference, out var error)) { FailSurfaceJob(error); return; }
        if(!BoneAlignPresets.Settled(alignLastDelta, world) || alignLastRotationError is not { } lastRotation ||
            MathF.Abs(lastRotation - rotationError) > 0.05f) {
            alignLastDelta = world;
            alignLastRotationError = rotationError;
            if(++alignSettleTries > 60) { FailSurfaceJob("Your character didn't hold still long enough to measure. Try again."); return; }
            alignWait = BoneAlignSettleFrames;
            return;
        }
        alignLastDelta = null;
        alignLastRotationError = null;
        alignSettleTries = 0;
        alignPasses++;
        var size = world.Length() + MathF.Abs(rotationError) * 0.005f;
        if(alignBest is not { } bestSoFar || size < bestSoFar.Error) alignBest = (alignAuto, alignAutoRotation, size);
        if(alignPasses > 12) {
            if(alignBest is { } best && best.Error < 0.05f) {
                alignAuto = best.Auto;
                alignAutoRotation = best.Rotation;
                FinishSurface(self, profile, FormattableString.Invariant($" (within {best.Error * 1000:0} mm)"));
            } else FailSurfaceJob("Couldn't get flat against the surface. Try standing closer to it and press Align to Wall.");
            return;
        }
        if(alignRotationSample is { } turn && MathF.Abs(alignAutoRotation - turn.Command) > 0.05f) {
            var gain = (turn.Error - rotationError) / (alignAutoRotation - turn.Command);
            if(float.IsFinite(gain) && MathF.Abs(gain) > 0.2f && MathF.Abs(gain) < 5f) alignRotationGain = gain;
        }
        if(MathF.Abs(rotationError) > 0.2f) {
            alignRotationSample = (alignAutoRotation, rotationError);
            alignAutoRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + rotationError / alignRotationGain);
            alignMoveSample = null;
            Logger.Warning($"[Surface] pass {alignPasses}: turned {rotationError:0.00} deg off, rotation now {alignAutoRotation:0.00}");
            SendHeelsCommand(alignAuto, alignAutoRotation);
            alignWait = BoneAlignSettleFrames;
            return;
        }
        if(alignMoveSample is { } move) {
            var moved = reference - move.Bone;
            var commanded = move.Command;
            if(new Vector2(moved.X, moved.Z).Length() > 0.005f && new Vector2(commanded.X, commanded.Z).Length() > 0.005f)
                alignOffsetYaw = MathF.Atan2(moved.X, moved.Z) - MathF.Atan2(commanded.X, commanded.Z);
        }
        var offsetYaw = alignOffsetYaw ?? (Client.IsGPosing && gposeBase is { } start ? start.Rotation : self.Rotation);
        var local = PositionRules.LocalOffset(Vector3.Zero, world, offsetYaw);
        Logger.Warning($"[Surface] pass {alignPasses}: move {world} (|{world.Length():0.0000}|), total {alignAuto + local}");
        if(world.Length() > 0.002f) {
            alignMoveSample = (local, reference);
            alignAuto += local;
            SendHeelsCommand(alignAuto, alignAutoRotation);
            alignWait = BoneAlignSettleFrames;
            return;
        }
        if(false && surfaceSweep < 0 && surfaceHold is { Duration: > 0.2f }) {   // first frame only (loop sweep off)
            surfaceSweep = 0;
            surfaceSweepPush = surfacePush;
            surfaceSweepLift = surfaceLift;
            NextSweepFrame(self);
            return;
        }
        FinishSurface(self, profile, string.Empty);
    }

    /// <summary>Holds the animation on the next sample of the loop (the pose settles before it's read).</summary>
    private void NextSweepFrame(IGameObject self) {
        if(surfaceHold is not { } hold) return;
        surfaceSweep++;
        var time = hold.Duration * surfaceSweep / (SurfaceSweepFrames + 1);
        hold.Apply(time, time, false, hold.PlayRate, 1, CanSyncSound(self));
        alignWait = BoneAlignSettleFrames + 2;
    }

    /// <summary>One sample of the loop read: remember the worst push/lift; after the last, move by it and finish.</summary>
    private void SweepSurface(IGameObject self, SurfaceProfile profile) {
        if(!MeasureSurface(self, profile, out _, out _, out _, out var error)) { FailSurfaceJob(error); return; }
        surfaceSweepPush = MathF.Max(surfaceSweepPush, surfacePush);
        surfaceSweepLift = MathF.Max(surfaceSweepLift, surfaceLift);
        SurfaceLog(FormattableString.Invariant($"sweep {surfaceSweep}: push {surfacePush:0.0000} lift {surfaceLift:0.0000}"));
        if(surfaceSweep < SurfaceSweepFrames) { NextSweepFrame(self); return; }
        // frame 0 was already exact; move only by how much worse the rest of the loop is
        var world = surfaceWallOut * MathF.Max(0, surfaceSweepPush) + new Vector3(0, MathF.Max(0, surfaceSweepLift), 0);
        if(world.Length() > 0.0005f) {
            var offsetYaw = alignOffsetYaw ?? (Client.IsGPosing && gposeBase is { } start ? start.Rotation : self.Rotation);
            alignAuto += PositionRules.LocalOffset(Vector3.Zero, world, offsetYaw);
        }
        SurfaceLog(FormattableString.Invariant($"loop needs: out {surfaceSweepPush:0.0000}, up {surfaceSweepLift:0.0000}"));
        FinishSurface(self, profile, string.Empty);
    }

    private void FinishSurface(IGameObject self, SurfaceProfile profile, string note) {
        surfaceAutoRetry = false;
        areaWantedUntil = 0;   // the map of the area has done its job: drop it
        AutoLimbLock(SnapRules.IsTop(profile.Kind), SnapRules.IsTop(profile.Kind));   // table / low table: feet and hands; wall and pole: feet only
        surfaceHeelApplied = profile.Kind is SurfaceKind.WallBack or SurfaceKind.WallFront or SurfaceKind.Pole && SurfaceHeelsOn ? surfaceHeel : 0;
        surfaceStage = SurfaceStage.None;
        ReleaseSurfaceHold();
        heelsOffset = alignAuto + alignTweak;
        heelsRotation = BoneAlignPresets.WrapDegrees(alignAutoRotation + alignTweakRotation);
        SendHeels(0);
        alignIsSurface = true;
        alignPartnerId = 0;
        alignLooks = (Looks(self), 0);
        alignLooksChangedAt = 0;
        alignAnimation = AnimationSampling.TryReadTimeline(self, out var on, out _) ? on.Timeline : null;
        surfaceAutoTried = alignAnimation;
        alignAnimationMisses = 0;
        Logger.Warning($"[Surface] done {profile.Name}{note}: {heelsOffset} rot {heelsRotation}{surfaceNote}");
        SurfaceLog($"done: offset {heelsOffset} rot {heelsRotation}");
        WriteSurfaceLog();
        boneAlignStatus = $"{profile.Name}: aligned{note}.";
    }

    /// <summary>surface_log.txt next to the plugin: the last alignment's readings, for checking the numbers.</summary>
    private void WriteSurfaceLog() {
        try {
            var dir = Interface.AssemblyLocation.DirectoryName;
            if(dir == null) return;
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "surface_log.txt"), surfaceLog);
            // every run, kept: so one that went wrong can still be read after the next
            var history = System.IO.Path.Combine(dir, "surface_log_history.txt");
            if(System.IO.File.Exists(history) && new System.IO.FileInfo(history).Length > 400_000) System.IO.File.Delete(history);
            System.IO.File.AppendAllLines(history, surfaceLog.Prepend($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {surfaceProfile?.Name} ({surfaceProfile?.Kind})"));
        } catch(Exception) { }
    }

    private void SurfaceLog(string line) {
        if(surfaceLog.Count < 200) surfaceLog.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    // ---- measuring ----
    private bool MoverIsYou(IGameObject actor) => ManualSelf() is { } you && you.Address == actor.Address;

    /// <summary>The heel height for the shoes this character has on right now - only a SimpleHeels preset made for
    /// that feet model (by model id or by file path), else the shoe model's own heels_offset attribute, else 0.
    /// (SimpleHeels' "current offset" would also give leg/top-gear presets or the character default - not shoes.)</summary>
    private unsafe float SimpleHeelsHeight() {
        try {
            var self = AlignMover();
            if(self is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player) return 0;
            var draw = ((Character*)self.Address)->GameObject.DrawObject;
            if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return 0;
            var characterBase = (CharacterBase*)draw;
            if(characterBase->GetModelType() != CharacterBase.ModelType.Human) return 0;
            var human = (Human*)characterBase;
            var feetId = human->Feet.Id;
            string? feetPath = null;
            ModelResourceHandle* feetModel = null;
            if(characterBase->Models != null && characterBase->SlotCount > 4 && characterBase->Models[4] != null) {
                feetModel = characterBase->Models[4]->ModelResourceHandle;
                if(feetModel != null) feetPath = feetModel->ResourceHandle.FileName.ToString();
            }
            surfaceShoeInfo = $"model {feetId}, {System.IO.Path.GetFileName(SurfaceRules.CleanPath(feetPath) ?? "?")}";
            var heelsJson = ReadSimpleHeelsConfig();
            if(heelsJson == null) surfaceShoeInfo += " (SimpleHeels settings not found)";
            if(SurfaceRules.ShoePreset(heelsJson, player.Name.TextValue, player.HomeWorld.RowId, feetId, feetPath) is { } preset) {
                surfaceHeelSource = "SimpleHeels shoe preset";
                return preset;
            }
            if(feetModel != null)
                foreach(var attribute in feetModel->Attributes) {
                    var text = Dalamud.Memory.MemoryHelper.ReadStringNullTerminated(new nint(attribute.Item1.Value));
                    if(SurfaceRules.ModelHeelAttribute(text) is { } value) {
                        surfaceHeelSource = "the shoe model";
                        return value * draw->Object.Scale.Y;
                    }
                }
        } catch(Exception ex) { Logger.Debug(ex, "Couldn't read the heel height for these shoes."); }
        surfaceHeelSource = "no preset for these shoes";
        return 0;
    }
    private string surfaceHeelSource = string.Empty;
    private string surfaceShoeInfo = string.Empty;
    private (DateTime Written, string? Json) simpleHeelsConfig;

    /// <summary>SimpleHeels' settings file (next to ours in pluginConfigs), re-read only when it changes.</summary>
    private string? ReadSimpleHeelsConfig() {
        try {
            var dir = Interface.ConfigFile.DirectoryName;
            if(dir == null) return null;
            var file = new System.IO.FileInfo(System.IO.Path.Combine(dir, "SimpleHeels.json"));
            if(!file.Exists) return null;
            if(simpleHeelsConfig.Written != file.LastWriteTimeUtc) simpleHeelsConfig = (file.LastWriteTimeUtc, System.IO.File.ReadAllText(file.FullName));
            return simpleHeelsConfig.Json;
        } catch(Exception) { return null; }
    }

    private static unsafe float SkeletonScale(IGameObject actor) {
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return 1;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        var scale = skeleton == null ? 1 : skeleton->Transform.Scale.Y;
        return float.IsFinite(scale) && scale > 0.1f && scale < 10 ? scale : 1;
    }

    /// <summary>How high a bone sits above the floor in the skeleton's rest pose (the model stands on y = 0).</summary>
    private static unsafe float? RestHeight(IGameObject actor, string name) {
        var draw = ((Character*)actor.Address)->GameObject.DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return null;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount < 1) return null;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if(pose == null || pose->Skeleton == null) return null;
        var havok = pose->Skeleton;
        var count = havok->Bones.Length;
        if(count > 4096 || havok->ReferencePose.Length < count || havok->ParentIndices.Length < count) return null;
        var index = -1;
        for(var i = 0; i < count; i++) if(string.Equals(havok->Bones[i].Name.String, name, StringComparison.Ordinal)) { index = i; break; }
        if(index < 0) return null;
        // walk up to the root, then compose back down
        var chain = new List<int>();
        for(var i = index; i >= 0 && chain.Count < 128; i = havok->ParentIndices[i]) chain.Add(i);
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        for(var c = chain.Count - 1; c >= 0; c--) {
            var local = havok->ReferencePose[chain[c]];
            position += Vector3.Transform(new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z), rotation);
            rotation = Quaternion.Normalize(rotation * new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W));
        }
        return float.IsFinite(position.Y) ? position.Y : null;
    }

    /// <summary>Height of the visible ground under a point (scenery/terrain models picked in visualActive; null = nothing).</summary>
    private float? FloorUnder(Vector3 at) {
        float? Down(float up, float length) =>
            VisualRay(at + new Vector3(0, up, 0), -Vector3.UnitY, length, out var ground, out var normal, out _) && normal.Y >= 0.5f ? ground.Y : null;
        // from 0.6 above: right if it lands near the foot (also catches a foot sunk into the floor)
        var near = Down(0.6f, 2.5f);
        if(near is { } y && y <= at.Y + 0.15f) return y;
        // it hit something over the feet (a bench, a step): look from just above the foot instead
        return Down(0.05f, 1.5f) ?? near ?? Down(1.5f, 4f);
    }

    private static unsafe bool Ray(Vector3 origin, Vector3 direction, float max, out Vector3 point, out Vector3 normal, out float distance) {
        point = default; normal = default; distance = float.PositiveInfinity;
        try {
            if(!BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, max)) return false;
            point = hit.Point;
            normal = SurfaceRules.FacingNormal(hit.V1, hit.V2, hit.V3, direction, hit.Normal);
            distance = Vector3.Distance(origin, point);
            return float.IsFinite(distance) && distance <= max + 0.01f && normal != Vector3.Zero;
        } catch(Exception) { return false; }
    }

    /// <summary>One reading: how far (world, yalms) and how much to turn (degrees) to sit on the surface.</summary>
    /// <summary>What the last reading saw, for the debug view (replaced whole, so the draw thread never sees half of it).</summary>
    private sealed record SurfaceView(
        IReadOnlyList<(Vector3 Point, bool Wall)> Hits, Vector3? WallPoint, Vector3 WallOut,
        IReadOnlyList<(string Bone, Vector3 At, float Gap, float Distance)> Touch,
        IReadOnlyList<(Vector3 At, float? Floor)> Feet, float Heel,
        IReadOnlyList<(Vector3 Point, bool Used)> FloorHits, FloorPlane? Floor, Vector3 FeetCentre, string Source = "");
    private volatile SurfaceView? surfaceView;

    /// <summary>One reading: how far (world, yalms) and how much to turn (degrees) to sit on the surface.
    /// forJob = false: only refresh the debug view (nothing logged).</summary>
    private bool MeasureSurface(IGameObject self, SurfaceProfile profile, out Vector3 world, out float rotationError, out Vector3 reference, out string error, bool forJob = true) {
        world = Vector3.Zero;
        rotationError = 0;
        reference = self.Position;
        error = string.Empty;
        if(SnapRules.IsTop(profile.Kind)) return MeasureTable(self, out world, out rotationError, out reference, out error);
        if(profile.Kind == SurfaceKind.Pole) return MeasurePole(self, out world, out rotationError, out reference, out error);
        var log = forJob && surfaceSweep < 0;
        var notes = new List<string>();
        var viewHits = new List<(Vector3, bool)>();
        var viewTouch = new List<(string, Vector3, float, float)>();
        var viewFeet = new List<(Vector3, float?)>();
        Vector3? viewWall = null;
        var viewSource = string.Empty;
        var viewOut = Vector3.Zero;
        // ---- the wall ----
        if(profile.Kind != SurfaceKind.Floor) {
            var contacts = profile.Kind == SurfaceKind.WallBack ? SurfaceRules.BackContacts : SurfaceRules.FrontContacts;
            var points = new List<Vector3>();
            float yaw = 0;
            foreach(var (bone, _) in contacts) {
                if(!TryBonePosition(self, bone, out var at, out yaw, out _)) { error = "This character's skeleton is missing a bone the wall needs."; return false; }
                points.Add(at);
            }
            var bulk = Vector3.Zero;
            var bulkCount = 0;
            foreach(var bone in SurfaceRules.BodyBulk) if(TryBonePosition(self, bone, out var at, out _, out _)) { bulk += at; bulkCount++; }
            if(bulkCount > 0) bulk /= bulkCount;
            var facing = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
            // out of the wall = the way you face when your back is on it, the other way when you face it
            var fallback = profile.Kind == SurfaceKind.WallBack ? facing : -facing;
            var n = SurfaceRules.FitWallNormal(points.Select(p => new Vector2(p.X, p.Z)).ToList(), new Vector2(bulk.X, bulk.Z), fallback, out _);
            var out3 = new Vector3(n.X, 0, n.Y);
            var centre = points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Count;
            reference = centre;
            // only the walls you see: the scenery models' own triangles (no collision).
            // Rays go out from where you STAND (the game never puts that inside a wall, and it doesn't move while
            // we offset you), in a full ring at three heights; the flat vertical surface most hits agree on wins -
            // a window or wall gets many, a curtain fold or a window frame's edge only a few.
            var stand = new Vector3(self.Position.X, centre.Y, self.Position.Z);
            visualActive = VisualPick(stand);
            var hits = new List<(Vector3 Point, Vector3 Normal, float Distance)>();
            Vector3 wallPoint;
            Vector2 wallOut;
            if(forJob && surfaceLockedWall is { } locked) {
                (wallPoint, wallOut) = locked;      // chosen once per alignment: no flip-flopping while it moves you
            } else {
                var ys = points.Select(p => p.Y).ToList();
                foreach(var y in new[] { ys.Min(), ys.Average(), ys.Max() }.Distinct())
                    for(var i = 0; i < 36; i++) {
                        var a = i * MathF.Tau / 36;
                        var dir = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                        var origin = new Vector3(stand.X, y, stand.Z);
                        if(VisualRay(origin, dir, 2.6f, out var p, out var hn, out var d)) hits.Add((p, hn, d));
                    }
                if(!SurfaceRules.FitWallPlane(hits, new Vector2(stand.X, stand.Z), n, out wallPoint, out wallOut, out var inliers)) {
                    foreach(var h in hits) viewHits.Add((h.Point, false));
                    var why = VisualLoadingCount > 0 ? $"wall models still loading ({VisualLoadingCount})" : $"no wall model close by ({VisualPlacedSummary})";
                    surfaceView = new SurfaceView(viewHits, null, Vector3.Zero, viewTouch, viewFeet, surfaceHeel, Array.Empty<(Vector3, bool)>(), null, self.Position, why);
                    error = VisualLoadingCount > 0 ? "The wall models are still loading - try again in a moment."
                        : "No wall close by. Stand within about 2 yalms of a wall and try again.";
                    return false;
                }
                if(forJob) surfaceLockedWall = (wallPoint, wallOut);
                surfaceWallSupport = $"{inliers}/{hits.Count} hits";
            }
            rotationError = PositionRules.AngleDelta(SurfaceRules.Yaw(wallOut), SurfaceRules.Yaw(n)) * 180 / MathF.PI;
            var wall3 = new Vector3(wallOut.X, 0, wallOut.Y);
            viewSource = $"wall model ({surfaceWallSupport}{(forJob && surfaceLockedWall != null ? ", locked" : "")}; {visualActive.Count} models near, {VisualPlacedSummary})";
            viewWall = wallPoint;
            viewOut = wall3;
            foreach(var h in hits) viewHits.Add((h.Point, MathF.Abs(Vector3.Dot(h.Point - wallPoint, wall3)) < 0.05f));
            // out until nothing is inside the wall: bones are inside the body, so each keeps the body's thickness there
            var touch = new List<(float Distance, float Gap)>();
            foreach(var (bone, gap) in SurfaceRules.WallTouchBones(config.SurfaceBodyThickness, config.SurfaceHandThickness))
                if(TryBonePosition(self, bone, out var at, out _, out _)) {
                    var distance = Vector3.Dot(at - wallPoint, wall3);
                    touch.Add((distance, gap));
                    viewTouch.Add((bone, at, gap, distance));
                }
            surfacePush = SurfaceRules.WallPush(touch);
            surfaceWallOut = wall3;
            world += wall3 * surfacePush;
            if(log)
                SurfaceLog(FormattableString.Invariant($"wall ({viewSource}) at {wallPoint} out {wall3}; turn {rotationError:0.00} deg; push {surfacePush:0.0000}; skin to wall ") +
                    string.Join(", ", viewTouch.Select(t => FormattableString.Invariant($"{t.Item1} {t.Item4 - t.Item3:0.000}"))));
        }
        // ---- the floor: like the wall, many hits around the feet fitted as one surface; then the lowest
        // ankle/toe bone just out of it, and the shoes' heel height on top ----
        var clearances = new List<float>();
        var feet = Vector3.Zero;
        var footCount = 0;
        var footBones = new List<Vector3>();
        var underFeet = new List<float>();
        var footFloor = new List<Vector3>();
        foreach(var bone in SurfaceRules.Feet) {
            if(!TryBonePosition(self, bone, out var at, out _, out _)) continue;
            feet += at; footCount++;
            footBones.Add(at);
        }
        // the floor you see: the same model detection as the wall (terrain plates + scenery around the feet)
        if(footCount > 0) visualActive = VisualPick(feet / footCount);
        foreach(var at in footBones)
            if(FloorUnder(at) is { } y) { underFeet.Add(y); footFloor.Add(new Vector3(at.X, y, at.Z)); }
        var floorHits = new List<Vector3>();
        FloorPlane? floor = null;
        var used = new List<Vector3>();
        if(footCount > 0) {
            var centre = feet / footCount;
            var low = footBones.Min(f => f.Y);
            for(var gx = -2; gx <= 2; gx++)
                for(var gz = -2; gz <= 2; gz++)
                    if(FloorUnder(new Vector3(centre.X + gx * 0.25f, low, centre.Z + gz * 0.25f)) is { } y)
                        floorHits.Add(new Vector3(centre.X + gx * 0.25f, y, centre.Z + gz * 0.25f));
            floorHits.AddRange(footFloor);
            floor = FloorPlane.Fit(floorHits, underFeet, out used);
            if(floor is { } plane)
                foreach(var at in footBones) {
                    var y = plane.HeightAt(at.X, at.Z);
                    clearances.Add(at.Y - y);
                    viewFeet.Add((at, y));
                }
            else foreach(var at in footBones) viewFeet.Add((at, null));
        }
        if(profile.Kind == SurfaceKind.Floor && footCount > 0) reference = feet / footCount;
        surfaceLift = 0;
        if(profile.Kind != SurfaceKind.Floor) {
            // keep the animation's own height, only the shoes' heel height on top (temp offset height = heel)
            world.Y = (SurfaceHeelsOn ? surfaceHeel : 0) - alignAuto.Y;
        } else if(SurfaceRules.FloorLift(clearances, SurfaceHeelsOn ? surfaceHeel : 0) is { } lift) { world.Y = lift; surfaceLift = lift; }
        else notes.Add(" No floor found under the feet.");
        if(log)
            SurfaceLog(FormattableString.Invariant($"heel {surfaceHeel:0.0000} ({surfaceHeelSource}); floor {(floor is { } f ? $"y={f.C:0.000} slope {f.A:0.000},{f.B:0.000} from {used.Count}/{floorHits.Count} hits" : "none")}; foot bones over floor {string.Join(", ", clearances.Select(c => c.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))} ({footCount - clearances.Count} without floor); lift {surfaceLift:0.0000}"));
        notes.Add(MathF.Abs(surfaceHeel) > 0.0001f ? FormattableString.Invariant($" Heels: +{surfaceHeel:0.####} ({surfaceHeelSource}).") : $" Heels: none ({surfaceHeelSource}).");
        if(forJob) surfaceNote = string.Concat(notes);
        var usedSet = new HashSet<Vector3>(used);
        surfaceView = new SurfaceView(viewHits, viewWall, viewOut, viewTouch, viewFeet, surfaceHeel,
            floorHits.Select(h => (h, usedSet.Contains(h))).ToList(), floor, footCount > 0 ? feet / footCount : self.Position, viewSource);
        return true;
    }

    // ---- debug view ----
    private long surfaceDebugAt;

    /// <summary>With the debug view on: re-read walls and floor a few times a second (not while aligning - the job reads them then).</summary>
    private void RefreshSurfaceDebug() {
        if(!SurfaceDebugOn) { surfaceView = null; return; }
        if(SurfaceBusy) return;
        var now = Environment.TickCount64;
        if(now < surfaceDebugAt) return;
        surfaceDebugAt = now + 150;
        var self = AlignMover();
        if(self == null) { surfaceView = null; return; }
        var profile = ProfileFor(self) ?? surfaceProfile ?? new SurfaceProfile("view", string.Empty, string.Empty, SurfaceKind.WallBack);
        if(SnapRules.IsSnap(profile.Kind)) profile = new SurfaceProfile("view", string.Empty, string.Empty, SurfaceKind.WallBack);   // the view shows walls and floor
        if(!SurfaceBusy) surfaceHeel = MoverIsYou(self) ? SimpleHeelsHeight() : 0;
        MeasureSurface(self, profile, out _, out _, out _, out _, false);
    }

    private static uint Color(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private static bool Screen(Vector3 world, out Vector2 screen) =>
        GameGui.WorldToScreen(world, out screen) && float.IsFinite(screen.X) && float.IsFinite(screen.Y);

    /// <summary>Draws the wall (cyan), ray hits, floor under each foot (green) and the contact bones with the body's
    /// thickness around them (green = clear, red = the skin would be inside the wall).</summary>
    private void DrawSurfaceDebug() {
        if(!SurfaceDebugOn || surfaceView is not { } view) return;
        var draw = ImGui.GetBackgroundDrawList();
        DrawAreaMap(draw);
        var black = Color(0, 0, 0, 0.8f);
        foreach(var (point, wall) in view.Hits)
            if(Screen(point, out var s)) draw.AddCircleFilled(s, 3, wall ? Color(0.2f, 0.9f, 1, 1) : Color(1, 0.8f, 0.2f, 1));
        if(view.WallPoint is { } wp) {
            var along = new Vector3(-view.WallOut.Z, 0, view.WallOut.X);
            var corners = new[] { wp - along * 0.8f, wp + along * 0.8f, wp + along * 0.8f + Vector3.UnitY * 1.6f, wp - along * 0.8f + Vector3.UnitY * 1.6f }
                .Select(c => c - Vector3.UnitY * 0.4f).ToArray();
            var screens = new Vector2[4];
            if(corners.Select((c, i) => Screen(c, out screens[i])).All(v => v)) {
                draw.AddQuadFilled(screens[0], screens[1], screens[2], screens[3], Color(0.2f, 0.9f, 1, 0.18f));
                draw.AddQuad(screens[0], screens[1], screens[2], screens[3], Color(0.2f, 0.9f, 1, 0.9f), 2);
            }
            if(Screen(wp, out var from) && Screen(wp + view.WallOut * 0.3f, out var to)) {
                draw.AddLine(from, to, black, 4);
                draw.AddLine(from, to, Color(0.2f, 0.9f, 1, 1), 2);
                if(view.Source.Length > 0) {
                    draw.AddText(to + new Vector2(5, -7), black, view.Source);
                    draw.AddText(to + new Vector2(4, -8), Color(0.2f, 0.9f, 1, 1), view.Source);
                }
            }
        }
        if(view.Floor is { } plane && areaMap == null) {   // the area map draws the real floor once it's built
            var c = view.FeetCentre;
            Vector3 P(float dx, float dz) => new(c.X + dx, plane.HeightAt(c.X + dx, c.Z + dz), c.Z + dz);
            var corners = new[] { P(-0.6f, -0.6f), P(0.6f, -0.6f), P(0.6f, 0.6f), P(-0.6f, 0.6f) };
            var screens = new Vector2[4];
            if(corners.Select((corner, i) => Screen(corner, out screens[i])).All(v => v)) {
                draw.AddQuadFilled(screens[0], screens[1], screens[2], screens[3], Color(0.3f, 1, 0.3f, 0.18f));
                draw.AddQuad(screens[0], screens[1], screens[2], screens[3], Color(0.3f, 1, 0.3f, 0.9f), 2);
            }
        }
        foreach(var (point, on) in view.FloorHits)
            if(Screen(point, out var s)) draw.AddCircleFilled(s, 3, on ? Color(0.3f, 1, 0.3f, 1) : Color(1, 0.8f, 0.2f, 1));
        foreach(var (at, floor) in view.Feet) {
            if(floor is not { } y) {
                if(Screen(at, out var lost)) draw.AddText(lost, Color(1, 0.3f, 0.3f, 1), "no floor");
                continue;
            }
            var spot = new Vector3(at.X, y, at.Z);
            if(Screen(at, out var bone) && Screen(spot, out var ground)) {
                draw.AddLine(bone, ground, Color(0.3f, 1, 0.3f, 0.9f), 1.5f);
                draw.AddCircle(ground, 6, Color(0.3f, 1, 0.3f, 1), 12, 2);
                if(view.Heel > 0.0001f && Screen(spot + Vector3.UnitY * view.Heel, out var heel)) draw.AddCircle(heel, 4, Color(1, 0.4f, 1, 1), 12, 2);
            }
        }
        foreach(var (name, at, gap, distance) in view.Touch) {
            if(!Screen(at, out var centre)) continue;
            // the body's thickness around the bone, drawn to scale
            var radius = Screen(at + view.WallOut * gap, out var edge) ? MathF.Max(3, Vector2.Distance(centre, edge)) : 4;
            var inside = distance < gap - 0.002f;
            var color = inside ? Color(1, 0.25f, 0.25f, 1) : Color(0.3f, 1, 0.3f, 1);
            draw.AddCircle(centre, radius, black, 16, 3);
            draw.AddCircle(centre, radius, color, 16, 1.5f);
            if(inside) draw.AddText(centre + new Vector2(radius + 2, -6), color,
                FormattableString.Invariant($"{name} {(gap - distance) * 100:0.0} cm in"));
        }
    }

    // ---- watching ----
    /// <summary>Not aligned (or surface-aligned): when your animation turns into one with a profile, align to it.</summary>
    private void WatchSurfaceAuto() {
        if(!SurfaceProfilesOn || !config.SurfaceAuto) return;   // profiles are off: nothing to watch for
        var now = Environment.TickCount64;
        if(now < surfaceAutoCheckAt) return;
        surfaceAutoCheckAt = now + 300;
        var self = AlignMover();
        if(self == null || !TryReadAnimationMod(self, out var timeline, out var key, out var mod)) { surfaceAutoTried = null; return; }
        if(timeline == surfaceAutoTried) return;
        surfaceAutoTried = timeline;
        var profile = SurfaceRules.Find(SurfaceProfiles(), mod, key);
        AutoLog($"{key}  mod: {mod ?? "(none)"}  -> {(profile != null ? "aligning: " + profile.Name : "no profile")}");
        if(profile != null) { surfaceAutoRetry = true; BeginSurfaceJob(false, profile); }
    }

    // automatic: one retry after a failed attempt (e.g. the pose was still loading)
    private bool surfaceAutoRetry;
    private readonly List<string> surfaceAutoLog = new();

    /// <summary>surface_auto_log.txt next to the plugin: what "Do it automatically" saw each time your animation changed.</summary>
    private void AutoLog(string line) {
        surfaceAutoLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if(surfaceAutoLog.Count > 60) surfaceAutoLog.RemoveAt(0);
        try {
            var dir = Interface.AssemblyLocation.DirectoryName;
            if(dir != null) System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "surface_auto_log.txt"), surfaceAutoLog);
        } catch(Exception) { }
    }

    private long surfaceHeelCheckAt;
    private float surfaceHeelApplied;             // the heel height inside the current wall alignment

    /// <summary>Wall-aligned: when the shoes change (or their SimpleHeels preset is edited), swap the old heel height
    /// for the new one right away - only the height moves, no re-align needed.</summary>
    private void WatchSurfaceHeels() {
        var now = Environment.TickCount64;
        if(now < surfaceHeelCheckAt || heelsOffset is not { } current || !alignManual && surfaceProfile?.Kind is SurfaceKind.Floor or SurfaceKind.Table or SurfaceKind.Low) return;
        surfaceHeelCheckAt = now + 500;
        var self = AlignMover();
        if(self == null) return;
        var applied = surfaceHeelApplied;
        var heel = MoverIsYou(self) ? SimpleHeelsHeight() : 0;
        var wanted = SurfaceHeelsOn ? heel : 0;
        if(MathF.Abs(wanted - applied) < 0.0005f) return;
        Logger.Warning($"[Surface] heel height {applied:0.####} -> {wanted:0.####} ({surfaceHeelSource})");
        AutoLog(FormattableString.Invariant($"shoes changed ({surfaceShoeInfo}): heel {applied:0.####} -> {wanted:0.####} ({surfaceHeelSource})"));
        surfaceHeel = heel;
        surfaceHeelApplied = wanted;
        alignAuto.Y += wanted - applied;
        heelsOffset = current + new Vector3(0, wanted - applied, 0);
        heelsPending = false;
        SendHeels(now);
        boneAlignStatus = FormattableString.Invariant($"Shoes changed - heel height now {(wanted > 0.0001f ? $"+{wanted:0.####}" : "none")} ({surfaceHeelSource}).");
    }

    private void WatchSurfaceAppearance() {
        var now = Environment.TickCount64;
        if(now < alignWatchAt) return;
        alignWatchAt = now + 250;
        var self = AlignMover();
        if(self == null || surfaceProfile is not { } profile) return;
        var looks = (Looks(self), 0L);
        if(looks == alignLooks) { alignLooksChangedAt = 0; return; }
        if(alignLooksChangedAt == 0 || looks != alignPendingLooks) { alignLooksChangedAt = now; alignPendingLooks = looks; return; }
        if(now - alignLooksChangedAt < 2000) return;
        alignLooks = looks;
        alignLooksChangedAt = 0;
        BeginSurfaceJob(true, profile);
    }

    // ---- window ----
    private string surfaceNewName = string.Empty;
    private long surfaceShoeShownAt;
    private float surfaceShoeHeel;
    private string surfaceNewNameFor = string.Empty;
    private int surfaceNewKind;

    /// <summary>The old Walls &amp; Floor tab (profiles, debug view, adjustments). Not shown any more - see DrawObjectsTab.</summary>
    private void DrawSurfaceTabOld(float font) {
        var self = AlignMover();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 20);
        ImGui.TextWrapped(boneAlignStatus);
        ImGui.PopTextWrapPos();
        if(Client.IsGPosing && self != null) ImGui.TextDisabled($"Moving: {self.Name.TextValue} (pick who in the Partner tab)");
        string key = string.Empty;
        string? mod = null;
        var playing = self != null && TryReadAnimationMod(self, out _, out key, out mod);
        var match = playing ? SurfaceRules.Find(SurfaceProfiles(), mod, key) : null;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 20);
        ImGui.TextDisabled(playing ? $"Animation: {key}" : "No animation playing.");
        if(playing) ImGui.TextDisabled($"Mod: {mod ?? "(none / game animation)"}");
        ImGui.TextWrapped(match != null ? $"Profile: {match.Name} - {KindName(match.Kind)}" : "No profile for this animation.");
        ImGui.PopTextWrapPos();
        ImGui.BeginDisabled(BoneAlignBusy || match == null || timelineWindow.IsOpen);
        if(ImGui.Button(match?.Kind == SurfaceKind.Floor ? "Align to Floor" : match?.Kind == SurfaceKind.Table ? "Bend over Table" : match?.Kind == SurfaceKind.Low ? "Low Table/Chair" : match?.Kind == SurfaceKind.Pole ? "Grab Pole" : "Align to Wall", new Vector2(font * 8, 0))) surfaceRequested = true;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(BoneAlignBusy || timelineWindow.IsOpen);
        if(ImGui.Button("Nearest Table", new Vector2(font * 7, 0))) RequestSnap(null);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Bend over the nearest table or counter with the animation you're doing now\n(same as /animsync table). No profile needed.");
        ImGui.SameLine();
        ImGui.BeginDisabled(BoneAlignBusy || timelineWindow.IsOpen);
        if(ImGui.Button("Low Table/Chair", new Vector2(font * 7, 0))) RequestSnap(null, SurfaceKind.Low);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Like Nearest Table, for lower things: a bed, a couch, a bench\n(from halfway up your shin to the middle of your thigh). Same as /animsync low or /animsync lowtable.");
        ImGui.BeginDisabled(BoneAlignBusy || timelineWindow.IsOpen);
        if(ImGui.Button("Nearest Wall", new Vector2(font * 7, 0))) RequestWall(null);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Put the animation you're doing now against the nearest wall (same as /animsync wall).\nWhich side of you goes to the wall is read from the pose; \"/animsync wall back\" or \"wall front\" says it outright.");
        ImGui.SameLine();
        ImGui.BeginDisabled(BoneAlignBusy || timelineWindow.IsOpen);
        if(ImGui.Button("Nearest Pole", new Vector2(font * 7, 0))) RequestSnap(null, SurfaceKind.Pole);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Put the gripping hand of the animation you're doing now on the nearest pole,\nstanding where the least of you is inside something (same as /animsync pole).");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Put your character against the surface this animation was made for.");
        if(alignIsSurface && heelsOffset != null) {
            ImGui.SameLine();
            if(ImGui.Button("Turn Off", new Vector2(font * 6, 0))) TurnOffBoneAlign();
        }
        if(ImGui.Checkbox("Do it automatically", ref config.SurfaceAuto)) Save();
        if(ImGui.Checkbox("Add the shoes' heel height", ref config.SurfaceAddHeels)) Save();
        if(self != null && MoverIsYou(self)) {
            var now = Environment.TickCount64;
            if(now >= surfaceShoeShownAt) { surfaceShoeShownAt = now + 500; surfaceShoeHeel = SimpleHeelsHeight(); }
            ImGui.TextDisabled(FormattableString.Invariant($"Shoes: {surfaceShoeInfo} -> {(surfaceShoeHeel > 0.0001f ? $"+{surfaceShoeHeel:0.####}" : "0")} ({surfaceHeelSource})"));
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Off: no heel height at all (height stays exactly as the animation has it).");
        if(ImGui.Checkbox("Show walls & floor (debug)", ref config.SurfaceDebug)) Save();
        if(SurfaceDebugOn) {
            if(ImGui.Checkbox("Show map points", ref config.SurfaceMapPoints)) { areaMap = null; Save(); }
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("A dot for every surface point the map found (green = floor, yellow to red = higher).");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 22);
            if(areaMap is { } area) ImGui.TextWrapped("You're on: " + area.StandingOn);
            if(surfaceShapeHead > 0.2f) {
                var seat = ShapeRules.SeatRange(surfaceSizeScale);
                ImGui.TextWrapped(FormattableString.Invariant($"Standing, your head is {surfaceShapeHead:0.00} up: seats for you are {seat.Low:0.00}-{seat.High:0.00} above the floor."));
            }
            ImGui.PopTextWrapPos();
            var adjust = config.SurfaceSizeAdjust;
            ImGui.SetNextItemWidth(font * 10f);
            if(ImGui.DragFloat("Size adjustment", ref adjust, 0.01f, 0.5f, 2f, "%.2f")) { config.SurfaceSizeAdjust = Math.Clamp(adjust, 0.5f, 2f); areaMap = null; Save(); }
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Moves the seat / table / counter heights up or down for your character.\nRaise it if seats show as tables, lower it if tables show as seats.");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 22);
            ImGui.TextDisabled(AreaMapCost());
            ImGui.PopTextWrapPos();
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Draws the wall it finds (cyan), the real floor's outline (green, pink = heel height),\n" +
            "everything on it - seats, tables, counters, steps - with its true outline (orange, nearest edge yellow),\n" +
            "and the body's thickness around the back/arm/hand bones (red = that part would be inside the wall).");
        ImGui.TextDisabled("Bones are inside the body - how far is the skin?");
        var body = config.SurfaceBodyThickness * 100;
        var hand = config.SurfaceHandThickness * 100;
        ImGui.SetNextItemWidth(font * 10f);
        if(ImGui.DragFloat("Back / hips (cm)", ref body, 0.1f, 0, 40, "%.1f")) { config.SurfaceBodyThickness = body / 100; Save(); }
        ImGui.SetNextItemWidth(font * 10f);
        if(ImGui.DragFloat("Arms / hands (cm)", ref hand, 0.1f, 0, 20, "%.1f")) { config.SurfaceHandThickness = hand / 100; Save(); }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Turn on the debug view and grow these until the circles reach the skin.\nThen press Align to Wall again.");
        if(alignIsSurface && heelsOffset != null && !BoneAlignBusy) DrawFineTune(font, ImGui.GetTime());
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Align as soon as you start an animation that has a profile.");
        if(playing) {
            ImGui.Spacing();
            ImGui.TextDisabled("Save a profile for this animation:");
            // a new animation/mod gets a fresh suggested name
            var suggestFor = $"{mod}|{key}";
            if(surfaceNewName.Length == 0 || surfaceNewNameFor != suggestFor) {
                surfaceNewName = mod != null ? $"{mod} ({key.Split('/').Last()})" : key;
                surfaceNewNameFor = suggestFor;
            }
            ImGui.SetNextItemWidth(font * 20f);
            ImGui.InputText("##SurfaceName", ref surfaceNewName, 128);
            ImGui.SetNextItemWidth(font * 10f);
            if(ImGui.BeginCombo("##SurfaceKind", KindName((SurfaceKind)surfaceNewKind))) {
                foreach(var kind in Enum.GetValues<SurfaceKind>())
                    if(ImGui.Selectable(KindName(kind), (int)kind == surfaceNewKind)) surfaceNewKind = (int)kind;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if(ImGui.Button("Save Profile")) {
                config.SurfaceProfiles.RemoveAll(p => SurfaceRules.SameKey(p.Animation, key) && string.Equals(p.Mod, mod ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                config.SurfaceProfiles.Add(new SurfaceProfile(surfaceNewName.Trim().Length > 0 ? surfaceNewName.Trim() : key, mod ?? string.Empty, key, (SurfaceKind)surfaceNewKind));
                surfaceNewName = string.Empty;
                surfaceAutoTried = null;
                Save();
            }
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(mod != null ? "Only when this mod plays this animation." : "For this animation, with or without a mod.");
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Profiles");
        foreach(var builtIn in SurfaceRules.BuiltIn) {
            var on = !config.SurfaceDisabledBuiltIn.Contains(builtIn.Name);
            if(ImGui.Checkbox($"{builtIn.Name}  ({KindName(builtIn.Kind)})##SurfaceBuiltIn{builtIn.Name}", ref on)) {
                if(on) config.SurfaceDisabledBuiltIn.Remove(builtIn.Name); else config.SurfaceDisabledBuiltIn.Add(builtIn.Name);
                Save();
            }
        }
        for(var i = 0; i < config.SurfaceProfiles.Count; i++) {
            var profile = config.SurfaceProfiles[i];
            if(ImGui.Checkbox($"{profile.Name}  ({KindName(profile.Kind)})##SurfaceProfile{i}", ref profile.Enabled)) Save();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{profile.Animation}\n{(profile.Mod.Length > 0 ? profile.Mod : "any mod")}");
            ImGui.SameLine();
            if(ImGui.SmallButton($"Remove##SurfaceRemove{i}")) { config.SurfaceProfiles.RemoveAt(i); Save(); break; }
        }
    }

    private static string KindName(SurfaceKind kind) => kind switch {
        SurfaceKind.WallBack => "back to the wall",
        SurfaceKind.WallFront => "facing the wall",
        SurfaceKind.Table => "bent over a table",
        SurfaceKind.Pole => "holding a pole",
        SurfaceKind.Low => "Low Table/Chair",
        _ => "floor only",
    };
}
