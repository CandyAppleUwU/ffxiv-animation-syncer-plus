using AnimSync;
using NAudio.Wave;
using System.Numerics;

static AnimationSample Player(ulong id, float x, int frame, ushort timeline = 100) =>
    new(id, $"Player {id}", new Vector3(x, 0, 0), timeline, frame, 200, 6.63333f, 1);
static void Check(bool condition, string name) {
    if(!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}
static List<List<AnimationSample>> Groups(params AnimationSample[] players) => NearbyGroups.Find(players, 0.6f);

var propTimeline = new byte[12 + 0x28];
"TMLB"u8.CopyTo(propTimeline);
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(propTimeline.AsSpan(4), propTimeline.Length);
propTimeline[8] = 1;
"C198"u8.CopyTo(propTimeline.AsSpan(12));
propTimeline[16] = 0x28;
System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(propTimeline.AsSpan(12 + 0x20), 1920);
Check(TmbPropModels.Read(propTimeline).SetEquals(new ushort[] { 1920 }), "Timeline props use the model ID from the Lemure spawn entry");
Check(TmbPropModels.Read(propTimeline.AsSpan(0, propTimeline.Length - 1)).Count == 0, "Truncated prop timelines cannot claim models");

var quantizedHeader = new byte[ClipFrames.QuantizedHeaderSize];
System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(quantizedHeader, 40);
System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(quantizedHeader.AsSpan(6), 257);
System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(quantizedHeader.AsSpan(8), 4.5f);
Check(ClipFrames.QuantizedFrameCount(quantizedHeader, 64, 4.5f) == 257, "Quantized clips use the stored frame count");
Check(ClipFrames.QuantizedFrameCount(quantizedHeader, 39, 4.5f) == 0 && ClipFrames.QuantizedFrameCount(quantizedHeader, 64, 5) == 0,
    "Truncated or inconsistent quantized headers are rejected");

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
Check(ClipFrames.PredictiveFrameCount(257, 150, 8, 150, 8) == 257, "Predictive clips use their stored frame count without a duration/FPS estimate");
Check(ClipFrames.PredictiveFrameCount(200, 180, 12, 150, 8) == 200, "Predictive skeletons may have more bones and slots than bound tracks");
Check(ClipFrames.PredictiveFrameCount(1, 150, 0, 150, 0) == 1, "Single-frame predictive poses remain readable by manual timelines");
Check(ClipFrames.PredictiveFrameCount(200, 0, 8, 0, 8) == 200, "Float-only predictive clips can expose timing");
Check(ClipFrames.PredictiveFrameCount(0, 150, 0, 150, 0) == 0 && ClipFrames.PredictiveFrameCount(1000001, 150, 0, 150, 0) == 0,
    "Invalid predictive frame counts cannot enter timeline arithmetic");
Check(ClipFrames.PredictiveFrameCount(200, 149, 8, 150, 8) == 0 && ClipFrames.PredictiveFrameCount(200, 150, 7, 150, 8) == 0,
    "Predictive metadata with too few bones or float slots is rejected");
Check(ClipFrames.PredictiveFrameCount(200, 0, 0, 0, 0) == 0 && ClipFrames.PredictiveFrameCount(200, -1, 0, 0, 0) == 0 &&
    ClipFrames.PredictiveFrameCount(200, 4097, 0, 150, 0) == 0, "Empty, negative and implausible predictive dimensions are rejected");
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
Check(PositionRules.AllowsAnimation("emote/pose01_start.pap") && PositionRules.AllowsAnimation(@"chara\animation\EMOTE\pose01_loop.PAP"),
    "Position alignment permits pose start and loop PAPs with full or relative paths");
Check(PositionRules.AllowsAnimation("chara/animation/resident/idle.pap") && PositionRules.AllowsAnimation("resident/move_a.pap"),
    "Position alignment permits resident idle and move_a");
Check(!PositionRules.AllowsAnimation("emote/j_pose02_start.pap") && !PositionRules.AllowsAnimation("emote/j_pose02_loop.pap") &&
    !PositionRules.AllowsAnimation("emote/s_pose01_loop.pap") && !PositionRules.AllowsAnimation("emote/dance.pap") &&
    !PositionRules.AllowsAnimation("not_emote/pose01_loop.pap") && !PositionRules.AllowsAnimation("resident/idle_extra.pap") &&
    !PositionRules.AllowsAnimation(null), "Position alignment rejects prefixed poses, other emotes, and missing PAPs");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(0.5001f, 0, 0)), "Position alignment rejects beyond 0.5 yalms");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(0, 0.51f, 0)), "Position alignment uses vertical distance too");
Check(!PositionRules.InRange(Vector3.Zero, new Vector3(float.NaN, 0, 0)), "Invalid coordinates cannot pass alignment range checks");
var positionCandidates = new[] { new PositionCandidate(1, Vector3.Zero), new PositionCandidate(2, new(0.4f, 0, 0)), new PositionCandidate(3, new(0.2f, 0, 0)) };
Check(PositionRules.Closest(Vector3.Zero, 1, positionCandidates) == 3, "No-target alignment chooses the nearest other player");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { positionCandidates[0] }) == null, "No-target alignment never selects yourself");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { new PositionCandidate(2, new(0.6f, 0, 0)) }) == null, "No candidate in range means no alignment");
Check(PositionRules.Closest(Vector3.Zero, 1, new[] { new PositionCandidate(4, new(0.2f, 0, 0)), positionCandidates[2] }) == 3, "Nearest-player ties are deterministic");
Check(Math.Abs(PositionRules.AngleDelta(MathF.PI - 0.0001f, -MathF.PI + 0.0001f)) < 0.001f, "Facing comparison handles angle wraparound");

Check(TimeSyncRules.Matches("Match anim with me c:", "Match anim with me c:"), "Shared phrases match exactly");
Check(TimeSyncRules.Matches("  shared phrase ", "shared phrase"), "Outer whitespace is ignored");
Check(!TimeSyncRules.Matches("shared phrase", "Shared phrase"), "Case differences do not trigger a restart");
Check(!TimeSyncRules.Matches("shared phrase", "hello shared phrase"), "A phrase within another message does not trigger");
Check(!TimeSyncRules.Matches("", ""), "Empty messages cannot trigger");
Check(!TimeSyncRules.ValidPhrase(" \t"), "Whitespace-only phrases are invalid");
Check(!TimeSyncRules.ValidPhrase("hello\n/party something"), "Multiline command injection is rejected");
Check(!TimeSyncRules.ValidPhrase("hello\0there"), "Null bytes cannot truncate the outgoing command");
Check(!TimeSyncRules.ValidPhrase("hello <t>"), "Chat placeholders cannot change a sent phrase");
Check(TimeSyncRules.ValidPhrase(new string('a', 180)), "The phrase byte limit is inclusive");
Check(!TimeSyncRules.ValidPhrase(new string('a', 181)), "Oversize phrases are rejected");
Check(!TimeSyncRules.ValidPhrase(new string('界', 61)), "Multibyte characters count toward the byte limit");
Check(TimeSyncRules.Due(1000, true) == 1300, "Outgoing echoes wait 300 ms");
Check(TimeSyncRules.Due(1000, false) == 1000, "Incoming messages add no artificial delay");

