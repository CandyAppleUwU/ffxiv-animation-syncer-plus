using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Bindings.ImGui;

namespace AnimSync;

// Snapping the animation you're doing to something nearby, without a profile for it:
// "/animsync table" bends you over the nearest table or counter. The area map finds the table, the pose itself says
// which way it bends, and the usual surface job turns and moves the character (so fine-tune and Turn Off work).
public sealed partial class AnimSync {
    private bool snapPending;
    private SurfaceProfile? snapProfile;      // the saved profile that asked (null = the command / button)
    private SurfaceKind snapKind = SurfaceKind.Table;
    private bool surfaceOneOff;               // lined up by "/animsync wall" for this one animation: any other animation ends it
    private (Vector2 Axis, string[] Hands, float TargetYaw, float Thickness, Vector3 Stand)? surfaceLockedPole;
    private long snapDeadline;
    private long areaWantedUntil;             // keep the area map going until then, debug view or not
    private (Vector3 Point, Vector2 Inward, float Top, string What, Vector3 Stand)? surfaceLockedTable;
    private readonly Dictionary<ushort, bool> timelineLoops = new();

    private void RequestSnap(SurfaceProfile? profile, SurfaceKind kind = SurfaceKind.Table) {
        snapPending = true;
        snapProfile = profile;
        snapKind = profile?.Kind ?? kind;
        snapDeadline = Environment.TickCount64 + 10_000;
        areaWantedUntil = Environment.TickCount64 + 15_000;
        boneAlignStatus = snapKind == SurfaceKind.Pole ? "Looking for a pole..." : snapKind == SurfaceKind.Low ? "Looking for a low table or chair..." : "Looking for a table...";
    }

    private bool SnapLockValid(SurfaceKind kind) => kind == SurfaceKind.Pole ? PoleLockValid() : TableLockValid();

    private bool PoleLockValid() =>
        surfaceLockedPole is { } pole && AlignMover() is { } self && Vector3.Distance(pole.Stand, self.Position) < 0.3f;

    /// <summary>The table picked for you is still the one to use while you stand where you were when it was picked.</summary>
    private bool TableLockValid() =>
        surfaceLockedTable is { } table && AlignMover() is { } self && Vector3.Distance(table.Stand, self.Position) < 0.3f;

    private bool TimelineLoops(ushort id) {
        if(timelineLoops.TryGetValue(id, out var loops)) return loops;
        loops = Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>().TryGetRow(id, out var row) && row.IsLoop;
        timelineLoops[id] = loops;
        return loops;
    }

    private bool AreaMapFresh(IGameObject self) =>
        areaScan == null && areaMap is { } map && map.Mover == self.Address && map.Objects.Count + map.FloorOutlines.Count > 0 &&
        Vector2.Distance(new Vector2(map.Stand.X, map.Stand.Z), new Vector2(self.Position.X, self.Position.Z)) < 0.5f &&
        MathF.Abs(map.Stand.Y - self.Position.Y) < 0.25f;

    /// <summary>Every frame while a snap is waiting: for the animation's loop to begin, for the map, then for a table.</summary>
    private void ProcessSnap() {
        if(!snapPending || BoneAlignBusy) return;
        var now = Environment.TickCount64;
        string? fail = null;
        var waiting = "Start the animation first, then try again.";
        var self = AlignMover();
        if(self == null) fail = "Your character isn't ready.";
        else if(AnimationSampling.TryReadTimeline(self, out var sample, out _)) {
            var key = ResolveTimelineKey(sample.Timeline);
            if(key.Contains("start", StringComparison.OrdinalIgnoreCase)) waiting = "The animation never got past its start.";
            else if(!TimelineLoops(sample.Timeline)) fail = "This animation doesn't loop, so there's nothing to hold in place.";
            else if(!AreaMapFresh(self)) {
                areaWantedUntil = now + 5_000;
                waiting = VisualLoadingCount > 0 ? "The area's models are still loading - try again in a moment." : "Couldn't map the area around you.";
                boneAlignStatus = "Mapping the area around you...";
            } else if(!(snapKind == SurfaceKind.Pole ? TryLockPole(self, out var problem) : TryLockTable(self, snapKind == SurfaceKind.Low, out problem))) fail = problem;
            else {
                snapPending = false;
                BeginSurfaceJob(false, snapProfile ?? new SurfaceProfile(snapKind == SurfaceKind.Pole ? "Pole" : snapKind == SurfaceKind.Low ? "Low Table/Chair" : "Table", string.Empty, key, snapKind));
                return;
            }
        }
        if(fail == null && now > snapDeadline) fail = waiting;
        if(fail == null) return;
        snapPending = false;
        boneAlignStatus = fail;
        if(snapProfile == null) Chat.Print("[AnimSync] " + fail);
    }

