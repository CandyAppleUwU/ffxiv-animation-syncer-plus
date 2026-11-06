using AnimSync;
using System.Numerics;

static AnimationSample Player(ulong id, float x, int frame, ushort timeline = 100) =>
    new(id, $"Player {id}", new Vector3(x, 0, 0), timeline, frame, 200, 6.63333f, 1);
static void Check(bool condition, string name) {
    if(!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}
static List<List<AnimationSample>> Groups(params AnimationSample[] players) => NearbyGroups.Find(players, 0.6f);

Check(Groups(Player(1, 0, 10)).Count == 0, "A single player never restarts");
Check(Groups(Player(1, 0, 10), Player(2, 0.6f, 11)).Single().Count == 2, "Exactly 0.60 yalms is included");
Check(Groups(Player(1, 0, 10), Player(2, 0.601f, 11)).Count == 0, "Outside the distance is excluded");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 10)).Count == 0, "Equal frames never restart");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11, 101)).Single().Count == 2, "Different animations with matching timing sync together");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11, 101) with { FrameCount = 300 }).Count == 0, "Different animations with different frame counts are excluded");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11, 101) with { Duration = 10 }).Count == 0, "Different animations with different durations are excluded");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11), Player(3, 0.4f, 12)).Single().Count == 3, "Three nearby players restart together");
Check(Groups(Player(1, 0, 10), Player(2, 0.5f, 11), Player(3, 1f, 12)).Single().All(p => p.Id != 3), "Chains cannot extend a group beyond its distance");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11), Player(3, 10, 5), Player(4, 10.2f, 6)).Count == 2, "Separate nearby groups stay separate");
Check(NearbyGroups.Find(new[] { Player(1, 0, 10), Player(2, 0.2f, 11) }, 0.6f).Count == 1, "Even a one-frame difference always triggers sync");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11) with { Speed = 2 }).Count == 0, "Different speeds cannot cause recurring synchronization");
Check(Groups(Player(1, 0, 10), Player(2, 0.2f, 11) with { FrameCount = 300 }).Count == 0, "Different clip lengths are excluded");
Check(Groups(Player(1, 0, 0), Player(2, 0.2f, 199)).Single().Count == 2, "Opposite sides of a loop are different frames");
Check(Groups(Player(1, 0, 10), Player(2, 0, 11) with { Position = new Vector3(0, 1, 0) }).Count == 0, "Distance includes vertical separation");
var unordered = Groups(Player(3, 0.4f, 12), Player(1, 0, 10), Player(2, 0.2f, 11));
Check(unordered.Single().Select(p => p.Id).SequenceEqual(new ulong[] { 1, 2, 3 }), "Object enumeration order does not change membership");
Check(ClipFrames.InterleavedFrameCount(200 * 150, 150, 0, 0) == 200, "Uncompressed body clips expose their actual frame count");
Check(ClipFrames.InterleavedFrameCount(0, 0, 200 * 20, 20) == 200, "Float-only clips expose their actual frame count");
Check(ClipFrames.InterleavedFrameCount(200 * 150, 150, 200 * 20, 20) == 200, "Combined track counts agree");
Check(ClipFrames.InterleavedFrameCount(200 * 150, 150, 100 * 20, 20) == 0, "Inconsistent track counts are rejected");
Check(ClipFrames.InterleavedFrameCount(301, 150, 0, 0) == 0, "Incomplete track data is rejected");
Check(ClipFrames.CurrentFrame(5, 10, 201) == 100, "Live elapsed time maps to the clip frame");
Check(ClipFrames.CurrentFrame(10, 10, 201) == 200, "The final frame is included");
Check(ClipFrames.CurrentFrame(-1, 10, 201) == 0, "Negative transition time is clamped");
foreach(var key in new[] { "normal/idle", "normal/idle_inactive1", "normal/walk", "normal/walk_start_br", "normal/run", "battle/run_l", "normal/sprint", "battle/idle", "normal/swim_idle", "swim/idle", "swimming/forward", "dive/start", "normal/jump_landing", "normal/turn_loop_l" })
    Check(AnimationExclusions.IsExcluded(key), $"Excluded: {key}");
foreach(var key in new[] { "emote/dance", "emote/sit_idle", "emote/runaway", "chara/action/emote/dance.tmb", "EMOTE\\DANCE.TMB" })
    Check(!AnimationExclusions.IsExcluded(key), $"Retained: {key}");
foreach(var key in new[] { "normal/event_action_loop", "", "unknown/dance", "event_base/event_base_dance", "emote", "emote/", "not_emote/dance", "normal/emote_dance", "emote/../normal/idle" })
    Check(AnimationExclusions.IsExcluded(key), $"Non-emote excluded: {key}");