Check(!SyncScopeRules.ShouldScan(true, true, false), "Cutscenes and GPose block automatic groups");
Check(SyncScopeRules.ShouldScan(true, true, true), "Manual groups still run while automatic sync is paused");
Check(SyncScopeRules.ShouldScan(true, false, true), "Manual groups work during cutscenes with auto sync disabled");
Check(!SyncScopeRules.ShouldScan(false, false, false), "Disabled automatic groups do not run without a request");
Check(SyncScopeRules.IncludesPlayer(201, true) && SyncScopeRules.IncludesPlayer(439, true), "GPose includes its first and last actor slots");
Check(!SyncScopeRules.IncludesPlayer(0, true) && !SyncScopeRules.IncludesPlayer(200, true) && !SyncScopeRules.IncludesPlayer(440, true), "GPose manual scope excludes world actors and UI slots");
Check(SyncScopeRules.IncludesPlayer(0, false) && SyncScopeRules.IncludesPlayer(199, false) && !SyncScopeRules.IncludesPlayer(201, false), "World manual scope excludes GPose copies");

Check(TimelineRules.TimeAtFrame(0, 200, 10) == 0, "Timeline starts at the first frame");
Check(TimelineRules.TimeAtFrame(0, 1, 3) == 0 && TimelineRules.FrameAtTime(2, 1, 3) == 0 && ClipFrames.CurrentFrame(2, 3, 1) == 0,
    "Single-frame poses remain at frame zero without invalid timeline arithmetic");
Check(TimelineRules.TimeAtFrame(199, 200, 10) == 10, "Timeline reaches the final frame");
Check(TimelineRules.TimeAtFrame(-5, 200, 10) == 0 && TimelineRules.TimeAtFrame(500, 200, 10) == 10, "Timeline seeks clamp to clip bounds");
Check(TimelineRules.TimeAtFrame(1, 1, 10) == 0 && TimelineRules.TimeAtFrame(1, 200, float.NaN) == 0, "Invalid timeline clips cannot produce invalid seek values");
Check(Enumerable.Range(0, 200).All(frame => TimelineRules.FrameAtTime(TimelineRules.TimeAtFrame(frame, 200, 6.63333f), 200, 6.63333f) == frame), "Every frame in a 200-frame clip seeks back to the same displayed frame");
Check(Enumerable.Range(0, 1000).All(frame => TimelineRules.FrameAtTime(TimelineRules.TimeAtFrame(frame, 1000, 33.3f), 1000, 33.3f) == frame), "Long clips preserve exact displayed frame selections");
Check(TimelineRules.FrameAtTime(-1, 200, 10) == 0 && TimelineRules.FrameAtTime(11, 200, 10) == 199, "Displayed timeline frames stay in range");
Check(TimelineRules.FrameAtTime(float.NaN, 200, 10) == 0, "Invalid playback time cannot corrupt timeline state");
Check(TimelineRules.Matches(Player(1, 0, 10), Player(2, .6f, 10, 101), .6f), "Timeline includes already-synced players and different animation IDs at the exact radius");
Check(!TimelineRules.Matches(Player(1, 0, 10), Player(2, .601f, 20), .6f), "Timeline excludes players beyond the radius");
Check(!TimelineRules.Matches(Player(1, 0, 10), Player(2, .1f, 20) with { FrameCount = 300 }, .6f), "Timeline excludes different frame counts");
Check(!TimelineRules.Matches(Player(1, 0, 10), Player(2, .1f, 20) with { Duration = 8 }, .6f), "Timeline excludes incompatible clip durations");
Check(TimelineRules.Matches(Player(1, 0, 10) with { Speed = 0 }, Player(2, .1f, 20), .6f), "Paused timeline leaders can still select matching participants");
Check(!TimelineRules.Matches(Player(1, 0, 10), Player(2, .1f, 20) with { Position = new(0, .61f, 0) }, .6f), "Timeline radius includes vertical separation");
Check(!TimelineRules.Matches(Player(1, 0, 10), Player(2, .1f, 20), float.NaN), "Invalid timeline radius cannot select other players");
Check(TimelineRules.PlaySpeed(2) == 2 && TimelineRules.PlaySpeed(-2) == 2 && TimelineRules.PlaySpeed(0) == 1,
    "Play keeps the original speed magnitude and uses the slider to choose direction");
Check(TimelineRules.CanRestore(0, 0), "Timeline restores playback speeds that it paused");
Check(TimelineRules.HoldTime(2, 2) < 2 && TimelineRules.HoldTime(1, 2) == 1,
    "Holding the last frame samples just inside the clip endpoint");
Check(TimelineRules.AdvanceLoop(1.75, .5, 2) == .25 && TimelineRules.AdvanceLoop(.25, -.5, 2) == 1.75,
    "Driven prop timelines wrap the full clip in both directions");
Check(TimelineRules.DrivenLayerTime(2.5f, 1) == .5f && TimelineRules.DrivenLayerTime(1, 1) < 1,
    "A shorter driven layer wraps instead of holding the end pose for the rest of the main clip");
Check(!TimelineRules.CanRestore(2, 0), "Timeline release preserves newer speed changes from other tools");

var scrub = new TimelineScrubState();
scrub.Begin(1, 30, true);
Check(scrub.Active && scrub.Frame == 30, "Slider activation retains its selected frame while playback pauses");
scrub.Move(150);
Check(scrub.Frame == 150, "Dragging updates the held frame independently of live playback samples");
var resumed = scrub.Finish(1);
Check(resumed is { Frame: 150, Play: true } && !scrub.Active, "Releasing a playing animation resumes at the last selected frame");
scrub.Begin(2, 80, false);
scrub.Move(100);
Check(scrub.Finish(2) is { Frame: 100, Play: false }, "Dragging an already paused animation leaves it paused");
scrub.Begin(3, 12, true);
Check(scrub.Finish(3) is { Frame: 12, Play: true }, "Holding without changing frames still resumes on release");
scrub.Begin(4, 9, true);
Check(scrub.Finish(5) == null && !scrub.Active, "An animation identity change cannot resume an old slider drag");
scrub.Begin(6, 99, true);
scrub.Cancel();
Check(scrub.Finish(6) == null, "Closing or resetting the timeline cancels pending drag resumption");
scrub.Begin(7, 45, false);
Check(scrub.Finish(7) is { Frame: 45, Play: false }, "A new drag does not inherit the previous drag's resume state");