    /// <summary>"/animsync wall": the animation you're doing, against the nearest wall, no profile needed. Which side of
    /// you goes to the wall is read from the pose (or said outright: "wall back", "wall front").</summary>
    private void RequestWall(bool? front) {
        string? fail = null;
        var self = AlignMover();
        if(BoneAlignBusy) fail = "Still aligning - try again in a moment.";
        else if(self == null) fail = "Your character isn't ready.";
        else if(!AnimationSampling.TryReadTimeline(self, out var sample, out _)) fail = "Start the animation first, then try again.";
        else {
            var key = ResolveTimelineKey(sample.Timeline);
            if(key.Contains("start", StringComparison.OrdinalIgnoreCase)) fail = "Wait for the animation to get past its start, then try again.";
            else if(!TimelineLoops(sample.Timeline)) fail = "This animation doesn't loop, so there's nothing to hold in place.";
            else if(!TryBonePosition(self, "j_kosi", out var hips, out var yaw, out _) || !TryBonePosition(self, "j_sebo_c", out var chest, out _, out _) ||
                !TryBonePosition(self, "j_kao", out var head, out _, out _)) fail = "This character's skeleton is missing a bone the wall needs.";
            else {
                var hands = new List<Vector3>();
                foreach(var bone in new[] { "j_te_l", "j_te_r" }) if(TryBonePosition(self, bone, out var hand, out _, out _)) hands.Add(hand);
                var side = front ?? SnapRules.WallFront(new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)), hips, chest, head, hands);
                snapPending = false;
                BeginSurfaceJob(false, new SurfaceProfile("Wall", string.Empty, key, side ? SurfaceKind.WallFront : SurfaceKind.WallBack));
                surfaceOneOff = true;
                return;
            }
        }
        boneAlignStatus = fail;
        Chat.Print("[AnimSync] " + fail);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The nearest table or counter (failing that, the nearest thing with a usable top), and the point on its edge nearest you.</summary>
    private bool TryLockTable(IGameObject self, bool lowOnes, out string problem) {
        problem = string.Empty;
        surfaceLockedTable = null;
        if(areaMap is not { } map) { problem = "Couldn't map the area around you."; return false; }
        var from = new Vector2(self.Position.X, self.Position.Z);
        (Vector3 Point, Vector2 Inward, float Top, string What, Vector3 Stand)? best = null;
        var bestDistance = 3f;   // within reach
        // anything with a usable top between the middle of your thigh and your head will do
        var size = SkeletonScale(self);
        var thigh = RestHeight(self, "j_asi_a_l");
        var knee = RestHeight(self, "j_asi_b_l");
        var head = RestHeight(self, "j_kao");
        var sized = thigh != null && knee != null && head != null;
        // a table: from the middle of your thigh up to your head. Something low (a bed, a couch, a bench): from
        // halfway up your shin to the middle of your thigh.
        var midThigh = sized ? (thigh!.Value + knee!.Value) / 2 * size : 0;
        var low = !sized ? 0 : lowOnes ? knee!.Value / 2 * size : midThigh;
        var high = !sized ? 0 : lowOnes ? midThigh : head!.Value * size;
        var facing = new Vector2(MathF.Sin(self.Rotation), MathF.Cos(self.Rotation));
        var seen = string.Empty;
        // first only tops with room for a body (not a rail, a chair's back or a lamp); failing that, anything
        foreach(var roomy in new[] { true, false }) {
            var bestCost = float.PositiveInfinity;
            foreach(var o in map.Objects) {
                if(sized ? !SnapRules.TableHeightFits(o.Height, low, high) : lowOnes ? o.Kind is not ("seat" or "low seat") : !SnapRules.IsLeanable(o.Kind)) continue;
                if(roomy && (o.Short < 0.3f || o.Long < 0.45f)) continue;
                if(!SnapRules.NearestEdge(o.Outlines, from, new Vector2(o.Centre.X, o.Centre.Z), out var point, out var inward, out var distance) || distance >= bestDistance) continue;
                var cost = SnapRules.PickCost(distance, facing, new Vector2(point.X, point.Z) - from);
                if(roomy) seen += FormattableString.Invariant($" [{o.Kind} {o.Height:0.00} tall {o.Long:0.00}x{o.Short:0.00}, {distance:0.00} away, cost {cost:0.00}]");
                if(cost >= bestCost) continue;
                bestCost = cost;
                best = (point, inward, o.Centre.Y, o.Seat?.Read.Type ?? o.Kind, self.Position);
            }
            if(best != null) break;
        }
        if(best == null) {
            problem = sized ? FormattableString.Invariant($"Nothing between {low:0.00} and {high:0.00} tall ({(lowOnes ? "mid-shin to mid-thigh" : "mid-thigh to head")}) within about 3 yalms of you.") : "Nothing to bend over within about 3 yalms of you.";
            return false;
        }
        surfaceSnapNote = FormattableString.Invariant($"picked {best.Value.What} at {best.Value.Point} from {self.Position}; fits between {low:0.00} and {high:0.00} tall; choices:{(seen.Length > 0 ? seen : " none with room for a body")}");
        surfaceLockedTable = best;
        return true;
    }

    /// <summary>One reading for a table: turn so the pose bends straight over the edge, hips up against it,
    /// and the body resting on the top (the feet may end up off the floor).</summary>
    private bool MeasureTable(IGameObject self, out Vector3 world, out float rotationError, out Vector3 reference, out string error) {
        world = Vector3.Zero;
        rotationError = 0;
        reference = self.Position;
        error = string.Empty;
        surfaceNote = string.Empty;
        if(surfaceLockedTable is not { } table) { error = "No table was picked. Try again."; return false; }
        if(surfaceSnapNote.Length > 0) { SurfaceLog(surfaceSnapNote); surfaceSnapNote = string.Empty; }
        if(!TryBonePosition(self, "j_kosi", out var hips, out var yaw, out _) || !TryBonePosition(self, "j_sebo_c", out var chest, out _, out _) ||
            !TryBonePosition(self, "j_kao", out var head, out _, out _)) { error = "This character's skeleton is missing a bone the table needs."; return false; }
        reference = hips;
        // which way the pose bends; standing upright, the way you face
        var bend = SnapRules.BendDirection(hips, chest, head) ?? new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        rotationError = PositionRules.AngleDelta(SurfaceRules.Yaw(table.Inward), SurfaceRules.Yaw(bend)) * 180 / MathF.PI;
        var into = new Vector3(table.Inward.X, 0, table.Inward.Y);
        float Past(Vector3 bone) => Vector3.Dot(bone - table.Point, into);
        // resting on the top: the torso, and any elbow or hand that ends up over the table
        float Lift(float slid) {
            var parts = new List<(float Y, float Gap, bool Torso, float Past)> { (chest.Y, config.SurfaceBodyThickness, true, Past(chest) + slid) };
            if(TryBonePosition(self, "j_sebo_b", out var belly, out _, out _)) parts.Add((belly.Y, config.SurfaceBodyThickness, true, Past(belly) + slid));
            foreach(var (bone, gap) in new[] { ("j_ude_b_l", config.SurfaceHandThickness + 0.01f), ("j_ude_b_r", config.SurfaceHandThickness + 0.01f),
                ("j_te_l", config.SurfaceHandThickness), ("j_te_r", config.SurfaceHandThickness) })
                if(TryBonePosition(self, bone, out var at, out _, out _)) parts.Add((at.Y, gap, false, Past(at) + slid));
            // the body goes down onto a table lower than the pose was made for, whatever that does to the lower legs
            // and feet (on a tall character they sink into the floor): only the body and the table matter here
            return SnapRules.TableLift(table.Top, parts);
        }
        // what meets the table's edge is whatever part of the legs and hips is still level with it or below once the
        // body rests on top: usually the front of the thighs. A pose lying on the table has nothing there, and its
        // hips go on the edge instead.
        var points = new List<(float Past, float Y, string What)> { (Past(hips), hips.Y, "hips") };
        var legs = string.Empty;
        foreach(var leg in new[] { "l", "r" }) {
            if(!TryBonePosition(self, "j_asi_a_" + leg, out var thigh, out _, out _) || !TryBonePosition(self, "j_asi_b_" + leg, out var knee, out _, out _)) continue;
            // the thigh only, stopping short of the knee: the knee, lower leg and foot never decide where you go
            for(var step = 0; step <= 3; step++) {
                var at = Vector3.Lerp(thigh, knee, step / 4f);
                points.Add((Past(at), at.Y, step == 0 ? "top of thigh" : "thigh"));
            }
            legs += FormattableString.Invariant($" {leg}: thigh {Past(thigh):0.000}/{thigh.Y - table.Top:0.000} knee {Past(knee):0.000}/{knee.Y - table.Top:0.000};");
        }
        var lift = Lift(-config.SurfaceBodyThickness - Past(hips));
        var slide = SnapRules.EdgeSlide(table.Top, lift, config.SurfaceBodyThickness, Past(hips), points, out var frontWhat, out var front);
        lift = Lift(slide);
        world += into * slide;
        world.Y = lift;
        SurfaceLog(FormattableString.Invariant(
            $"table ({table.What}) edge {table.Point} into {table.Inward} top {table.Top:0.000}; bend {bend}; turn {rotationError:0.00} deg; front = {frontWhat} at {front:0.000} past the edge; hips {Past(hips):0.000}/{hips.Y - table.Top:0.000};{legs} slide {slide:0.000}; lift {lift:0.000}"));
        return true;
    }

    // ---- poles ----
    private static readonly string[] PoleBodyBones = {
        "j_kao", "j_kubi", "j_sebo_c", "j_sebo_b", "j_sebo_a", "j_kosi", "j_asi_a_l", "j_asi_b_l", "j_asi_c_l", "j_asi_d_l",
        "j_asi_a_r", "j_asi_b_r", "j_asi_c_r", "j_asi_d_r", "j_ude_a_l", "j_ude_b_l", "j_te_l", "j_ude_a_r", "j_ude_b_r", "j_te_r",
    };

    /// <summary>A hand's grip: how much it looks like holding a pole, and where the pole would run through it.</summary>
    private static bool TryGrip(IGameObject self, string side, out float score, out float curl, out Vector3 centre) {
        score = 0; curl = 0; centre = default;
        if(!TryBonePosition(self, "j_te_" + side, out var wrist, out _, out _) || !TryBonePosition(self, "j_naka_a_" + side, out var knuckle, out _, out _) ||
            !TryBonePosition(self, "j_naka_b_" + side, out var joint, out _, out _)) return false;
        if(!TryBonePosition(self, "j_hito_a_" + side, out var index, out _, out _) || !TryBonePosition(self, "j_ko_a_" + side, out var pinky, out _, out _)) index = pinky = knuckle;
        score = SnapRules.GripScore(wrist, knuckle, joint, index, pinky, out curl);
        centre = SnapRules.GripCentre(wrist, knuckle, joint);
        return true;
    }

    private static bool TryGripPoint(IGameObject self, string[] hands, out Vector3 grip) {
        grip = Vector3.Zero;
        foreach(var hand in hands) {
            if(!TryGrip(self, hand, out _, out _, out var centre)) return false;
            grip += centre / hands.Length;
        }
        return hands.Length > 0;
    }

    /// <summary>Picks the hand that grips best (both, when both hold the same thing), the pole nearest to it, and the
    /// way to stand round that pole with the least of the body inside something.</summary>
    private bool TryLockPole(IGameObject self, out string problem) {
        problem = string.Empty;
        surfaceLockedPole = null;
        if(areaMap is not { } map) { problem = "Couldn't map the area around you."; return false; }
        // only that there is a pole to go to: the hand, the pole and the side are picked on the animation's first
        // frame (ChoosePole), once the job holds it there, so the choice is the same whenever you ask
        if(!map.Poles.Any(p => Vector2.Distance(new Vector2(p.Bottom.X, p.Bottom.Z), new Vector2(self.Position.X, self.Position.Z)) < 4f)) {
            problem = map.Poles.Count == 0 ? "No pole found around you." : "No pole within about 3 yalms of you.";
            return false;
        }
        surfaceLockedPole = (Vector2.Zero, Array.Empty<string>(), 0, 0, self.Position);
        surfacePoleTurn = 0;
        return true;
    }

    private bool ChoosePole(IGameObject self, out string problem) {
        problem = string.Empty;
        if(areaMap is not { } map) { problem = "Couldn't map the area around you."; return false; }
        var grips = new List<(string Side, float Score, float Curl, Vector3 Centre)>();
        foreach(var side in new[] { "l", "r" }) if(TryGrip(self, side, out var score, out var curl, out var centre)) grips.Add((side, score, curl, centre));
        if(grips.Count == 0 || !TryBonePosition(self, "j_kosi", out _, out var yaw, out _)) { problem = "This character's skeleton is missing the hand bones a pole needs."; return false; }
        grips.Sort((a, b) => b.Score.CompareTo(a.Score));
        var hands = new[] { grips[0].Side };
        // both hands on it: the other one grips too, and holds much the same upright line
        if(grips.Count > 1 && grips[1].Score >= grips[0].Score * 0.6f &&
            Vector2.Distance(new Vector2(grips[0].Centre.X, grips[0].Centre.Z), new Vector2(grips[1].Centre.X, grips[1].Centre.Z)) < 0.2f) hands = new[] { grips[0].Side, grips[1].Side };
        var grip = hands.Length == 2 ? (grips[0].Centre + grips[1].Centre) / 2 : grips[0].Centre;
        AreaPole? pole = null;
        var nearest = 3f;   // within reach
        foreach(var candidate in map.Poles) {
            var distance = Vector2.Distance(new Vector2(candidate.Bottom.X, candidate.Bottom.Z), new Vector2(grip.X, grip.Z));
            // one that doesn't reach the hand's height is a worse choice than one a little further off
            if(!candidate.GoesOn && (grip.Y < candidate.Bottom.Y - 0.1f || grip.Y > candidate.Bottom.Y + candidate.Height + 0.1f)) distance += 1;
            if(distance >= nearest) continue;
            nearest = distance;
            pole = candidate;
        }
        if(pole == null) { problem = map.Poles.Count == 0 ? "No pole found around you." : "No pole within about 3 yalms of your hand."; return false; }
        var axis = new Vector3(pole.Bottom.X, grip.Y, pole.Bottom.Z);
        // with the hand on the pole, try standing every 10 degrees round it: count the body points inside something
        var turn = 0f;
        var clipNow = 0f;
        var clipBest = 0f;
        if(areaScan == null && areaVoxels is { } voxels) {
            var body = new List<Vector3>();
            foreach(var bone in PoleBodyBones) if(TryBonePosition(self, bone, out var at, out _, out _)) body.Add(at - grip);
            var floor = self.Position.Y + 0.15f;
            bool Blocked(Vector3 spot) {
                var cell = (spot - voxels.Origin) / voxels.Cube;
                return !voxels.IsFree((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z));
            }
            var clips = new float[36];
            for(var i = 0; i < clips.Length; i++) {
                foreach(var offset in body) {
                    var at = SnapRules.TurnAbout(axis + offset, axis, i * MathF.Tau / clips.Length);
                    if(at.Y < floor || Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(axis.X, axis.Z)) < 0.12f) continue;   // the floor and the pole itself don't count
                    if(Blocked(at)) clips[i] += 1;
                    else if(Blocked(at + new Vector3(0.1f, 0, 0)) || Blocked(at - new Vector3(0.1f, 0, 0)) || Blocked(at + new Vector3(0, 0, 0.1f)) || Blocked(at - new Vector3(0, 0, 0.1f))) clips[i] += 0.25f;
                }
            }
            turn = SnapRules.BestTurn(clips);
            clipNow = clips[0];
            clipBest = clips[(int)MathF.Round((turn < 0 ? turn + MathF.Tau : turn) / MathF.Tau * clips.Length) % clips.Length];
        }
        surfaceLockedPole = (new Vector2(axis.X, axis.Z), hands, yaw + turn, pole.Thickness, self.Position);
        surfacePoleTurn = 0;
        surfacePoleNote = FormattableString.Invariant(
            $"pole {pole.Thickness:0.00} thick at {pole.Bottom}, {nearest:0.00} from the grip; hands {string.Join("+", hands)} (scores{string.Concat(grips.Select(g => FormattableString.Invariant($" {g.Side} {g.Score:0.00} curl {g.Curl:0.00}")))}); turn {turn * 180 / MathF.PI:0} deg (in the way: {clipNow:0.00} as you stood, {clipBest:0.00} after)");
        return true;
    }

    private string surfacePoleNote = string.Empty;
    private string surfaceSnapNote = string.Empty;
    private float surfacePoleTurn;            // degrees round the pole, added by the slider to the side picked

    /// <summary>The pole's rotation slider (in place of the usual rotation offset): walks you round the pole, the
    /// hand staying on it. You move when the slider is let go; the other offsets are kept.</summary>
    private void DrawPoleTurn(float font) {
        if(surfaceProfile is not { Kind: SurfaceKind.Pole } || surfaceLockedPole is not { Hands.Length: > 0 }) return;
        ImGui.SetNextItemWidth(font * 10f);
        ImGui.SliderFloat("Rotation", ref surfacePoleTurn, -180f, 180f, "%.0f deg");
        if(ImGui.IsItemActive()) DrawPoleRing();
        if(ImGui.IsItemDeactivatedAfterEdit()) ApplyPoleTurn();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Around the pole. You move when you let go of the slider.");
    }

    private bool PoleAligned => alignIsSurface && surfaceProfile is { Kind: SurfaceKind.Pole } && surfaceLockedPole is { Hands.Length: > 0 };

    private void ApplyPoleTurn() {
        if(surfaceProfile is { Kind: SurfaceKind.Pole } profile && !BoneAlignBusy) BeginSurfaceJob(true, profile);
    }

    /// <summary>While the slider is held: a ring round the pole at neck height, as wide as the neck is from the pole,
    /// with a dot where the neck is now and a bigger one where it will be on release.</summary>
    private void DrawPoleRing() {
        if(surfaceLockedPole is not { Hands.Length: > 0 } pole || AlignMover() is not { } self) return;
        if(!TryBonePosition(self, "j_kubi", out var neck, out var yaw, out _)) return;
        var axis = new Vector3(pole.Axis.X, neck.Y, pole.Axis.Y);
        var radius = MathF.Max(0.15f, Vector2.Distance(pole.Axis, new Vector2(neck.X, neck.Z)));
        var start = Vector2.Distance(pole.Axis, new Vector2(neck.X, neck.Z)) > 0.01f ? neck : axis + new Vector3(0, 0, radius);
        start = axis + Vector3.Normalize(start - axis) * radius;
        var draw = ImGui.GetBackgroundDrawList();
        var ring = Color(1, 1, 1, 0.75f);
        const int Steps = 72;
        for(var i = 0; i < Steps; i++) {
            var a = SnapRules.TurnAbout(start, axis, i * MathF.Tau / Steps);
            var b = SnapRules.TurnAbout(start, axis, (i + 1) * MathF.Tau / Steps);
            if(Screen(a, out var sa) && Screen(b, out var sb)) draw.AddLine(sa, sb, ring, 2);
        }
        if(Screen(start, out var here)) draw.AddCircleFilled(here, 5, Color(1, 1, 1, 0.9f));
        var turn = PositionRules.AngleDelta(pole.TargetYaw + surfacePoleTurn * MathF.PI / 180, yaw);
        var target = SnapRules.TurnAbout(start, axis, turn);
        if(Screen(target, out var there)) {
            draw.AddCircleFilled(there, 11, Color(0, 0, 0, 0.9f));
            draw.AddCircleFilled(there, 9, Color(1, 0.25f, 0.6f, 1));
        }
        if(Screen(axis, out var middle) && Screen(target, out there)) draw.AddLine(middle, there, Color(1, 0.25f, 0.6f, 0.8f), 2);
    }

    /// <summary>One reading for a pole: the gripping hand goes onto the pole, and the body turns to the side picked for it.
    /// The height stays the animation's own.</summary>
    private bool MeasurePole(IGameObject self, out Vector3 world, out float rotationError, out Vector3 reference, out string error) {
        world = Vector3.Zero;
        rotationError = 0;
        reference = self.Position;
        error = string.Empty;
        surfaceNote = string.Empty;
        if(surfaceLockedPole is not { } pole) { error = "No pole was picked. Try again."; return false; }
        if(pole.Hands.Length == 0) {   // first reading, on the held first frame: pick the hand, the pole and the side
            if(!ChoosePole(self, out error)) return false;
            pole = surfaceLockedPole!.Value;
        }
        if(!TryGripPoint(self, pole.Hands, out var grip) || !TryBonePosition(self, "j_kosi", out _, out var yaw, out _)) { error = "This character's skeleton is missing the hand bones a pole needs."; return false; }
        reference = grip;
        // the height stays the animation's own, plus the shoes' heel height when that's switched on (as for walls)
        var heel = SurfaceHeelsOn ? surfaceHeel : 0;
        world = new Vector3(pole.Axis.X - grip.X, heel - alignAuto.Y, pole.Axis.Y - grip.Z);
        rotationError = PositionRules.AngleDelta(pole.TargetYaw + surfacePoleTurn * MathF.PI / 180, yaw) * 180 / MathF.PI;
        if(surfacePoleNote.Length > 0) { SurfaceLog(surfacePoleNote); surfacePoleNote = string.Empty; }
        SurfaceLog(FormattableString.Invariant($"pole at {pole.Axis}; grip {grip}; move {world.X:0.000}, {world.Z:0.000}; heel {heel:0.0000} ({surfaceHeelSource}); turn {rotationError:0.00} deg"));
        return true;
    }
}