Check(AnimationExclusions.IsExcluded("chara/action/normal/idle.tmb"), "Full scheduler paths are classified");
Check(AnimationExclusions.IsExcluded("NORMAL\\SPRINT.TMB"), "Windows path separators and case are handled");
foreach(var key in new[] { "emote/pose", "emote/pose03_loop", "emote/pose01_loop.tmb", "chara/action/emote/pose03_loop.tmb", "EMOTE\\POSE03_LOOP.TMB" })
    Check(AnimationExclusions.IsExcluded(key), $"Pose excluded: {key}");
foreach(var key in new[] { "emote/s_pose01_loop", "emote/s_pose01_loop.tmb", "chara/action/emote/s_pose01_loop.tmb", "emote/c_pose03_loop" })
    Check(!AnimationExclusions.IsExcluded(key), $"Other pose names remain eligible: {key}");
Check(AnimationExclusions.GetExclusionReason("emote/pose03_loop") == "Excluded emote/pose animation", "Debug identifies the pose exclusion");
foreach(var key in new[] { "emote/dance_start", "emote/start_dance", "emote/s_pose01_start", "emote/dance_start_loop", "EMOTE/DANCE_START.TMB", "chara/action/emote/dance_start.tmb" })
    Check(AnimationExclusions.IsExcluded(key), $"Start excluded: {key}");
Check(!AnimationExclusions.IsExcluded("chara/start/action/emote/dance_loop.tmb"), "Start outside the emote name does not exclude the emote");
Check(AnimationExclusions.GetExclusionReason("emote/dance_start") == "Excluded emote containing start", "Debug identifies the start exclusion");
var djKey = SoundSyncPolicy.PlayerKey("Example Player", 21)!;
var djs = new Dictionary<string, string> { [djKey] = "Example Player@World" };
Check(!SoundSyncPolicy.CanSync(true, true, djKey, djs), "DJ sound is excluded even with global sound enabled");
Check(SoundSyncPolicy.CanSync(true, true, SoundSyncPolicy.PlayerKey("Other Player", 21), djs), "Other players still sync sound");
Check(SoundSyncPolicy.CanSync(true, true, SoundSyncPolicy.PlayerKey("Example Player", 22), djs), "Same name on another world is a different player");
Check(!SoundSyncPolicy.CanSync(false, true, "other", djs), "Global sound off overrides per-player eligibility");
Check(!SoundSyncPolicy.CanSync(true, true, null, djs), "Unidentified players do not accidentally restart DJ sound");
Check(SoundSyncPolicy.CanSync(true, false, null, djs), "Non-player actors keep their existing sound behavior");
djs.Remove(djKey);
Check(SoundSyncPolicy.CanSync(true, true, djKey, djs), "Removing the DJ exclusion restores sound eligibility");
Check(SoundSyncPolicy.PlayerKey("Example Player", 0) == null, "Missing home world is not a persistent identity");
var leaderGroup = new[] {
    Player(1, 0, 10) with { LocalTime = 0.33f },
    Player(2, 0.2f, 150, 101) with { LocalTime = 5f },
    Player(3, 0.4f, 80) with { LocalTime = 2.66f }
};
Check(NearbyGroups.Leader(leaderGroup).Id == 2, "Leading-frame mode chooses frame 150 rather than the new starter");
Check(NearbyGroups.Leader(leaderGroup).LocalTime == 5f, "Leader timing preserves the actual clip time");
Check(NearbyGroups.Leader(new[] { leaderGroup[1], leaderGroup[1] with { Id = 4, LocalTime = 5.01f } }).Id == 4, "Same-frame ties choose the furthest elapsed time");
Check(NearbyGroups.Leader(new[] { leaderGroup[1] with { Id = 4 }, leaderGroup[1] }).Id == 2, "Exact leader ties are deterministic");
Check(NearbyGroups.Leader(new[] { Player(1, 0, 0), Player(2, 0.2f, 199) }).Frame == 199, "Highest current frame wins at a loop boundary");
Check(PositionRules.InRange(Vector3.Zero, new Vector3(0.5f, 0, 0)), "Position alignment includes exactly 0.5 yalms");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(0.5001f, 0, 0)), "Position alignment rejects beyond 0.5 yalms");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(0, 0.51f, 0)), "Position alignment uses vertical distance too");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(float.NaN, 0, 0)), "Invalid coordinates cannot pass alignment range checks");
var positionCandidates = new[] { new PositionCandidate(1, Vector3.Zero), new PositionCandidate(2, new(0.4f, 0, 0)), new PositionCandidate(3, new(0.2f, 0, 0)) };
Check(PositionRules.Closest(Vector3.Zero, 1, positionCandidates) == 3, "No-target alignment chooses the nearest other player");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { positionCandidates[0] }) == null, "No-target alignment never selects yourself");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { new PositionCandidate(2, new(0.6f, 0, 0)) }) == null, "No candidate in range means no alignment");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { new PositionCandidate(4, new(0.2f, 0, 0)), positionCandidates[2] }) == 3, "Nearest-player ties are deterministic");
Check(Math.Abs(PositionRules.AngleDelta(MathF.PI - 0.0001f, -MathF.PI + 0.0001f)) < 0.001f, "Facing comparison handles angle wraparound");