var repeat = new TimelineStepRepeat();
Check(repeat.Start(1, 10, 200, 0) == 11, "Next-frame press steps immediately");
Check(repeat.Poll(1, 200, .49) == null, "Next-frame hold waits half a second before repeating");
Check(repeat.Poll(1, 200, .5) == 12, "Next-frame hold begins repeating at half a second");
Check(repeat.Poll(1, 200, .69) == null && repeat.Poll(1, 200, .71) == 13, "Held Next advances at five frames per second");
Check(repeat.Poll(1, 200, 10) == 14 && repeat.Poll(1, 200, 10) == null, "Slow renders do not cause repeated-step bursts");
repeat.Cancel();
Check(repeat.Poll(1, 200, 11) == null, "Releasing Next stops repeating");
repeat.Start(2, 198, 200, 0);
Check(repeat.Poll(2, 200, 1) == null, "Next-frame repeat stops at the last frame");
repeat.Start(3, 10, 200, 0);
Check(repeat.Poll(4, 200, 1) == null && !repeat.Active, "A changed animation cancels a held step");
var rowSamples = new[] {Player(1, 0, 10), Player(2, .2f, 10), Player(3, 5, 30, 101), Player(4, 5.2f, 40, 102), Player(5, 5.3f, 50) with {FrameCount = 300}};
var claimedRows = new HashSet<ulong>();
var ownRow = TimelineGroups.Select(rowSamples[0], rowSamples, .6f, claimedRows);
Check(ownRow.SetEquals(new ulong[]{1,2}), "Own timeline claims only its matching nearby group");
claimedRows.UnionWith(ownRow);
Check(TimelineGroups.Select(rowSamples[1], rowSamples, .6f, claimedRows).Count == 0, "A target already in your group cannot create a duplicate timeline");
var targetRow = TimelineGroups.Select(rowSamples[2], rowSamples, .6f, claimedRows);
Check(targetRow.SetEquals(new ulong[]{3,4}), "A distant target gets its own group without mixing incompatible frame counts");
claimedRows.UnionWith(targetRow);
Check(TimelineGroups.Select(rowSamples[3], rowSamples, .6f, claimedRows).Count == 0, "An already-added target group cannot be added twice");
var overlap = new[] {Player(6, .5f, 10), Player(7, 1f, 20)};
var secondRow = TimelineGroups.Select(overlap[1], overlap, .6f, new HashSet<ulong>{6});
Check(secondRow.SetEquals(new ulong[]{7}), "Overlapping timeline groups never claim the same actor twice");

Check(repeat.Start(10, 50, 200, 0, -1) == 49, "Previous-frame press steps backward immediately");
Check(repeat.Poll(10, 200, .49) == null && repeat.Poll(10, 200, .5) == 48, "Previous-frame hold uses the same half-second delay");
Check(repeat.Poll(10, 200, .69) == null && repeat.Poll(10, 200, .71) == 47, "Held Previous moves backward at five frames per second");
repeat.Cancel();
Check(repeat.Poll(10, 200, 1) == null, "Releasing Previous stops backward stepping");
Check(repeat.Start(11, 0, 200, 0, -1) == 0 && repeat.Poll(11, 200, 1) == null, "Previous-frame repeat stops at the first frame");
Check(repeat.Start(12, 10, 200, 0, 1) == 11 && repeat.Direction == 1, "Switching from backward to forward resets repeat direction");
Check(repeat.Start(12, 11, 200, .1, -1) == 10 && repeat.Poll(12, 200, .59) == null, "Switching direction restarts the hold delay");

static byte[] TestScd() {
    var bytes = new byte[176];
    "SEDBSSCF"u8.CopyTo(bytes);
    void Short(int p, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p, 2), v);
    void Int(int p, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(p, 4), v);
    Short(14, 48); Short(52, 1); Int(60, 80); Int(80, 96);
    Int(96, 8); Int(100, 2); Int(104, 44100); Int(108, 6); Int(120, 40);
    Int(144, 0); Int(148, 8); "OggS"u8.CopyTo(bytes.AsSpan(160));
    return bytes;
}
static bool InvalidScd(byte[] bytes) {
    try { ScdVorbisClip.Read(bytes); return false; }
    catch(Exception ex) when(ex is InvalidDataException or OverflowException) { return true; }
}
var clipTest = ScdVorbisClip.Read(TestScd());
Check(clipTest.Ogg.Length == 16 && clipTest.Channels == 2 && clipTest.SampleRate == 44100, "SCD reader joins the Vorbis header and audio payload");
Check(InvalidScd(new byte[16]), "Truncated SCD headers are rejected");
var badScd = TestScd(); badScd[12] = 1;
Check(InvalidScd(badScd), "Big-endian SCD data is rejected by the limited test reader");
badScd = TestScd(); badScd[128] = 2;
Check(InvalidScd(badScd), "Encrypted Vorbis is rejected rather than decoded incorrectly");
badScd = TestScd(); badScd[52] = 2;
Check(InvalidScd(badScd), "Unsupported multiple-entry SCD files fail clearly");
badScd = TestScd(); badScd[80] = 255;
Check(InvalidScd(badScd), "Out-of-bounds SCD audio offsets are rejected");
badScd = TestScd(); badScd[160] = 0;
Check(InvalidScd(badScd), "Missing Ogg stream headers are rejected");
Check(SoundProbeRules.Relevant("sound/custom/new_dance.scd"), "Listener captures an arbitrary virtual sound before its PAP has been indexed");
Check(SoundProbeRules.Relevant("C:\\Mods\\Different Mod\\music.SCD"), "Listener accepts resolved files outside the preview folder");
Check(SoundProbeRules.Relevant("|collection|C:/Other/track.scd"), "Listener handles Penumbra-wrapped sound paths");
Check(!SoundProbeRules.Relevant("sound/track.pap") && !SoundProbeRules.Relevant("sound/track.scd.bak"), "Listener rejects non-SCD resource paths");
if(args.Length > 0) {
    foreach(var file in Directory.EnumerateFiles(args[0], "*.scd", SearchOption.AllDirectories)) {
        var clip = ScdVorbisClip.Read(File.ReadAllBytes(file));
        using var audio = new NAudio.Vorbis.VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
        Check(audio.TotalTime.TotalSeconds > 0 && audio.WaveFormat.SampleRate == clip.SampleRate, $"Decodes real Luna sound: {Path.GetFileName(file)} ({audio.TotalTime.TotalSeconds:0.00}s)");
        var middle = audio.TotalTime.TotalSeconds / 2;
        audio.CurrentTime = TimeSpan.FromSeconds(middle);
        Check(Math.Abs(audio.CurrentTime.TotalSeconds - middle) < .05 && audio.Read(new byte[8192], 0, 8192) > 0, $"Seeks and decodes the middle of {Path.GetFileName(file)} without an audio device");
        audio.CurrentTime = TimeSpan.Zero;
        Check(audio.CurrentTime == TimeSpan.Zero && audio.Read(new byte[1024], 0, 1024) > 0, $"Seeks backward to the beginning of {Path.GetFileName(file)}");
    }
}

Check(!MusicRules.Eligible(10, true, false), "Exactly 10 seconds is not music");
Check(!MusicRules.Eligible(9.999, true, false) && MusicRules.Eligible(10.001, true, false), "Music requires strictly more than 10 seconds");
Check(!MusicRules.Eligible(double.NaN, true, false) && !MusicRules.Eligible(double.PositiveInfinity, true, false), "Unknown and invalid audio durations cannot create music rows");
Check(!MusicRules.Eligible(259.32, false, false), "A long song without matching ownership cannot create a music row");
Check(!MusicRules.Eligible(259.32, true, true), "DJ-excluded players cannot receive music rows");
var musicOwner = new MusicOwner(1, 1234, (nint)0x1000, 4, 100, false);
Check(MusicRules.OwnerMatches(musicOwner, musicOwner with {}), "Captured ownership matches the exact timeline player");
Check(!MusicRules.OwnerMatches(musicOwner, null), "An unknown source player is never assigned to a timeline");
Check(!MusicRules.OwnerMatches(musicOwner, musicOwner with {Track=2}), "A different timeline cannot inherit another player's music");
Check(!MusicRules.OwnerMatches(musicOwner, musicOwner with {Id=5678}) && !MusicRules.OwnerMatches(musicOwner, musicOwner with {Address=(nint)0x2000}), "Reused object slots or addresses cannot pass full music ownership checks");
Check(!MusicRules.OwnerMatches(musicOwner, musicOwner with {Index=5}) && !MusicRules.OwnerMatches(musicOwner, musicOwner with {Territory=200}) && !MusicRules.OwnerMatches(musicOwner, musicOwner with {Gpose=true}), "Music ownership is invalidated by slot, territory or GPose changes");

var inferredOwner = new MusicOwner(0, 100, (nint)1000, 0, 10, false);
var candidateA = new MusicCandidate(inferredOwner, "sound/music.scd", "C:/Mods/A/song.scd", 0, 1000, 2000);
Check(MusicInference.Matches("sound/music.scd", 0, 2000, new[]{candidateA}).Count == 1, "Unique PAP sound reference produces one inferred candidate");
var candidateB = candidateA with { Owner = inferredOwner with { Id=101, Address=(nint)2000, Index=2, Track=-1 } };
Check(MusicInference.Matches("sound/music.scd", 0, 2000, new[]{candidateA, candidateB}).Count == 2, "Another visible player without a timeline still makes inference ambiguous");
Check(MusicInference.Matches("sound/music.scd", 0, 2000, new[]{candidateA, candidateA}).Count == 1, "Repeated references from one player do not create false ambiguity");
Check(MusicInference.Matches("sound/music.scd", 1, 2000, new[]{candidateA}).Count == 0, "Inference requires the exact sound index");
Check(MusicInference.Matches("sound/other.scd", 0, 2000, new[]{candidateA}).Count == 0, "Unrelated sounds cannot match PAP candidates");
Check(MusicInference.Matches("|collection|C:\\Mods\\A\\song.scd", 0, 2000, new[]{candidateA}).Count == 1, "Resolved Penumbra paths match with wrapper and separator normalization");
Check(MusicInference.Matches("C:/Mods/B/song.scd", 0, 2000, new[]{candidateA}).Count == 0, "Equal basenames in different collections do not establish ownership");
Check(MusicInference.Matches("sound/music.scd", 0, 5001, new[]{candidateA}).Count == 0, "Old PAP history does not claim unrelated later sound triggers");
Check(MusicInference.Matches("sound/music.scd", 0, 2000, new[]{candidateA with {FirstSeen=2501,LastSeen=2501}}).Count == 0, "A player starting the animation much later cannot claim an earlier trigger");
Check(MusicInference.Matches("sound/music.scd", 0, 2000, new[]{candidateA with {FirstSeen=2250,LastSeen=2250}}).Count == 1, "A post-trigger polling snapshot can supply the just-started animation");
Check(MusicInference.Matches("C:/Mods/A/song.scd", 0, 2000, new[]{candidateA, candidateB with {ResolvedPath="C:/Mods/B/song.scd"}}).Count == 1, "Distinct resolved files can disambiguate players sharing a virtual cue");
if(args.Length > 0) {
    foreach(var pap in Directory.EnumerateFiles(args[0], "*.pap", SearchOption.AllDirectories)) {
        var references = PapSoundReferences.Read(File.ReadAllBytes(pap));
        Check(references.Count > 0 && references.All(r => r.Path.StartsWith("sound/") && r.SoundIndex == 0), $"Parses bounded embedded sound entries from {Path.GetFileName(pap)}");
        if(Path.GetFileName(pap).StartsWith("lunastart", StringComparison.OrdinalIgnoreCase))
            Check(references.Any(r => r.Path == "sound/starter.scd") && references.Any(r => r.Path == "sound/poppy.scd"), $"Recognizes both intro effects in {Path.GetFileName(pap)}");
    }
}
static bool InvalidPap(byte[] data) { try {PapSoundReferences.Read(data); return false;} catch(Exception ex) when(ex is InvalidDataException or OverflowException){return true;} }
Check(InvalidPap(new byte[25]), "Truncated PAP headers are rejected");
var invalidPap = new byte[26]; "pap "u8.CopyTo(invalidPap);
Check(InvalidPap(invalidPap), "Empty or invalid PAP animation tables are rejected");
var customSoundPath = System.Text.Encoding.UTF8.GetBytes("sound/completely_different/song.scd\0");
var customPap = new byte[110 + customSoundPath.Length];
"pap "u8.CopyTo(customPap);
customPap[8] = 1;
void PapInt(int offset, int value) => BitConverter.GetBytes(value).CopyTo(customPap, offset);
PapInt(14, 26); PapInt(18, 66); PapInt(22, 66);
customPap[60] = 7; // Active Havok binding, independent of the PAP/emote name.
"TMLB"u8.CopyTo(customPap.AsSpan(66)); PapInt(70, customPap.Length - 66); PapInt(74, 1);
"C063"u8.CopyTo(customPap.AsSpan(78)); PapInt(82, 32); PapInt(98, 24); PapInt(102, 5);
customSoundPath.CopyTo(customPap, 110);
var customReferences = PapSoundReferences.Read(customPap);
Check(customReferences.Count == 1 && customReferences[0] == new PapSoundReference(7, "sound/completely_different/song.scd", 5),
    "Non-Luna PAP sound paths, active binding and nonzero cue metadata are parsed without a name allowlist");
Check(SoundProbeRules.Relevant(customReferences[0].Path), "A newly parsed arbitrary PAP sound is eligible for trigger capture");

var slots = new SoundSlotTracker();
Check(slots.Capture((nint)1000, (nint)900, 0) == null, "Unobserved sound allocations cannot be taken over");
slots.Allocated((nint)1000, (nint)900);
var ticket = slots.Capture((nint)1000, (nint)900, 0)!;
Check(slots.Current(ticket), "Pending sound tickets survive loading without requiring a ready sound ID");
Check(slots.Capture((nint)1000, (nint)901, 0) == null, "A different sound pool cannot inherit a ticket");
slots.Allocated((nint)1000, (nint)900);
Check(!slots.Current(ticket), "Recycling a sound slot invalidates the previous ticket");
var newTicket = slots.Capture((nint)1000, (nint)900, 0)!;
slots.Clear();
Check(!slots.Current(newTicket), "Disabling the listener invalidates pending sound control");
slots.Allocated((nint)1000, (nint)900);
Check(!slots.Current(newTicket), "Re-enabling listening cannot revive an old sound ticket");
Check(MusicControlRules.PoolContains((nint)4096, (nint)4096, 208), "Sound pool accepts the first exact slot");
Check(MusicControlRules.PoolContains((nint)(4096+255*208), (nint)4096, 208), "Sound pool accepts the last exact slot");
Check(!MusicControlRules.PoolContains((nint)(4096+256*208), (nint)4096, 208), "Sound pool rejects the first address beyond its bounds");
Check(!MusicControlRules.PoolContains((nint)4097, (nint)4096, 208) && !MusicControlRules.PoolContains((nint)4095, (nint)4096, 208), "Sound pool rejects misaligned and preceding addresses");
Check(!MusicControlRules.PoolContains(0,0,208), "Null sound pools cannot be controlled");
Check(MusicControlRules.RestoreVolume(0) && !MusicControlRules.RestoreVolume(.4f) && !MusicControlRules.RestoreVolume(float.NaN), "Release preserves external volume changes and rejects invalid volume state");

var signedSound = new NativeSoundIdentity((nint)4096, (nint)8192, -2147437464, 0);
Check(MusicControlRules.ReadyIdentity(true, false, signedSound) == signedSound, "Observed high-bit negative sound IDs remain valid opaque handles");
Check(MusicControlRules.ReadyIdentity(true, false, signedSound with {SoundId=int.MinValue}) != null, "Sound readiness does not depend on the signed ID range");
Check(MusicControlRules.ReadyIdentity(false, false, signedSound) == null, "Negative-ID support does not bypass the active sound requirement");
Check(MusicControlRules.ReadyIdentity(true, true, signedSound) == null, "Negative-ID support does not bypass the loading check");
Check(MusicControlRules.ReadyIdentity(true, false, signedSound with {Resource=0}) == null, "A missing native resource still prevents takeover");
Check(signedSound != signedSound with {SoundId=-2147437463}, "Full sound-handle identity distinguishes different negative tokens");

Check(MusicVolume.Resolve(null, .42f) == .42f && MusicVolume.Resolve(null, .61f) == .61f,
    "Without a saved override, timeline volume follows game channel changes");
Check(MusicVolume.Resolve(.25f, .9f) == .25f && MusicVolume.Resolve(.25f, .8f) == .25f,
    "The same saved volume applies to songs from different channels");
Check(MusicVolume.Resolve(0, .7f) == 0 && MusicVolume.Resolve(null, .7f) == .7f,
    "Saved zero volume is distinct from restoring game volume");
Check(MusicVolume.Resolve(float.NaN, .8f) == 0 && MusicVolume.Resolve(null, 2) == 1 && MusicVolume.Resolve(null, -1) == 0,
    "Invalid or out-of-range local volume cannot amplify unexpectedly");
Check(MusicVolume.Channel(1) == "Music" && MusicVolume.Channel(16) == "Music (Orchestrion)" && MusicVolume.Channel(2) == "Sound Effects" &&
    MusicVolume.Channel(3) == "Voice" && MusicVolume.Channel(4) == "System" && MusicVolume.Channel(5) == "Ambient", "Game buses display the corresponding user-facing audio channels");
Check(MusicVolume.Channel(-1) == "Unknown channel", "Unknown sound routing is not guessed as music or effects");

static byte[] TestSoundTable() {
    var bytes = new byte[144]; "SEDBSSCF"u8.CopyTo(bytes);
    bytes[14] = 48; bytes[48] = 2; bytes[80] = 96; bytes[84] = 112;
    bytes[97] = 16; bytes[113] = 3;
    BitConverter.GetBytes(1f).CopyTo(bytes, 104); BitConverter.GetBytes(.5f).CopyTo(bytes, 120);
    return bytes;
}
static bool InvalidSoundInfo(byte[] bytes, uint index = 0) {
    try { ScdSoundInfo.Read(bytes, index); return false; }
    catch(Exception ex) when(ex is InvalidDataException or OverflowException) { return true; }
}
Check(ScdSoundInfo.Read(TestSoundTable(), 0) is { Bus: 16, Volume: 1 }, "SCD sound entry supplies its real audio bus");
Check(ScdSoundInfo.Read(TestSoundTable(), 1) is { Bus: 3, Volume: .5f }, "Sound routing is selected by exact cue index");
Check(InvalidSoundInfo(TestSoundTable(), 2) && InvalidSoundInfo(TestSoundTable(), uint.MaxValue), "Missing or oversized cue indices cannot read another table");
var badSound = TestSoundTable(); badSound[80] = 250;
Check(InvalidSoundInfo(badSound), "Out-of-bounds sound entries are rejected");
badSound = TestSoundTable(); badSound[97] = 21;
Check(InvalidSoundInfo(badSound), "Unknown bus numbers never index native volume arrays");
badSound = TestSoundTable(); BitConverter.GetBytes(float.NaN).CopyTo(badSound, 104);
Check(InvalidSoundInfo(badSound), "Nonfinite sound-entry gain is rejected");
Check(InvalidSoundInfo(new byte[16]), "Truncated sound routing is rejected");

var tinyWave = new NAudio.Wave.WaveFormat(8000, 8, 1);
using(var bytes = new MemoryStream(new byte[] {1,2,3}))
using(var source = new NAudio.Wave.RawSourceWaveStream(bytes, tinyWave)) {
    var loops = 0;
    var looping = new LoopingWaveProvider(source, () => { source.Position = 0; loops++; }, new object(), true);
    var buffer = new byte[12];
    Check(looping.Read(buffer, 2, 8) == 8 && buffer.Skip(2).Take(8).SequenceEqual(new byte[]{1,2,3,1,2,3,1,2}) && loops == 2,
        "Audio buffer continues across multiple loop boundaries without gaps or padding");
    Check(buffer[0] == 0 && buffer[1] == 0 && buffer[10] == 0, "Looping respects destination buffer offsets");
    Check(looping.Read(buffer, 0, 0) == 0 && loops == 2, "Zero-length reads do not rewind playback");
}
using(var bytes = new MemoryStream(new byte[] {1,2,3}))
using(var source = new NAudio.Wave.RawSourceWaveStream(bytes, tinyWave)) {
    var unlooped = new LoopingWaveProvider(source, () => throw new Exception("Unexpected rewind"), new object(), false);
    var buffer = new byte[8];
    Check(unlooped.Read(buffer, 0, 8) == 3 && unlooped.Read(buffer, 0, 8) == 0, "Independent file preview retains normal end-of-file behavior");
}
using(var bytes = new MemoryStream())
using(var source = new NAudio.Wave.RawSourceWaveStream(bytes, tinyWave)) {
    var loops = 0;
    var emptyLoop = new LoopingWaveProvider(source, () => { source.Position = 0; loops++; }, new object(), true);
    Check(emptyLoop.Read(new byte[8], 0, 8) == 0 && loops == 1, "An empty looping stream terminates without spinning the audio thread");
}
if(args.Length > 0) {
    foreach(var file in Directory.EnumerateFiles(args[0], "*.scd", SearchOption.AllDirectories)) {
        var bytes = File.ReadAllBytes(file);
        var route = ScdSoundInfo.Read(bytes, 0);
        Check(route.Bus is >= 1 and <= 20, $"Reads real sound channel for {Path.GetFileName(file)}: {MusicVolume.Channel(route.Bus)} (bus {route.Bus})");
        var clip = ScdVorbisClip.Read(bytes);
        using var audio = new NAudio.Vorbis.VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
        var loops = 0;
        var looping = new LoopingWaveProvider(audio, () => { audio.CurrentTime = TimeSpan.Zero; loops++; }, new object(), true);
        var buffer = new byte[audio.WaveFormat.AverageBytesPerSecond / 2];
        var filled = true;
        // Decode naturally through EOF: some mod files have inconsistent final-page seek granules.
        for(var read = 0; read < Math.Ceiling(audio.TotalTime.TotalSeconds * 2) + 4 && loops == 0; read++)
            filled &= looping.Read(buffer, 0, buffer.Length) == buffer.Length;
        Check(filled && loops >= 1 && audio.CurrentTime.TotalSeconds < .6,
            $"Real Vorbis playback fills a buffer through the end/start loop: {Path.GetFileName(file)}");
    }
}

if(args.Length > 0) {
    var exportDirectory = Path.Combine(Path.GetTempPath(), "AnimSync-export-check-" + Guid.NewGuid().ToString("N"));
    var exported = new List<string>();
    try {
        var source = Path.Combine(args[0], "song", "dance2.scd");
        var first = SoundExport.Wav(source, exportDirectory, "Test Character"); exported.Add(first);
        var second = SoundExport.Wav(source, exportDirectory, "Test Character"); exported.Add(second);
        Check(Path.GetFileName(first).StartsWith("Test Character_") && Guid.TryParseExact(Path.GetFileNameWithoutExtension(first)["Test Character_".Length..], "N", out _),
            "WAV filename uses the source character name and a random ID, not the SCD filename");
        Check(first != second && File.Exists(first) && File.Exists(second), "Repeated WAV exports preserve earlier files");
        var clip = ScdVorbisClip.Read(File.ReadAllBytes(source));
        using var original = new NAudio.Vorbis.VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
        using var wav = new NAudio.Wave.WaveFileReader(first);
        Check(wav.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.Pcm && wav.WaveFormat.BitsPerSample == 16 &&
            wav.WaveFormat.SampleRate == clip.SampleRate && wav.WaveFormat.Channels == clip.Channels,
            "Export produces standard PCM16 WAV with original sample rate and channels");
        Check(Math.Abs(wav.TotalTime.TotalSeconds - original.TotalTime.TotalSeconds) < .05,
            "WAV export contains the complete song once without playback looping");
        var expected = new float[8192]; var actual = new float[8192];
        var a = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(original).Read(expected, 0, expected.Length);
        var b = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(wav).Read(actual, 0, actual.Length);
        Check(a == b && expected.Zip(actual).All(pair => Math.Abs(pair.First - pair.Second) < .0001f),
            "Decoded WAV samples match source audio within PCM16 precision without playback gain");
    } finally {
        foreach(var path in exported) File.Delete(path);
        if(Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory);
    }
}

static byte[] AvfxChunk(string tag, params byte[][] contents) {
    var content = contents.SelectMany(c => c).ToArray();
    var bytes = new byte[8 + content.Length + (4 - content.Length % 4) % 4];
    System.Text.Encoding.ASCII.GetBytes(tag.Reverse().ToArray()).CopyTo(bytes, 0);
    BitConverter.GetBytes(content.Length).CopyTo(bytes, 4); content.CopyTo(bytes, 8);
    return bytes;
}
var emitterA = AvfxChunk("Emit", AvfxChunk("SdNm", System.Text.Encoding.UTF8.GetBytes("sound/vfx_song.scd\0")), AvfxChunk("SdNo", BitConverter.GetBytes(3)));
var emitterB = AvfxChunk("Emit", AvfxChunk("SdNo", BitConverter.GetBytes(0)), AvfxChunk("SdNm", System.Text.Encoding.UTF8.GetBytes("sound/another.scd\0")));
var syntheticVfx = AvfxChunk("AVFX", emitterA, emitterB);
var vfxRefs = AvfxSoundReferences.Read(syntheticVfx);
Check(vfxRefs.SequenceEqual(new[]{new VfxSoundReference("sound/vfx_song.scd", 3), new VfxSoundReference("sound/another.scd", 0)}),
    "AVFX sound paths pair with cue numbers in their own emitters, regardless of field order");
Check(AvfxSoundReferences.Read(AvfxChunk("AVFX", emitterA, emitterA)).Count == 1, "Repeated emitter sound references deduplicate");
Check(AvfxSoundReferences.Read(AvfxChunk("AVFX", AvfxChunk("SdNm", System.Text.Encoding.UTF8.GetBytes("sound/not_an_emitter.scd\0")))).Count == 0,
    "Stray AVFX strings cannot invent an emitter sound reference");
Check(AvfxSoundReferences.Read(AvfxChunk("AVFX", AvfxChunk("Emit", AvfxChunk("SdNm", System.Text.Encoding.UTF8.GetBytes("sound/disabled.scd\0")), AvfxChunk("SdNo", BitConverter.GetBytes(-1))))).Count == 0,
    "Disabled AVFX sound index minus one is excluded");
Check(AvfxSoundReferences.Read(AvfxChunk("AVFX", AvfxChunk("Emit", AvfxChunk("SdNm"), AvfxChunk("SdNo", BitConverter.GetBytes(0))))).Count == 0,
    "Empty AVFX sound fields remain silent");
static bool InvalidVfx(byte[] bytes) { try { AvfxSoundReferences.Read(bytes); return false; } catch(Exception ex) when(ex is InvalidDataException or OverflowException){return true;} }
Check(InvalidVfx(new byte[7]), "Truncated AVFX headers are rejected");
var corruptVfx = syntheticVfx.ToArray(); BitConverter.GetBytes(int.MaxValue).CopyTo(corruptVfx, 12);
Check(InvalidVfx(corruptVfx), "AVFX chunk lengths cannot escape parent bounds");
Check(InvalidVfx(AvfxChunk("AVFX", AvfxChunk("Emit", AvfxChunk("SdNm", "sound/unterminated.scd"u8.ToArray())))), "Unterminated AVFX sound names are rejected");
foreach(var entry in new[]{("C012",0x48), ("C173",0x44)}) {
    var pathBytes = System.Text.Encoding.UTF8.GetBytes("vfx/custom/song.avfx\0");
    var pap = new byte[78 + entry.Item2 + pathBytes.Length];
    "pap "u8.CopyTo(pap); pap[8] = 1;
    void Int(int p, int v) => BitConverter.GetBytes(v).CopyTo(pap, p);
    Int(14, 26); Int(18, 66); Int(22, 66); pap[60] = 9;
    "TMLB"u8.CopyTo(pap.AsSpan(66)); Int(70, pap.Length - 66); Int(74, 1);
    System.Text.Encoding.ASCII.GetBytes(entry.Item1).CopyTo(pap, 78); Int(82, entry.Item2);
    Int(98, entry.Item2 - 8); pathBytes.CopyTo(pap, 78 + entry.Item2);
    var references = new List<PapVfxReference>();
    Check(PapSoundReferences.Read(pap, references).Count == 0 && references.SequenceEqual(new[]{new PapVfxReference(9, "vfx/custom/song.avfx")}),
        $"PAP {entry.Item1} VFX path preserves the exact Havok binding");
    Int(98, int.MaxValue);
    try { PapSoundReferences.Read(pap, new List<PapVfxReference>()); Check(false, "Invalid VFX offset accepted"); }
    catch(Exception ex) when(ex is InvalidDataException or OverflowException) { Check(true, "PAP VFX path bounds are enforced"); }
}
if(args.Length > 0) {
    foreach(var file in Directory.EnumerateFiles(args[0], "*.avfx", SearchOption.AllDirectories)) {
        var references = AvfxSoundReferences.Read(File.ReadAllBytes(file));
        Check(references.All(r => r.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)), $"Parses real AVFX emitter chunks: {Path.GetFileName(file)} ({references.Count} sound references)");
    }
    foreach(var file in Directory.EnumerateFiles(args[0], "*.pap", SearchOption.AllDirectories)) {
        var references = new List<PapVfxReference>(); PapSoundReferences.Read(File.ReadAllBytes(file), references);
        Check(references.All(r => r.Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)), $"Parses real PAP VFX links: {Path.GetFileName(file)} ({references.Count} links)");
    }
}

customPap[62] = 1;
Check(PapSoundReferences.Read(customPap).Single() == new PapSoundReference(7, "sound/completely_different/song.scd", 5),
    "Face-marked PAP entries preserve sound references for verified live bindings");
if(args.Length > 1) {
    foreach(var file in Directory.EnumerateFiles(args[1], "*.pap", SearchOption.AllDirectories)) {
        var refs = new List<PapVfxReference>(); PapSoundReferences.Read(File.ReadAllBytes(file), refs);
        Check(refs.Count > 0, $"BPM face-marked PAP preserves VFX dependency: {Path.GetFileName(file)}");
    }
    foreach(var file in Directory.EnumerateFiles(args[1], "*.avfx", SearchOption.AllDirectories)) {
        var refs = AvfxSoundReferences.Read(File.ReadAllBytes(file));
        Check(refs.Any(r => (r.Path == "sound/bpmloop.scd" || r.Path == "sound/bpmstart.scd") && r.SoundIndex == 0),
            $"BPM VFX supplies the observed sound and cue: {Path.GetFileName(file)}");
    }
}
var callOwner = new MusicOwner(2, 123, (nint)0xabc, 4, 100, false);
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd")) {
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/MareCache/abcdef.tmp");
    Check(call.Owner == callOwner && call.ResolvedPath == "C:/MareCache/abcdef.tmp" && !call.Conflict,
        "Penumbra owner evidence binds a virtual cue to the added timeline and actual cache file");
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/MareCache/abcdef.tmp");
    Check(!call.Conflict, "Repeated identical resource notifications do not create false conflicts");
}
using(var call = SoundCallOwnership.Begin("|collection|C:\\MareCache\\abcdef.tmp")) {
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/MareCache/abcdef.tmp");
    Check(call.Owner == callOwner, "Resolved wrapped paths can correlate to the same sound resource");
}
using(var outer = SoundCallOwnership.Begin("sound/outer.scd")) {
    using(var inner = SoundCallOwnership.Begin("sound/inner.scd")) {
        SoundCallOwnership.Observe(callOwner, "sound/inner.scd", "C:/Cache/inner.tmp");
        Check(inner.Owner == callOwner && outer.Owner == null, "Nested playback cannot leak ownership to a different outer call");
    }
    SoundCallOwnership.Observe(callOwner, "sound/outer.scd", "C:/Cache/outer.tmp");
    Check(outer.Owner == callOwner, "Returning from a nested playback restores the outer capture context");
}
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd")) {
    SoundCallOwnership.Observe(callOwner, "sound/other.scd", "C:/Cache/unrelated.tmp");
    Check(call.Owner == null && !call.Conflict, "Unrelated resource callbacks cannot assign the active sound call");
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/Cache/a.tmp");
    SoundCallOwnership.Observe(callOwner with {Id = 456, Address = (nint)0xdef}, "sound/bpmloop.scd", "C:/Cache/a.tmp");
    Check(call.Conflict, "Different players resolving the same sound within one call are rejected");
}
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd")) {
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/Cache/a.tmp");
    SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/Cache/b.tmp");
    Check(call.Conflict, "Conflicting resolved files cannot silently replace owner evidence");
}
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd")) {
    SoundCallOwnership.Observe(null, "sound/bpmloop.scd", "C:/Cache/a.tmp");
    Check(call.Conflict && call.Owner == null, "An unrecognized character cannot become a guessed sound owner");
}
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd")) {
    var thread = new System.Threading.Thread(() => SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/Cache/a.tmp"));
    thread.Start(); thread.Join();
    Check(call.Owner == null, "A resource event from another thread cannot attach to the current sound call");
}
try { using var failed = SoundCallOwnership.Begin("sound/bpmloop.scd"); throw new InvalidOperationException("simulated native failure"); }
catch(InvalidOperationException) { }
SoundCallOwnership.Observe(callOwner, "sound/bpmloop.scd", "C:/Cache/a.tmp");
using(var call = SoundCallOwnership.Begin("sound/bpmloop.scd"))
    Check(call.Owner == null && !call.Conflict, "Finished and exceptional sound calls leave no stale ownership context");

var addedAnchor = Player(100, 10, 80);
var addedNeighbors = new[] { addedAnchor, Player(101, 10.2f, 120, 201), Player(102, 10.3f, 60),
    Player(103, 0, 80), Player(104, 10.1f, 30) with { FrameCount = 300 } };
var addRestartScope = TimelineGroups.Select(addedAnchor, addedNeighbors, .6f, new HashSet<ulong>{102});
Check(addRestartScope.SetEquals(new ulong[]{100,101}),
    "Adding a timeline selects its target and matching neighbors without restarting distant, incompatible or earlier-claimed players");
var djKeys = new Dictionary<string,string> { ["1:DJ Player"] = "DJ Player" };
Check(!SoundSyncPolicy.CanSync(true, true, "1:DJ Player", djKeys) && SoundSyncPolicy.CanSync(true, true, "1:Other Player", djKeys) &&
    !SoundSyncPolicy.CanSync(false, true, "1:Other Player", djKeys),
    "Group restart sound policy preserves DJ exclusions and the disabled sound-sync setting");
var autoTake = new MusicAutoTakeover(1000);
Check(autoTake.CanAttempt(1000), "A newly detected song becomes eligible for immediate automatic takeover");
autoTake.Attempted(1000);
Check(!autoTake.CanAttempt(1249) && autoTake.CanAttempt(1250), "Loading sounds retry at bounded 250ms intervals");
Check(!autoTake.Expired(1249), "A retry cooldown is not misreported as a terminal failure");
Check(autoTake.CanAttempt(11000) && !autoTake.CanAttempt(11001) && autoTake.Expired(11001), "Automatic takeover stops retrying after ten seconds");
autoTake.Retry(12000); autoTake.Release();
Check(autoTake.Released && !autoTake.CanAttempt(12000), "Explicit release prevents immediate automatic recapture");
var secondTake = new MusicAutoTakeover(12000);
Check(secondTake.CanAttempt(12000) && !autoTake.CanAttempt(12000), "Releasing one timeline does not suspend another timeline's song");
autoTake.Retry(13000);
Check(!autoTake.Released && autoTake.CanAttempt(13000), "Resume or retry begins a new bounded takeover window");
Check(new MusicAutoTakeover(14000).CanAttempt(14000), "A replacement song begins with its own automatic takeover state");
Check(MusicVolume.Resolve(5, .1f) == 5 && MusicVolume.Resolve(6, 1) == 5,
    "The saved music gain supports 500 percent and clamps values above that maximum");
Check(MusicVolume.OutputGain(5, 1, 1, 1) == 5 && MusicVolume.OutputGain(1.5f, 1, 1, 1) == 1.5f,
    "Output mix preserves gain above unity instead of clamping it at 100 percent");
Check(MusicVolume.OutputGain(5, .8f, 1, 1) == 4 && MusicVolume.OutputGain(5, 0, 1, 1) == 0,
    "Amplified music still respects master attenuation and mute");
Check(MusicVolume.ClampGain(6) == 5 && MusicVolume.ClampGain(-1) == 0 && MusicVolume.ClampGain(float.NaN) == 0,
    "Sample-player gain remains finite and bounded to the 500 percent maximum");
Check(MusicVolume.PlaybackAllowed(true, false, false, false), "Background music stays audible when global and category background settings allow it");
Check(!MusicVolume.PlaybackAllowed(true, true, false, false), "Disabling all background sounds mutes the local music output");
Check(!MusicVolume.PlaybackAllowed(true, false, true, false), "Disabling just a sound category mutes that category in the background");
Check(!MusicVolume.PlaybackAllowed(true, true, true, false), "Global and category background mutes cannot cancel each other out");
Check(MusicVolume.PlaybackAllowed(false, true, true, false), "Inactive-only restrictions do not mute a focused game window");
Check(!MusicVolume.PlaybackAllowed(false, false, false, true) && !MusicVolume.PlaybackAllowed(true, false, false, true),
    "A muted category stays muted both in the foreground and background");
Check(MusicVolume.PlaybackAllowed(true, false, false, false) && !MusicVolume.PlaybackAllowed(true, false, true, false),
    "Different sound buses retain independent background permissions");
Check(MusicVolume.OutputGain(5, 1, 1, 1) == 5 && !MusicVolume.PlaybackAllowed(true, false, true, false),
    "Manual 500 percent gain does not override the separate channel background permission gate");

var eqTone = new float[48000];
for(var i = 0; i < eqTone.Length; i += 2)
    eqTone[i] = .1f * MathF.Sin(i / 2 * 2 * MathF.PI * 60 / 48000);
var eqBytes = new byte[eqTone.Length * sizeof(float)];
Buffer.BlockCopy(eqTone, 0, eqBytes, 0, eqBytes.Length);
using(var source = new RawSourceWaveStream(new MemoryStream(eqBytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) {
    var equalizer = new MusicEqualizer(source.ToSampleProvider());
    var output = new float[eqTone.Length];
    equalizer.SetGains([6, 0, 0, 0, 0, 0]);
    Check(equalizer.Read(output, 0, output.Length) == output.Length && output.Max() is > .19f and < .21f,
        "A six-decibel bass boost nearly doubles the 60 Hz sample amplitude");
    Check(output.Where((_, index) => index % 2 == 1).All(sample => sample == 0), "Stereo equalizer histories never leak into the other channel");
    source.Position = 0;
    equalizer.SetGains([0, 0, 0, 0, 0, 0]);
    Check(equalizer.Read(output, 0, output.Length) == output.Length && output.SequenceEqual(eqTone),
        "Selecting Flat during playback restores the exact unfiltered samples");
}
Check(MusicEqualizer.Normalize([20, -20, float.NaN, 0, 0, 0]).SequenceEqual(new float[] {12, -12, 0, 0, 0, 0}) &&
    MusicEqualizer.Normalize([3]).All(gain => gain == 0), "Malformed equalizer settings load with bounded or flat gains");

var impulse = new float[48000 * 2 * 2];
impulse[0] = .5f;
var impulseBytes = new byte[impulse.Length * sizeof(float)];
Buffer.BlockCopy(impulse, 0, impulseBytes, 0, impulseBytes.Length);
using(var source = new RawSourceWaveStream(new MemoryStream(impulseBytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) {
    var reverb = new MusicReverb(source.ToSampleProvider());
    var output = new float[impulse.Length];
    Check(reverb.Read(output, 0, output.Length) == output.Length && output.SequenceEqual(impulse),
        "Reverb at zero is an exact bypass");
    source.Position = 0;
    reverb.SetAmount(1);
    reverb.Read(output, 0, output.Length);
    Check(output[0] > 0 && output.Skip(1).Take(48000).Any(sample => MathF.Abs(sample) > .01f) &&
        output.TakeLast(24000).All(sample => MathF.Abs(sample) < .0001f), "Reverb keeps the original onset and adds a decaying tail");
    Check(output.Where((_, index) => index % 2 == 1).All(sample => sample == 0), "Reverb processes channels independently");
    source.Position = 0;
    reverb.Read(output, 0, 2);
    reverb.Reset();
    Check(reverb.Read(output, 0, output.Length - 2) == output.Length - 2 && output.Take(output.Length - 2).All(sample => sample == 0),
        "Resetting the reverb after a seek clears old reflections");
}

var musicLink = new LinkedTimelineClock(30, 300, 15, true, 1000);
Check(musicLink.MusicMaster && musicLink.MusicTime == 15 && musicLink.AnimationTime == 15, "Longer song becomes master with matching initial animation phase");
musicLink.Seek(45, 1000);
Check(musicLink.MusicTime == 45 && musicLink.AnimationTime == 15, "45 seconds of a 300-second song maps to 15 seconds of a 30-second animation");
Check(TimelineRules.FrameAtTime((float)musicLink.AnimationTime, 901, 30) == 450, "Linked 15-second animation phase maps to frame 450 in a 30fps clip");
musicLink.Tick(2000);
Check(musicLink.MusicTime == 46 && musicLink.AnimationTime == 16, "Linked real-time clock advances both timeline positions together");
musicLink.SetPlaying(false, 2000); musicLink.Tick(9000);
Check(musicLink.Position == 46, "Pausing the shared clock holds both timelines");
musicLink.Seek(299, 9000); musicLink.SetPlaying(true, 9000); musicLink.Tick(11000);
Check(musicLink.Position == 1 && musicLink.AnimationTime == 1, "Both timelines return to matching phases when the longer master loops");
var animationLink = new LinkedTimelineClock(300, 30, 45, false, 0);
Check(!animationLink.MusicMaster && animationLink.AnimationTime == 45 && animationLink.MusicTime == 15, "Longer animation drives a shorter looping song");
animationLink.Seek(300, 0);
Check(animationLink.AnimationTime == 300 && animationLink.MusicTime == 0, "Paused final animation frame stays selectable while shorter music wraps");
animationLink.SetPlaying(true, 0); animationLink.Tick(1000);
Check(animationLink.AnimationTime == 1 && animationLink.MusicTime == 1, "Playing from the master endpoint loops without an invalid seek");
animationLink.Seek(-10, 1000); Check(animationLink.Position == 0, "Linked seeks clamp before the start");
animationLink.Seek(500, 1000); Check(animationLink.Position == 300, "Linked seeks clamp after the end");
animationLink.Seek(double.NaN, 1000); Check(animationLink.Position == 300, "Nonfinite seek input cannot corrupt the shared clock");
Check(new LinkedTimelineClock(30,30,12,true,0).MusicMaster, "Equal length timelines select music as a deterministic master");
Check(Math.Abs(LinkedTimelineClock.LoopDistance(.01, 29.99, 30) - .02) < .00001, "Audio drift comparisons handle opposite sides of a loop boundary");
var fractionalLink = new LinkedTimelineClock(6.13,18.2,0,true,0); fractionalLink.Tick(18200);
Check(Math.Abs(fractionalLink.Position) < .000001, "Fractional-duration masters wrap consistently");
Check(musicLink.NeedsSeek, "A linked seek/play change requests immediate audio alignment"); musicLink.Sought(); Check(!musicLink.NeedsSeek, "Successful alignment clears the forced-seek flag");
var independentLink = new LinkedTimelineClock(20,60,10,false,0); independentLink.Seek(25,0);
Check(musicLink.Position == 1 && independentLink.AnimationTime == 5, "Separate character links keep independent master positions");
try { _ = new LinkedTimelineClock(0,30,0,true,0); Check(false, "Zero-length link accepted"); }
catch(ArgumentOutOfRangeException) { Check(true, "Zero-duration timelines cannot create a linked clock"); }
try { _ = new LinkedTimelineClock(30,double.NaN,0,true,0); Check(false, "Nonfinite link accepted"); }
catch(ArgumentOutOfRangeException) { Check(true, "Unknown-duration music cannot create a linked clock"); }
