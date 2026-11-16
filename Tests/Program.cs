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
Check(Vector3.Distance(PositionRules.LocalOffset(new Vector3(2, 3, 4), new Vector3(3, 5, 4), MathF.PI / 2), new Vector3(0, 2, 1)) < .0001f,
    "Bone alignment converts the world position difference into SimpleHeels local axes");
Check(PositionRules.TryParseBlenderOffset("AnimSyncAlign j_kao iv_ochinko_f 0.0067 0.0501 0.0301", out var blenderOffset) &&
    Vector3.Distance(blenderOffset, new Vector3(0.0067f, 0.0501f, 0.0301f)) < 1e-6f, "Bone alignment reads the head offset line copied from Blender");
Check(PositionRules.TryParseBlenderOffset("0.1, -0.2, 0.3", out var plainOffset) && plainOffset == new Vector3(0.1f, -0.2f, 0.3f), "Bone alignment accepts three plain numbers");
Check(!PositionRules.TryParseBlenderOffset("AnimSyncAlign j_kao 0.1 0.2", out _) && !PositionRules.TryParseBlenderOffset("", out _) &&
    !PositionRules.TryParseBlenderOffset("1 2 NaN", out _) && !PositionRules.TryParseBlenderOffset("10 0 0", out _), "Bone alignment rejects incomplete, invalid or huge offsets");
Check(Vector3.Distance(PositionRules.AlignTarget(new Vector3(1, 2, 3), new Vector3(0, 0.05f, 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)),
    new Vector3(1.1f, 2.05f, 3)) < 1e-5f, "Bone alignment turns the Blender offset with the partner's facing");
Check(PositionRules.TryParseBlenderAlign("AnimSyncAlign j_f_ulip_01_l+j_f_ulip_01_r+j_f_dlip_01_l+j_f_dlip_01_r iv_ochinko_d 0.001 -0.002 0.003", out var alignBones, out var alignPartner, out var alignOffset) &&
    alignBones.Length == 4 && alignBones[3] == "j_f_dlip_01_r" && alignPartner == "iv_ochinko_d" && alignOffset == new Vector3(0.001f, -0.002f, 0.003f),
    "Bone alignment reads the chosen bones from the Blender line");
Check(PositionRules.TryParseBlenderAlign("0.1 0.2 0.3", out var defaultBones, out var defaultPartner, out _) && defaultBones.Single() == "j_kao" && defaultPartner == "iv_ochinko_f",
    "Bone alignment keeps the head and tip when the line names no bones");
Check(PositionRules.SimpleHeelsAddCommand(new Vector3(-0.0123f, 0.05f, 0.1f)) == "/heels temp add height 0.05 forward 0.1 left -0.0123 silent",
    "Bone alignment builds the SimpleHeels add command with its axes and invariant numbers");
Check(BoneAlignPresets.BuiltIn.Single(p => p.Name == "Blow Job").Line.EndsWith("iv_ochinko_f -0.00447 -0.01078 -0.00143") &&
    PositionRules.TryParseBlenderAlign(BoneAlignPresets.BuiltIn[0].Line, out var presetBones, out _, out _) && presetBones.Length == 4, "The Blow Job preset is built in and parses");
Check(BoneAlignPresets.SimpleHeelsSetCommand(new Vector3(-0.01f, 0.02f, 0.03f), -12.5f) == "/heels temp set height 0.02 forward 0.03 left -0.01 rotate -12.5 silent",
    "Manual controls set the whole SimpleHeels temp offset including rotation");
Check(BoneAlignPresets.ShouldSend(true, 1.0, 0.85) && !BoneAlignPresets.ShouldSend(true, 1.0, 0.95) && !BoneAlignPresets.ShouldSend(false, 5, 0),
    "Live SimpleHeels updates are throttled and only sent when something changed");
Check(BoneAlignPresets.LooksKey(1, 0, 1, 50, 2) != BoneAlignPresets.LooksKey(1, 1, 1, 50, 2) &&
    BoneAlignPresets.LooksKey(1, 0, 1, 50, 2) != BoneAlignPresets.LooksKey(1, 0, 1, 51, 2) &&
    BoneAlignPresets.LooksKey(1, 0, 1, 50, 2) != BoneAlignPresets.LooksKey(2, 0, 1, 50, 2) &&
    BoneAlignPresets.LooksKey(1, 0, 1, 50, 2) == BoneAlignPresets.LooksKey(1, 0, 1, 50, 2), "Bone Align notices gender, height and race changes");
Check(BoneAlignPresets.SimpleHeelsSetCommand(Vector3.Zero, 0) == "/heels temp set height 0 forward 0 left 0 rotate 0 silent",
    "Bone Align zeroes SimpleHeels before measuring");
Check(!BoneAlignPresets.Settled(null, Vector3.Zero) && BoneAlignPresets.Settled(new Vector3(0.0003f, 0, 0), Vector3.Zero) &&
    !BoneAlignPresets.Settled(new Vector3(0.002f, 0, 0), Vector3.Zero), "Bone Align only measures once the pose has stopped changing");
Check(BoneAlignPresets.ModelKey(5, 100, 200, 10000) != BoneAlignPresets.ModelKey(5, 101, 200, 10000) &&
    BoneAlignPresets.ModelKey(5, 100, 200, 10000) != BoneAlignPresets.ModelKey(5, 100, 200, 10500) &&
    BoneAlignPresets.ModelKey(5, 100, 200, 10000) == BoneAlignPresets.ModelKey(5, 100, 200, 10000), "Bone Align waits for the redrawn model, not just the new settings");
Check(PositionRules.TryParseBlenderAlign("AnimSyncAlign j_kao iv_ochinko_f 0.01 0.02 0.03 -15.5", out _, out _, out var yawOffset, out var yawDegrees) &&
    yawOffset == new Vector3(0.01f, 0.02f, 0.03f) && yawDegrees == -15.5f, "Bone Align reads an optional facing angle from the Blender line");
Check(PositionRules.TryParseBlenderAlign(BoneAlignPresets.BuiltIn[0].Line, out _, out _, out _, out var presetYaw) && presetYaw == 0 &&
    !PositionRules.TryParseBlenderOffset("1 2 3 4 5", out _), "Lines without an angle face the same way; extra numbers are rejected");
Check(BoneAlignPresets.WrapDegrees(190) == -170 && BoneAlignPresets.WrapDegrees(-190) == 170 && BoneAlignPresets.WrapDegrees(45) == 45,
    "Bone Align keeps its rotation between -180 and 180 degrees");
Check(PositionRules.TryParseBlenderAlign(BoneAlignPresets.BuiltIn.Single(p => p.Name == "Ass").Line, out var testBones, out var testPartner, out var testOffset) &&
    testBones.Single() == "iv_koumon" && testPartner == "iv_ochinko_d" && testOffset == Vector3.Zero, "The Ass preset puts iv_koumon right on iv_ochinko_d");
Check(PositionRules.TryParseBlenderAlign(BoneAlignPresets.BuiltIn.Single(p => p.Name == "Pussy").Line, out var pussyBones, out var pussyPartner, out var pussyOffset) &&
    pussyBones.Single() == "iv_ochinko_d" && pussyPartner == "iv_omanko" && pussyOffset == Vector3.Zero, "The Pussy preset puts iv_ochinko_d right on iv_omanko");
Check(Vector3.Distance(PositionRules.ReverseAlignTarget(new Vector3(1, 2, 3), new Vector3(0, 0.05f, 0.1f), MathF.PI / 2), new Vector3(0.9f, 1.95f, 3)) < 1e-5f &&
    Vector3.Distance(PositionRules.AlignTarget(PositionRules.ReverseAlignTarget(new Vector3(1, 2, 3), new Vector3(0.02f, 0.05f, 0.1f), 0.7f), new Vector3(0.02f, 0.05f, 0.1f),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f)), new Vector3(1, 2, 3)) < 1e-5f, "Reverse align is the exact opposite of normal align");
Check(BoneAlignPresets.ClassifyAnimation("emote/s_pose02_loop") == BoneAlignPresets.AnimationChange.Realign &&
    BoneAlignPresets.ClassifyAnimation("emote/pose01_loop") == BoneAlignPresets.AnimationChange.Realign &&
    BoneAlignPresets.ClassifyAnimation("emote/s_pose02_start") == BoneAlignPresets.AnimationChange.Ignore &&
    BoneAlignPresets.ClassifyAnimation("emote/pose_start") == BoneAlignPresets.AnimationChange.Ignore &&
    BoneAlignPresets.ClassifyAnimation("resident/idle") == BoneAlignPresets.AnimationChange.Cancel &&
    BoneAlignPresets.ClassifyAnimation("resident/move_a") == BoneAlignPresets.AnimationChange.Cancel &&
    BoneAlignPresets.ClassifyAnimation("resident/move_b") == BoneAlignPresets.AnimationChange.Cancel &&
    BoneAlignPresets.ClassifyAnimation("emote/dance01") == BoneAlignPresets.AnimationChange.Ignore,
    "Bone Align re-aligns on pose emotes, cancels on idle/walking and ignores start animations");

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
// ---- Surface Align ----
Check(SurfaceRules.RaceCode(4, 1, 7) == "c0801" && SurfaceRules.RaceCode(1, 1, 2) == "c0401" && SurfaceRules.RaceCode(1, 0, 1) == "c0101" &&
    SurfaceRules.RaceCode(3, 0, 5) == "c1101" && SurfaceRules.RaceCode(8, 1, 15) == "c1801" && SurfaceRules.RaceCode(0, 0, 0) == null,
    "Surface profiles find each race's animation folder");
Check(SurfaceRules.AnimationPath("c0801", "emote/j_pose01_loop") == "chara/human/c0801/animation/a0001/bt_common/emote/j_pose01_loop.pap",
    "Surface profiles build the emote's animation path");
const string wallRideFile = @"C:\Penumbra\[Candy] Wall Ride [GSit  1 2]\main\gsit1\wallride_top_204b\anim.pap";
Check(SurfaceRules.ModFolder(wallRideFile, @"C:\Penumbra") == "[Candy] Wall Ride [GSit  1 2]" &&
    SurfaceRules.ModFolder("|abc_1|" + wallRideFile.ToLowerInvariant(), "C:/Penumbra/") == "[candy] wall ride [gsit  1 2]" &&
    SurfaceRules.ModFolder("chara/human/c0801/animation/a0001/bt_common/emote/j_pose01_loop.pap", @"C:\Penumbra") == null &&
    SurfaceRules.ModFolder(wallRideFile, @"D:\Mods") == null, "The mod playing an animation is read from Penumbra's file path");
var wallRide = SurfaceRules.Find(SurfaceRules.BuiltIn, "[Candy] Wall Ride [GSit  1 2]", "emote/j_pose01_loop");
Check(wallRide is { Kind: SurfaceKind.WallBack } && SurfaceRules.Find(SurfaceRules.BuiltIn, "Other Mod", "emote/j_pose01_loop") == null &&
    SurfaceRules.Find(SurfaceRules.BuiltIn, "[Candy] Wall Ride [GSit  1 2]", "emote/j_pose02_loop") == null,
    "Wall Ride's groundsit 1 is a built-in back-to-the-wall profile, only with that mod");
var anyMod = new SurfaceProfile("Any", "", "emote/sit", SurfaceKind.Floor);
var oneMod = new SurfaceProfile("Mine", "Chair Mod", "emote/sit", SurfaceKind.WallBack);
Check(SurfaceRules.Find(new[] { anyMod, oneMod }, "My Chair Mod v2", "emote/sit") == oneMod &&
    SurfaceRules.Find(new[] { anyMod, oneMod }, null, "/emote/sit/") == anyMod, "A profile naming the mod wins over an any-mod profile");
var wallLine = new[] { new Vector2(-0.3f, 0), new Vector2(0, 0.01f), new Vector2(0.3f, 0) };
var outward = SurfaceRules.FitWallNormal(wallLine, new Vector2(0, 0.4f), new Vector2(0, -1), out var wallSpread);
Check(Vector2.Distance(outward, new Vector2(0, 1)) < 0.05f && wallSpread > 0.25f, "The wall the pose was made for faces the side the body is on");
Check(Vector2.Distance(SurfaceRules.FitWallNormal(new[] { Vector2.Zero, new Vector2(0.02f, 0) }, Vector2.Zero, new Vector2(1, 0), out _), new Vector2(1, 0)) < 1e-4f,
    "Contacts too close together use the character's facing instead");
var wallHits = new List<(Vector3, Vector3, float)> {
    (new Vector3(0, 0, -1), new Vector3(0, 0, 1), 1.0f), (new Vector3(0.2f, 0.5f, -1.02f), new Vector3(0.05f, 0, 1), 1.02f),
    (new Vector3(0, -0.5f, 0), new Vector3(0, 1, 0), 0.5f), (new Vector3(3, 0, -2), new Vector3(1, 0, 0), 2.2f),
};
Check(SurfaceRules.PickWall(wallHits, out var wallAt, out var wallFacing) && Vector2.Distance(wallFacing, new Vector2(0, 1)) < 0.05f &&
    MathF.Abs(wallAt.Z + 1.01f) < 0.02f, "The nearest wall is found from raycast hits, ignoring the floor and farther walls");
Check(!SurfaceRules.PickWall(new List<(Vector3, Vector3, float)> { (Vector3.Zero, Vector3.UnitY, 1) }, out _, out _), "Only floors hit means no wall");
Check(SurfaceRules.FacingNormal(Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), Vector3.Zero) == new Vector3(0, 0, -1),
    "Hit surfaces face back along the ray");
Check(MathF.Abs(SurfaceRules.Yaw(new Vector2(1, 0)) - MathF.PI / 2) < 1e-5f, "Ground directions use the game's yaw convention");
Check(MathF.Abs(SurfaceRules.WallPush(new List<(float, float)> { (0.10f, 0.10f), (-0.05f, 0.03f), (0.6f, 0.03f) }) - 0.08f) < 1e-5f,
    "The deepest bone in the wall (a hand) decides how far out to move; far bones don't count");
Check(MathF.Abs(SurfaceRules.WallPush(new List<(float, float)> { (0.20f, 0.10f), (0.15f, 0.03f) }) + 0.10f) < 1e-5f,
    "Bones floating off the wall move back until the closest one touches");
Check(SurfaceRules.WallPush(new List<(float, float)>()) == 0, "No wall bones, no push");
Check(MathF.Abs(SurfaceRules.FloorLift(new[] { -0.04f, 0.02f }, 0) !.Value - 0.04f) < 1e-6f &&
    MathF.Abs(SurfaceRules.FloorLift(new[] { -0.04f, 0.02f }, 0.1f)!.Value - 0.14f) < 1e-6f &&
    SurfaceRules.FloorLift(Array.Empty<float>(), 0.1f) == null,
    "Feet come out of the floor first, then the shoes' heel height goes on top");
var touchGaps = SurfaceRules.WallTouchBones(0.09f, 0.03f).ToDictionary(t => t.Bone, t => t.Gap);
Check(touchGaps["j_sebo_c"] == 0.09f && touchGaps["j_kosi"] == 0.09f && touchGaps["j_te_l"] == 0.03f &&
    MathF.Abs(touchGaps["j_ude_b_r"] - 0.04f) < 1e-6f && MathF.Abs(touchGaps["j_naka_b_l"] - 0.02f) < 1e-6f &&
    SurfaceRules.WallTouchBones(0.09f, 0f).All(t => t.Gap >= 0), "Back and arm thickness set how far each touch bone stays from the wall");
var floorHits = new List<Vector3>();
for(var fx = -2; fx <= 2; fx++) for(var fz = -2; fz <= 2; fz++) floorHits.Add(new Vector3(fx * 0.25f, 1 + 0.1f * fx * 0.25f, fz * 0.25f));
floorHits.Add(new Vector3(0.1f, 1.6f, 0.1f));   // a bench top
var floorPlane = FloorPlane.Fit(floorHits, new[] { 1f, 1.01f }, out var floorUsed);
Check(floorPlane is { } fp && MathF.Abs(fp.A - 0.1f) < 1e-3f && MathF.Abs(fp.B) < 1e-3f && MathF.Abs(fp.HeightAt(0.5f, 0) - 1.05f) < 1e-3f && floorUsed.Count == 25,
    "The floor is fitted as one sloped surface from many hits, ignoring a bench top");
Check(FloorPlane.Fit(new List<Vector3> { new(0, 2, 0) }, new[] { 2f }, out _) is { A: 0, B: 0, C: 2 } && FloorPlane.Fit(new List<Vector3>(), Array.Empty<float>(), out _) == null,
    "Too few floor hits mean a flat floor at that height; none means no floor");
const string heelsJson = """
{"WorldCharacterDictionary":{"73":{"Candy Apple":{"Enabled":true,"HeelsConfig":[
 {"Enabled":true,"ModelId":6012,"Offset":0.14,"Slot":3,"PathMode":false},
 {"Enabled":false,"ModelId":5100,"Offset":0.2,"Slot":4},
 {"Enabled":true,"ModelId":5100,"Offset":0.05,"Slot":4},
 {"Enabled":true,"PathMode":true,"Path":"chara/equipment/e0500/model/c0201e0500_sho.mdl","Offset":0.08,"Slot":"Feet"}]}}}}
""";
Check(SurfaceRules.ShoePreset(heelsJson, "Candy Apple", 73, 5100, null) == 0.05f, "Only an enabled preset for the shoes worn counts");
Check(SurfaceRules.ShoePreset(heelsJson, "Candy Apple", 73, 6012, null) == null, "A leg-gear preset isn't a shoe heel (flat slides stay at 0)");
Check(SurfaceRules.ShoePreset(heelsJson, "Candy Apple", 73, 1, "|x_1|chara\\equipment\\e0500\\model\\c0201e0500_sho.mdl") == 0.08f, "Path presets match the shoes' model file");
Check(SurfaceRules.ShoePreset(heelsJson, "Someone Else", 73, 5100, null) == null && SurfaceRules.ShoePreset("not json", "Candy Apple", 73, 5100, null) == null,
    "Other characters' presets and unreadable settings give no heel");
Check(SurfaceRules.ModelHeelAttribute("heels_offset=0.035") == 0.035f && SurfaceRules.ModelHeelAttribute("heels_offset_a_bc") == 0.12f &&
    SurfaceRules.ModelHeelAttribute("atr_foo") == null, "A shoe model's own heels_offset attribute is read");
// ---- visible-wall raycasting ----
var boxVerts = new List<Vector3>();
var boxIdx = new List<int>();
void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { var s = boxVerts.Count; boxVerts.AddRange(new[] { a, b, c, d }); boxIdx.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 }); }
// a wall slab 10 wide, 4 tall, at z = 2 .. 2.2, plus a floor at y = 0
Quad(new(-5, 0, 2), new(5, 0, 2), new(5, 4, 2), new(-5, 4, 2));
Quad(new(-5, 0, 2.2f), new(5, 0, 2.2f), new(5, 4, 2.2f), new(-5, 4, 2.2f));
for(var gx = -5; gx < 5; gx++) for(var gz = -5; gz < 5; gz++) Quad(new(gx, 0, gz), new(gx + 1, 0, gz), new(gx + 1, 0, gz + 1), new(gx, 0, gz + 1));
var wallMesh = new MeshBvh(boxVerts.ToArray(), boxIdx.ToArray());
Check(wallMesh.TriangleCount == 204 && wallMesh.Raycast(new Vector3(0, 1, 0), Vector3.UnitZ, 10, out var wallT, out var wallN) &&
    MathF.Abs(wallT - 2) < 1e-4f && MathF.Abs(MathF.Abs(Vector3.Normalize(wallN).Z) - 1) < 1e-4f, "A ray hits the nearest face of a model through its triangle tree");
Check(wallMesh.Raycast(new Vector3(0.3f, 1, 0.4f), -Vector3.UnitY, 5, out var floorT, out _) && MathF.Abs(floorT - 1) < 1e-4f &&
    !wallMesh.Raycast(new Vector3(0, 1, 0), -Vector3.UnitZ * 1, 4, out _, out _), "Model rays find the floor below and miss where nothing is");
var placedWall = new MeshInstance(wallMesh, new Vector3(100, 5, 50), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(2, 1, 1), "test");
Check(placedWall.Raycast(new Vector3(100, 6, 50), Vector3.UnitX, 10, out var placedHit, out var placedNormal, out var placedDistance) &&
    MathF.Abs(placedDistance - 2) < 1e-3f && Vector3.Distance(placedHit, new Vector3(102, 6, 50)) < 1e-3f && MathF.Abs(Vector3.Normalize(placedNormal).X) > 0.999f &&
    placedWall.Near(new Vector3(101, 6, 50), 0.5f) && !placedWall.Near(new Vector3(130, 6, 50), 1f), "Placed models are moved, turned and scaled into the world for rays");
// a window (many hits on z = 1) with a curtain fold and a frame edge closer to the player
var windowHits = new List<(Vector3, Vector3, float)>();
for(var i = -8; i <= 8; i++) windowHits.Add((new Vector3(i * 0.1f, 1, 1), new Vector3(0, 0, -1), 1));
windowHits.Add((new Vector3(0.6f, 1, 0.5f), new Vector3(-1, 0, 0), 0.8f));    // frame edge, perpendicular
windowHits.Add((new Vector3(-0.5f, 1, 0.6f), new Vector3(0.3f, 0, -1), 0.7f)); // curtain fold
windowHits.Add((new Vector3(-0.5f, 1, 0.62f), new Vector3(0.2f, 0, -1), 0.7f));
Check(SurfaceRules.FitWallPlane(windowHits, new Vector2(0, 0), new Vector2(0, -1), out var winAt, out var winOut, out var winSupport) &&
    Vector2.Distance(winOut, new Vector2(0, -1)) < 0.02f && MathF.Abs(winAt.Z - 1) < 0.01f && winSupport == 17,
    "The wall most hits agree on wins over a closer curtain fold or window-frame edge, facing where you stand");
Check(ShapeRules.Kind(0.45f, 0.98f, 0.4f) == "seat" && ShapeRules.Kind(0.55f, 0.9f, 0.5f) == "seat" && ShapeRules.Kind(0.78f, 1, 0.9f) == "table" &&
    ShapeRules.Kind(1.0f, 1, 0.6f) == "counter" && ShapeRules.Kind(2.2f, 1, 1) == "wall" && ShapeRules.Kind(0.45f, 0.3f, 0.4f) == "edge" &&
    ShapeRules.Kind(0.15f, 1, 0.5f) == "step" && ShapeRules.Kind(1.4f, 1, 0.3f) == "shelf / high ledge",
    "Detected tops are named by real height: seats 0.45-0.55, tables ~0.75, counters ~1");
Check(ShapeRules.Kind(0.25f, 1, 0.4f, ShapeRules.SizeScale(0.5f)) == "seat" && ShapeRules.Kind(0.48f, 1, 0.4f, ShapeRules.SizeScale(0.5f)) == "counter" &&
    ShapeRules.Kind(0.62f, 1, 0.4f, ShapeRules.SizeScale(1.9f)) == "seat" && ShapeRules.SizeScale(1.0f) == 1 && ShapeRules.SizeScale(float.NaN) == 1 &&
    ShapeRules.Kind(0.52f, 1, 0.4f, ShapeRules.SizeScale(1.02f)) == "seat" && ShapeRules.Kind(0.78f, 1, 0.9f, ShapeRules.SizeScale(1.02f)) == "table",
    "Seat and table heights scale with the character's legs (Lalafell lower, Roegadyn higher)");
var ringFound = new List<(int, Vector3, Vector2, float)> {
    (0, new(0, 0.45f, 1), new(0, 1), 0.45f), (1, new(0.2f, 0.45f, 1), new(0, 1), 0.46f), (35, new(-0.2f, 0.45f, 1), new(0, 1), 0.45f),
    (10, new(1, 0.9f, 0), new(1, 0), 0.9f), (11, new(1, 0.9f, -0.2f), new(1, 0), 0.9f), (20, new(0, 2.4f, -1), new(0, -1), 2.4f) };
var ringGroups = ShapeRules.Group(ringFound, 36);
Check(ringGroups.Count == 3 && ringGroups.Any(g => g.Count == 3) && ringGroups.Any(g => g.Count == 2), "Neighbouring directions with the same top join into one object, also across the start of the ring");
var stacked = new List<(int, Vector3, Vector2, float)> { (3, new(0, 0.45f, 1), new(0, 1), 0.45f), (3, new(0, 0.78f, 1.6f), new(0, 1), 0.78f),
    (4, new(0.2f, 0.45f, 1), new(0, 1), 0.45f), (4, new(0.25f, 0.78f, 1.6f), new(0, 1), 0.78f) };
Check(ShapeRules.Group(stacked, 36).Count == 2, "A bench and a table behind it in the same directions stay two objects");
var grid = new float[5 * 5];
for(var gi = 0; gi < grid.Length; gi++) grid[gi] = float.NaN;
foreach(var gi in new[] { 0, 1, 5, 6 }) grid[gi] = 0.75f;           // a table top
foreach(var gi in new[] { 3, 4, 8, 9, 14 }) grid[gi] = 0.45f;       // a bench
grid[24] = 0.9f;                                                     // a candle
var gridRegions = ShapeRules.GridRegions(grid, 5, 0.05f, 4);
Check(gridRegions.Count == 2 && gridRegions.Any(r => r.Count == 4) && gridRegions.Any(r => r.Count == 5),
    "Tops seen from above join into objects by height; tiny ones are dropped");
// ---- area map ----
var area = new AreaGrid(12, 0.15f, 10, 20);
for(var cellIndex = 0; cellIndex < area.Cells; cellIndex++) {
    int ax = cellIndex % 12, az = cellIndex / 12;
    if(ax >= 2 && ax <= 5 && az >= 2 && az <= 3) area.Add(cellIndex, 0.48f, 1);                          // a bench
    if(ax >= 7 && ax <= 9 && az >= 6 && az <= 9) { area.Add(cellIndex, 0.75f, 1); area.Add(cellIndex, 0.62f, 1); }   // a table top and its apron's underside
    area.Add(cellIndex, ax == 11 ? 0.03f : 0f, 1);                                                       // the floor, with a rug along one side
}
var areaParts = AreaRules.Components(area, 0.06f, 0.3f);
var areaFloor = AreaRules.FloorIndex(areaParts, area, 0.1f);
Check(areaParts.Count == 3 && areaFloor >= 0 && areaParts[areaFloor].Count == 144 && areaParts.Any(part => part.Count == 8) && areaParts.Any(part => part.Count == 12),
    "The area map finds the floor (rug and all), a bench and a table top, and ignores the underside of the table");
var benchNode = AreaRules.NodeUnder(area, 10 + 3 * 0.15f, 0.5f, 20 + 2 * 0.15f);
Check(benchNode >= 0 && MathF.Abs(area.Y[benchNode] - 0.48f) < 1e-5f && !areaParts[areaFloor].Contains(benchNode) &&
    areaParts[areaFloor].Contains(AreaRules.NodeUnder(area, 10, 0.02f, 20)) && AreaRules.NodeUnder(area, 0, 0, 0) == -1,
    "Standing on the bench is told apart from standing on the floor");
Check(AreaRules.FloorIndex(areaParts, area, -1f) == -1 && MathF.Abs(AreaRules.MeanY(area, areaParts.First(part => part.Count == 12)) - 0.75f) < 1e-5f && area.LayerCount() == 144 + 8 + 24,
    "Surfaces above what you stand on can't be the floor");
var blockLoops = AreaRules.Outlines(new HashSet<int> { 0, 1, 12, 13 }, 12);
var splitLoops = AreaRules.Outlines(new HashSet<int> { 0, 30 }, 12);
var ringCells = new HashSet<int>();
for(var rz = 0; rz < 3; rz++) for(var rx = 0; rx < 3; rx++) if(rx != 1 || rz != 1) ringCells.Add(rz * 12 + rx);
var ringLoops = AreaRules.Outlines(ringCells, 12);
Check(blockLoops.Count == 1 && blockLoops[0].Count == 8 && splitLoops.Count == 2 && splitLoops.All(loop => loop.Count == 4) &&
    ringLoops.Count == 2 && ringLoops[0].Count == 12 && ringLoops[1].Count == 4 && blockLoops[0].All(edge => new[] { 0, 1, 12, 13 }.Contains(edge.Cell)),
    "Outlines follow the border of a shape in order: one loop per piece, the outer border first, holes after");
var blockPoints = new List<Vector2>();
for(var bz = 0; bz < 2; bz++) for(var bx = 0; bx < 4; bx++) blockPoints.Add(new Vector2(bx * 0.15f, bz * 0.15f));
var blockSize = AreaRules.Extents(blockPoints, 0.15f);
Check(MathF.Abs(blockSize.Long - 0.6f) < 1e-4f && MathF.Abs(blockSize.Short - 0.3f) < 1e-4f && AreaRules.Extents(new List<Vector2>(), 0.15f) == (0f, 0f),
    "An object's size is measured along its own direction");
// stairs: four treads 0.25 apart climbing from the floor, a seat beside them, a small footstool
var stairs = new AreaGrid(14, 0.15f, 0, 0);
for(var stairCell = 0; stairCell < stairs.Cells; stairCell++) {
    int sx = stairCell % 14, sz = stairCell / 14;
    if(sx >= 4 && sx <= 11 && sz >= 2 && sz <= 9) stairs.Add(stairCell, 0.25f * ((sz - 2) / 2 + 1), 1);   // treads, two points deep
    else if(sx <= 1 && sz >= 5 && sz <= 8) { stairs.Add(stairCell, 0.48f, 1); stairs.Add(stairCell, 0, 1); }   // a bench
    else if(sx <= 1 && sz <= 1) { stairs.Add(stairCell, 0.25f, 1); stairs.Add(stairCell, 0, 1); }              // a footstool
    else stairs.Add(stairCell, 0, 1);
}
var stairParts = AreaRules.Components(stairs, 0.06f, 0.3f);
var areaTable = areaParts.ToList().FindIndex(part => part.Count == 12);
Check(AreaRules.FloorIndex(areaParts, area, 10f, areaTable, 10) == areaTable && AreaRules.FloorIndex(areaParts, area, 10f, areaTable, 100) == AreaRules.FloorIndex(areaParts, area, 10f) &&
    AreaRules.FloorIndex(areaParts, area, 10f, -1, 1) == AreaRules.FloorIndex(areaParts, area, 10f),
    "What you stand on is the floor when it's big enough to be one, whatever lies below; a small top isn't");
var stairFloor = AreaRules.FloorIndex(stairParts, stairs, 0.1f);
var stairGround = AreaRules.Ground(stairs, stairParts, stairFloor, 0.3f, 12);
Check(stairParts.Count == 7 && stairGround.Count == 5 && stairGround.Contains(stairFloor) &&
    stairGround.All(index => index == stairFloor || stairParts[index].Count == 16) &&
    AreaRules.Ground(stairs, stairParts, -1, 0.3f, 12).Count == 0,
    "Stairs are ground you walk on, tread by tread; a bench and a footstool beside them stay objects");
Check(AreaRules.Covered(area, areaParts.First(part => part.Count == 12), 0.9f) == 0 && AreaRules.Covered(area, areaParts[areaFloor], 0.9f) > 0.1f &&
    AreaRules.Covered(area, new[] { 7 * AreaGrid.MaxLayers * 12 + 7 * AreaGrid.MaxLayers + 1 }, 0.9f) == 1 && AreaRules.Covered(area, Array.Empty<int>(), 0.9f) == 0,
    "A surface tucked under another one (a table's foot under its top) is told apart from an open one");
// tops and undersides: a slab whose underside is far enough below its top to look like a surface of its own
var slab = new AreaGrid(6, 0.15f, 0, 0);
for(var slabCell = 0; slabCell < slab.Cells; slabCell++) {
    if(slabCell % 6 >= 2) { slab.Add(slabCell, 1.1f, -1); slab.Add(slabCell, 0.76f, 1); }   // faces wound the other way round: top is "-1"
    slab.Add(slabCell, 0, -1);
}
slab.OrientUp();
var slabParts = AreaRules.Components(slab, 0.06f, 0.3f);
Check(slabParts.Count == 2 && slabParts.Any(part => part.Count == 24 && MathF.Abs(AreaRules.MeanY(slab, part) - 1.1f) < 1e-5f) &&
    slabParts.Any(part => part.Count == 36) && AreaRules.NodeUnder(slab, 0.45f, 0.5f, 0) % AreaGrid.MaxLayers == 2,
    "The underside of a ledge is never taken for a surface, whichever way round the model's faces are");
var railGap = AreaRules.Close(new HashSet<int> { 2 * 12 + 0, 2 * 12 + 1, 2 * 12 + 4, 2 * 12 + 5 }, 12);
var railWide = AreaRules.Close(new HashSet<int> { 2 * 12 + 0, 2 * 12 + 1, 2 * 12 + 5, 2 * 12 + 6 }, 12);
Check(railGap.Count == 6 && railGap.Contains(2 * 12 + 2) && railGap.Contains(2 * 12 + 3) && railWide.Count == 4,
    "Small gaps in a narrow rail are bridged; wide ones and the outside are left alone");
var diagonal = new AreaGrid(6, 0.15f, 0, 0);
for(var step = 0; step < 5; step++) diagonal.Add(step * 6 + step, 1f, 1);
Check(AreaRules.Components(diagonal, 0.06f, 0.3f).Count == 1, "A rail running corner to corner across the grid is one surface");
Check(AreaRules.Close(new HashSet<int> { 4 * 12 + 0, 4 * 12 + 1, 4 * 12 + 6, 4 * 12 + 7 }, 12, 2).Count == 8, "A longer reach bridges wider gaps in a rail");
// ---- the solid 3D picture ----
static void VoxelQuad(VoxelGrid target, Vector3 a, Vector3 b, Vector3 c, Vector3 d) { target.AddTriangle(a, b, c); target.AddTriangle(a, c, d); }
static void FloodAll(VoxelGrid target, Vector3 from) { if(target.BeginFlood(from)) while(!target.StepFlood(1000)) { } }
var room3d = new VoxelGrid(Vector3.Zero, 20, 20, 20, 0.05f);
VoxelQuad(room3d, new(-1, 0.26f, -1), new(2, 0.26f, -1), new(2, 0.26f, 2), new(-1, 0.26f, 2));                  // a floor right across
VoxelQuad(room3d, new(0.32f, 0.67f, 0.32f), new(0.68f, 0.67f, 0.32f), new(0.68f, 0.67f, 0.68f), new(0.32f, 0.67f, 0.68f));   // a box on it: top...
VoxelQuad(room3d, new(0.32f, 0.26f, 0.32f), new(0.68f, 0.26f, 0.32f), new(0.68f, 0.67f, 0.32f), new(0.32f, 0.67f, 0.32f));   // ...and four sides
VoxelQuad(room3d, new(0.32f, 0.26f, 0.68f), new(0.68f, 0.26f, 0.68f), new(0.68f, 0.67f, 0.68f), new(0.32f, 0.67f, 0.68f));
VoxelQuad(room3d, new(0.32f, 0.26f, 0.32f), new(0.32f, 0.26f, 0.68f), new(0.32f, 0.67f, 0.68f), new(0.32f, 0.67f, 0.32f));
VoxelQuad(room3d, new(0.68f, 0.26f, 0.32f), new(0.68f, 0.26f, 0.68f), new(0.68f, 0.67f, 0.68f), new(0.68f, 0.67f, 0.32f));
FloodAll(room3d, new Vector3(0.1f, 0.6f, 0.1f));
Check((room3d.At(3, 5, 3) & (VoxelGrid.Level | VoxelGrid.Flat)) == (VoxelGrid.Level | VoxelGrid.Flat) && (room3d.At(6, 9, 10) & VoxelGrid.Side) != 0 &&
    (room3d.At(10, 13, 10) & VoxelGrid.Level) != 0 && room3d.At(3, 9, 3) == VoxelGrid.Free && room3d.At(40, 0, 0) == 0,
    "Triangles mark the cubes they pass through, and which way they face");
Check(room3d.IsFree(10, 14, 10) && room3d.IsFree(3, 6, 3) && !room3d.IsFree(10, 9, 10) && room3d.At(10, 9, 10) == 0 && !room3d.IsFree(3, 3, 3) && !room3d.IsFree(10, 3, 10),
    "Open space is what you can reach: not the inside of a box, not under the floor");
var slope3d = new VoxelGrid(Vector3.Zero, 20, 20, 20, 0.05f);
VoxelQuad(slope3d, new(-1, -0.2f, -1), new(2, 1.3f, -1), new(2, 1.3f, 2), new(-1, -0.2f, 2));
FloodAll(slope3d, new Vector3(0.1f, 0.9f, 0.5f));
var slopeLeaks = 0;
for(var lx = 0; lx < 20; lx++) for(var lz = 0; lz < 20; lz++) if(slope3d.IsFree(lx, 0, lz) || (lx > 4 && slope3d.IsFree(lx, 2, lz))) slopeLeaks++;
Check(slope3d.IsFree(2, 19, 10) && slope3d.IsFree(10, 16, 10) && slopeLeaks == 0, "A sloping surface has no pinholes for open space to leak through");
room3d.Reset(new Vector3(5, 0, 0));
Check(room3d.At(3, 5, 3) == 0 && room3d.At(3, 9, 3) == 0 && room3d.Origin.X == 5, "A grid can be emptied and used again");
var squareLoop = new List<Vector3>();
for(var sq = 0; sq < 10; sq++) squareLoop.Add(new Vector3(sq * 0.05f, 1, 0));
for(var sq = 0; sq < 10; sq++) squareLoop.Add(new Vector3(0.5f, 1, sq * 0.05f));
for(var sq = 0; sq < 10; sq++) squareLoop.Add(new Vector3(0.5f - sq * 0.05f, 1, 0.5f));
for(var sq = 0; sq < 10; sq++) squareLoop.Add(new Vector3(0, 1, 0.5f - sq * 0.05f));
var smoothLoop = AreaRules.Smooth(squareLoop, 2, 0.012f);
Check(smoothLoop.Count >= 4 && smoothLoop.Count <= 20 && smoothLoop.All(point => point.Y == 1 && point.X > -0.01f && point.X < 0.51f) && AreaRules.Smooth(squareLoop.Take(3).ToList(), 2, 0.01f).Count == 3,
    "Outlines are rounded off and thinned to the points that matter");
var roomy = new AreaGrid(2, 0.05f, 0, 0);
roomy.Add(0, 1f, 1, 0.2f); roomy.Add(1, 1f, 1, 2f);
Check(!AreaRules.Usable(roomy, 0, 0.3f) && AreaRules.Usable(roomy, AreaGrid.MaxLayers, 0.3f) && AreaRules.Covered(roomy, new[] { 0, AreaGrid.MaxLayers }, 0.9f) == 0.5f,
    "A surface with too little open space above it isn't one you can use");
// ---- seats: backrest, armrests, front ----
var tilted = new List<Vector2>();
for(var tu = -9; tu <= 9; tu++) for(var tv = -3; tv <= 3; tv++)
    tilted.Add(new Vector2(5, 7) + new Vector2(MathF.Cos(MathF.PI / 6), MathF.Sin(MathF.PI / 6)) * (tu * 0.1f) + new Vector2(-MathF.Sin(MathF.PI / 6), MathF.Cos(MathF.PI / 6)) * (tv * 0.1f));
var tiltedBox = AreaBox.Fit(tilted, 0.05f);
Check(Vector2.Distance(tiltedBox.Centre, new Vector2(5, 7)) < 0.01f && MathF.Abs(tiltedBox.HalfLong - 0.95f) < 0.01f && MathF.Abs(tiltedBox.HalfShort - 0.35f) < 0.01f &&
    MathF.Abs(MathF.Abs(Vector2.Dot(tiltedBox.Long, new Vector2(MathF.Cos(MathF.PI / 6), MathF.Sin(MathF.PI / 6)))) - 1) < 0.01f &&
    tiltedBox.SideOf(tiltedBox.Centre + tiltedBox.Long * 0.9f) == 0 && tiltedBox.SideOf(tiltedBox.Centre - tiltedBox.Short * 0.3f) == 3 &&
    Vector2.Distance(tiltedBox.Outward(1), -tiltedBox.Long) < 1e-5f && tiltedBox.Reach(2) == tiltedBox.HalfShort && tiltedBox.HalfSide(2) == tiltedBox.HalfLong,
    "A seat turned at an angle still gets a rectangle along its own sides");
var couch = SeatRules.Read(new[] { 0.2f, 0.22f, 0.4f, 0f }, new[] { 1f, 0.9f, 1f, 0f }, 1.8f, 0.6f, 0, 1);
var chair = SeatRules.Read(new[] { 0f, 0f, 0f, 0.45f }, new[] { 0f, 0.1f, 0f, 1f }, 0.5f, 0.45f, 0, 1);
var benchSeat = SeatRules.Read(new[] { 0f, 0f, 0f, 0f }, new[] { 0f, 0f, 0f, 0f }, 1.5f, 0.4f, 1, 1);
var boxedIn = SeatRules.Read(new[] { 0.3f, 0.3f, 0.5f, 0.3f }, new[] { 1f, 1f, 1f, 1f }, 0.6f, 0.6f, 0, 1);
Check(couch.Type == "couch" && couch.Back == 2 && couch.Arms.SequenceEqual(new[] { 0, 1 }) && couch.Front == 3 && couch.Depth == 0.6f && couch.Width == 1.8f,
    "A couch: the tall long side is the backrest, the raised ends are armrests, the open side is the front");
Check(chair.Type == "chair" && chair.Back == 3 && chair.Arms.Length == 0 && chair.Front == 2 && benchSeat.Type == "bench" && benchSeat.Back == -1 && benchSeat.Front == 1 &&
    SeatRules.Read(new[] { 0f, 0f, 0f, 0f }, new[] { 0f, 0f, 0f, 0f }, 0.4f, 0.4f, 2, 1).Type == "stool" && boxedIn.Front == -1 && boxedIn.Back == 2,
    "Chairs, benches and stools are told apart; a seat with no back faces whoever walks up to it");
// ---- snapping a pose to a table ----
Check(SnapRules.IsTable("table") && SnapRules.IsTable("counter") && SnapRules.IsTable("ledge / rail (table height)") && !SnapRules.IsTable("seat") && !SnapRules.IsTable("step") &&
    SnapRules.IsLeanable("seat") && SnapRules.IsLeanable("ledge / rail (seat height)") && !SnapRules.IsLeanable("step") && !SnapRules.IsLeanable("platform"),
    "Tables and counters are what you bend over; other raised tops are a second choice");
Check(SnapRules.BendDirection(new Vector3(0, 0.9f, 0), new Vector3(0.02f, 1.3f, 0.01f), new Vector3(0.03f, 1.5f, 0)) == null &&
    Vector2.Distance(SnapRules.BendDirection(new Vector3(1, 0.9f, 1), new Vector3(1, 1.0f, 1.4f), new Vector3(1, 0.95f, 1.7f))!.Value, new Vector2(0, 1)) < 1e-4f,
    "A pose bends the way its chest and head lean from the hips; an upright one doesn't bend");
var tableEdge = new List<Vector3> { new(0, 0.8f, 0), new(0.25f, 0.8f, 0), new(0.5f, 0.8f, 0), new(0.75f, 0.8f, 0), new(1, 0.8f, 0),
    new(1, 0.8f, 0.5f), new(1, 0.8f, 1), new(0.5f, 0.8f, 1), new(0, 0.8f, 1), new(0, 0.8f, 0.5f) };
Check(SnapRules.NearestEdge(new[] { tableEdge }, new Vector2(0.4f, -1), new Vector2(0.5f, 0.5f), out var edgeAt, out var edgeIn, out var edgeFar) &&
    Vector3.Distance(edgeAt, new Vector3(0.4f, 0.8f, 0)) < 1e-4f && Vector2.Distance(edgeIn, new Vector2(0, 1)) < 0.05f && MathF.Abs(edgeFar - 1) < 1e-4f &&
    !SnapRules.NearestEdge(new List<List<Vector3>>(), Vector2.Zero, Vector2.Zero, out _, out _, out _),
    "The nearest point on a table's edge is found, with the way in over the table");
var roundEdge = new List<Vector3>();
for(var i = 0; i < 60; i++) {
    var angle = i * MathF.Tau / 60;
    var r = i % 2 == 0 ? 1.03f : 0.97f;   // jagged, like an outline made of small squares
    roundEdge.Add(new Vector3(MathF.Sin(angle) * r, 0.8f, MathF.Cos(angle) * r));
}
var roundFrom = new Vector2(MathF.Sin(0.7f), MathF.Cos(0.7f)) * 1.5f;
Check(SnapRules.NearestEdge(new[] { roundEdge }, roundFrom, Vector2.Zero, out _, out var roundIn, out _) &&
    Vector2.Dot(roundIn, -Vector2.Normalize(roundFrom)) > 0.985f,
    "At a round table the way in points at its middle, however jagged the outline");
var chairSide = new[] { new[] { new Vector3(0, 0.5f, 0), new Vector3(0.7f, 0.5f, 0), new Vector3(0.7f, 0.5f, 0.7f), new Vector3(0, 0.5f, 0.7f) } };
var longSide = new[] { new[] { new Vector3(0, 0.5f, 0), new Vector3(3f, 0.5f, 0), new Vector3(3f, 0.5f, 1f), new Vector3(0, 0.5f, 1f) } };
Check(Vector3.Distance(SnapRules.CentreOnSide(chairSide, new Vector3(0.65f, 0.5f, 0), new Vector2(0, 1), 0.3f), new Vector3(0.35f, 0.5f, 0)) < 1e-5f &&
    Vector3.Distance(SnapRules.CentreOnSide(longSide, new Vector3(2.95f, 0.5f, 0), new Vector2(0, 1), 0.3f), new Vector3(2.7f, 0.5f, 0)) < 1e-5f &&
    Vector3.Distance(SnapRules.CentreOnSide(longSide, new Vector3(1.2f, 0.5f, 0), new Vector2(0, 1), 0.3f), new Vector3(1.2f, 0.5f, 0)) < 1e-5f,
    "You settle on the middle of a chair-sized side, and on a longer one where you were but never hanging off its end");
Check(SnapRules.PickCost(1f, new Vector2(0, 1), new Vector2(0, 1)) < SnapRules.PickCost(0.6f, new Vector2(0, 1), new Vector2(0, -0.6f)) &&
    SnapRules.PickCost(0.5f, new Vector2(0, 1), new Vector2(0, 0.5f)) < SnapRules.PickCost(1f, new Vector2(0, 1), new Vector2(0, 1)),
    "What you face is bent over rather than something a little nearer behind you");
Check(MathF.Abs(SnapRules.TableLift(0.8f, new[] { (0.95f, 0.09f, true, 0.3f), (0.78f, 0.03f, false, -0.1f) }) + 0.06f) < 1e-5f &&
    MathF.Abs(SnapRules.TableLift(0.8f, new[] { (0.95f, 0.09f, true, 0.3f), (0.78f, 0.03f, false, 0.2f) }) - 0.05f) < 1e-5f &&
    SnapRules.TableLift(0.8f, Array.Empty<(float, float, bool, float)>()) == 0,
    "The body is raised or lowered to rest on the table, and a hand over the table is never left inside it");
Check(MathF.Abs(SnapRules.EdgeSlide(0.8f, 0.2f, 0.09f, -0.3f, new[] { (-0.3f, 0.7f, "hips"), (-0.2f, 0.5f, "thigh"), (0.1f, 0.7f, "knee") }, out var slideWhat, out var slideFront) - 0.11f) < 1e-5f &&
    slideWhat == "thigh" && MathF.Abs(slideFront + 0.2f) < 1e-5f &&
    MathF.Abs(SnapRules.EdgeSlide(0.8f, 0.2f, 0.09f, -0.3f, new[] { (-0.3f, 0.7f, "hips"), (0.1f, 0.9f, "knee") }, out var lyingWhat, out _) - (0.3f + SnapRules.LyingHipsPast)) < 1e-5f &&
    lyingWhat.StartsWith("hips on the edge"),
    "Legs below the table top meet its edge; a pose lying on the table puts its hips on the edge");
Check(SnapRules.TableHeightFits(0.75f, 0.6f, 1.5f) && SnapRules.TableHeightFits(0.6f, 0.6f, 1.5f) && SnapRules.TableHeightFits(1.5f, 0.6f, 1.5f) &&
    !SnapRules.TableHeightFits(0.45f, 0.6f, 1.5f) && !SnapRules.TableHeightFits(1.7f, 0.6f, 1.5f),
    "Anything from mid-thigh to head height can be bent over; lower or taller can't");
Check(SnapRules.GripScore(Vector3.Zero, new Vector3(0, 0, 0.08f), new Vector3(0.04f, 0, 0.08f), new Vector3(0, 0.03f, 0.08f), new Vector3(0, -0.03f, 0.08f), out var fistCurl) > 0.9f && fistCurl > 0.99f &&
    MathF.Abs(SnapRules.GripScore(Vector3.Zero, new Vector3(0, 0, 0.08f), new Vector3(0, 0, 0.12f), new Vector3(0, 0.03f, 0.08f), new Vector3(0, -0.03f, 0.08f), out _) - 0.5f) < 1e-4f &&
    SnapRules.GripScore(Vector3.Zero, new Vector3(0, 0, 0.08f), new Vector3(0, -0.04f, 0.08f), new Vector3(0.03f, 0, 0.08f), new Vector3(-0.03f, 0, 0.08f), out var cuppedCurl) < 0.1f && cuppedCurl > 0.99f &&
    SnapRules.GripScore(Vector3.Zero, new Vector3(0, 0, 0.08f), new Vector3(0.04f, 0, 0.08f), new Vector3(0, 0, 0.11f), new Vector3(0, 0, 0.05f), out _) < 0.45f &&
    Vector3.Distance(SnapRules.GripCentre(Vector3.Zero, new Vector3(0, 0, 0.08f), new Vector3(0.04f, 0, 0.08f)), new Vector3(0.03f, 0, 0.06f)) < 1e-5f,
    "A hand turned to hold something upright is the best grip (a curled palm-up hand loses to an open one turned right), and what it holds runs in front of the palm");
Check(Vector3.Distance(SnapRules.TurnAbout(new Vector3(1, 2, 1), new Vector3(1, 0, 0), MathF.PI / 2), new Vector3(2, 2, 0)) < 1e-5f &&
    MathF.Abs(SnapRules.BestTurn(new[] { 2f, 0f, 1f, 0f }) - MathF.PI / 2) < 1e-5f && SnapRules.BestTurn(new[] { 1f, 1f, 1f, 1f }) == 0 &&
    MathF.Abs(SnapRules.BestTurn(new[] { 3f, 2f, 2f, 0f }) + MathF.PI / 2) < 1e-5f && SnapRules.BestTurn(Array.Empty<float>()) == 0 &&
    SnapRules.IsSnap(SurfaceKind.Pole) && SnapRules.IsSnap(SurfaceKind.Table) && SnapRules.IsSnap(SurfaceKind.Low) && !SnapRules.IsSnap(SurfaceKind.WallBack) &&
    SnapRules.IsTop(SurfaceKind.Low) && SnapRules.IsTop(SurfaceKind.Table) && !SnapRules.IsTop(SurfaceKind.Pole),
    "Turning round a pole picks the angle with the least in the way, and the smallest turn between equals");
Check(SnapRules.WallFront(new Vector2(0, 1), new Vector3(0, 0.9f, 0), new Vector3(0, 1.3f, 0.2f), new Vector3(0, 1.5f, 0.3f), Array.Empty<Vector3>()) &&
    !SnapRules.WallFront(new Vector2(0, 1), new Vector3(0, 0.9f, 0), new Vector3(0, 1.3f, -0.2f), new Vector3(0, 1.5f, -0.3f), new[] { new Vector3(0, 1.3f, 0.5f) }) &&
    SnapRules.WallFront(new Vector2(0, 1), new Vector3(0, 0.9f, 0), new Vector3(0, 1.3f, 0), new Vector3(0, 1.5f, 0), new[] { new Vector3(0.2f, 1.3f, 0.4f), new Vector3(-0.2f, 1.3f, 0.3f) }) &&
    !SnapRules.WallFront(new Vector2(0, 1), new Vector3(0, 0.9f, 0), new Vector3(0, 1.3f, 0), new Vector3(0, 1.5f, 0), new[] { new Vector3(0.3f, 1.0f, -0.05f), new Vector3(-0.3f, 1.0f, -0.05f) }),
    "A pose leaning forwards or reaching out puts its front to the wall; leaning back or upright with the hands by it, its back");
// ---- locking feet / hands in place through LivePose ----
var limbRoot = new Vector3(10, 5, -3);
var limbTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
var limbScale = new Vector3(1.1f);
var limbFoot = new Vector3(0.12f, 0.08f, 0.05f);
var limbWorld = LimbRules.WorldPoint(limbFoot, limbRoot, limbTurn, limbScale);
var limbLower = LimbRules.Offset(limbWorld, limbFoot, limbRoot - new Vector3(0, 0.22f, 0), limbTurn, limbScale);
var limbTurned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f);
var limbMoved = LimbRules.Offset(limbWorld, limbFoot, limbRoot + new Vector3(0.3f, -0.1f, 0.2f), limbTurned, limbScale);
Check(LimbRules.Offset(limbWorld, limbFoot, limbRoot, limbTurn, limbScale).Length() < 1e-5f &&
    Vector3.Distance(limbLower, new Vector3(0, 0.2f, 0)) < 1e-5f &&
    Vector3.Distance(LimbRules.WorldPoint(limbFoot + limbMoved, limbRoot + new Vector3(0.3f, -0.1f, 0.2f), limbTurned, limbScale), limbWorld) < 1e-4f,
    "A locked foot's offset is zero until the body moves, then exactly what puts it back where it was (height scale and turning included)");
var limbMeasured = Quaternion.CreateFromYawPitchRoll(0.3f, 0.5f, -0.2f);
var limbWanted = LimbRules.DesiredModel(limbTurned, limbTurn * Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f));
var limbSent = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f);
var limbRaw = limbMeasured * Quaternion.Inverse(limbSent);   // the bone without the turn already sent
Check(LimbRules.Angle(limbRaw * LimbRules.RotationFix(limbSent, limbMeasured, limbWanted), limbWanted) < 0.01f &&
    LimbRules.Angle(limbTurned * limbWanted, limbTurn * Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f)) < 0.01f &&
    MathF.Abs(LimbRules.Angle(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)) - 30) < 0.01f,
    "The turn sent for a locked foot brings it back to the way it faced, counting the turn already sent");
var limbStacks = new[] {
    new LimbStack("j_asi_d_l", new Vector3(0, 0.2f, 0), Quaternion.Identity, true),
    new LimbStack("j_te_r", new Vector3(0.01f, 0, 0), Quaternion.Identity, false),
    new LimbStack("j_asi_d_r", Vector3.Zero, Quaternion.Identity, true),
};
var limbJson = System.Text.Json.Nodes.JsonNode.Parse(LimbRules.PoseJson("", 7, 2, limbStacks))!;
var limbPose = limbJson["BodyPoses"]![0]!["Pose"]!.AsArray();
var limbKept = System.Text.Json.Nodes.JsonNode.Parse(LimbRules.PoseJson(
    "{\"BodyPoses\":[{\"TimelineId\":7,\"SecondaryTimelineId\":2,\"Pose\":[{\"BonePoseInfoId\":{\"BoneName\":\"j_asi_d_l\",\"Partial\":0,\"Slot\":0},\"Stacks\":[{\"Propogate\":3}]}]},{\"TimelineId\":9,\"SecondaryTimelineId\":0,\"Pose\":[]}],\"Frozen\":false}",
    7, 2, limbStacks))!;
var limbCursed = System.Text.Json.Nodes.JsonNode.Parse(LimbRules.PoseJson("{\"CursedPose\":[]}", 7, 2, limbStacks))!;
Check((int)limbJson["BodyPoses"]![0]!["TimelineId"]! == 7 && (int)limbJson["BodyPoses"]![0]!["SecondaryTimelineId"]! == 2 && limbPose.Count == 2 &&
    (string)limbPose[0]!["BonePoseInfoId"]!["BoneName"]! == "j_asi_d_l" && (int)limbPose[0]!["Stacks"]![0]!["IK_Arg0"]! == 3 &&
    (double)limbPose[0]!["Stacks"]![0]!["Transform"]!["Position"]!["Y"]! == 0.2 && (double)limbPose[0]!["Stacks"]![0]!["IK_RotationAxis"]!["Z"]! == -1 &&
    (int)limbPose[1]!["Stacks"]![0]!["IK_Arg0"]! == 2 && (bool)limbPose[1]!["Stacks"]![0]!["IK_Enabled"]! &&
    limbKept["BodyPoses"]!.AsArray().Count == 2 && limbKept["BodyPoses"]![0]!["Pose"]!.AsArray().Count == 2 &&
    limbKept["BodyPoses"]![0]!["Pose"]![0]!["Stacks"]!.AsArray().Count == 2 && (bool)limbKept["Frozen"]! == false &&
    limbCursed["CursedPose"]!.AsArray().Count == 2 && limbCursed["BodyPoses"] == null,
    "Locked limbs are added to LivePose's data for the animation playing, keeping what was there and leaving out limbs that haven't moved");
var legTop = new Vector3(0.1f, 1f, 0);
var legKnee = new Vector3(0.1f, 0.52f, 0.06f);
var legFoot = new Vector3(0.1f, 0.06f, 0);
var legKneeTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);   // its own -Z is the model's -X: the hinge
var legFootTurn = Quaternion.CreateFromYawPitchRoll(0.2f, 0.1f, 0);
var (legOffset, legFix) = LimbRules.Solve(legTop, legKnee, legFoot, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0.03f, 0.25f, 0.05f), legFootTurn);
// bend the leg the way the solver says it bends, and see where the foot ends up
static (Vector3, Quaternion) BendLeg(Vector3 top, Vector3 knee, Vector3 foot, Vector3 target, Quaternion footTurn) {
    Quaternion best = Quaternion.Identity; var miss = float.PositiveInfinity;
    for(var step = -1800; step <= 1800; step++) {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, step * MathF.PI / 3600);
        var error = MathF.Abs(Vector3.Distance(knee + Vector3.Transform(foot - knee, turn), top) - Vector3.Distance(target, top));
        if(error < miss) { miss = error; best = turn; }
    }
    var bent = knee + Vector3.Transform(foot - knee, best);
    var swing = LimbRules.FromTo(bent - top, target - top);
    return (top + Vector3.Transform(bent - top, swing), swing * best * footTurn);
}
var (legEnds, legTilted) = BendLeg(legTop, legKnee, legFoot, legFoot + legOffset, legFootTurn);
var (legHigh, _) = LimbRules.Solve(legTop, legKnee, legFoot, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0, -0.6f, 0), legFootTurn);
var legLength = Vector3.Distance(legTop, legKnee) + Vector3.Distance(legKnee, legFoot);
Check(Vector3.Distance(legOffset, new Vector3(0.03f, 0.25f, 0.05f)) < 1e-5f && Vector3.Distance(legEnds, legFoot + legOffset) < 2e-3f &&
    LimbRules.Angle(legTilted, legFootTurn) > 5 && LimbRules.Angle(legTilted * legFix, legFootTurn) < 0.5f &&
    MathF.Abs(Vector3.Distance(legFoot + legHigh, legTop) - legLength * 0.998f) < 1e-4f && legHigh.Y > -0.6f + 0.05f &&
    Vector3.Cross(Vector3.Normalize(legFoot + legHigh - legTop), Vector3.Normalize(legFoot + new Vector3(0, -0.6f, 0) - legTop)).Length() < 1e-4f &&
    LimbRules.Angle(LimbRules.FromTo(Vector3.UnitX, Vector3.UnitZ), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)) < 0.01f,
    "A locked leg bends to keep the foot in place and flat; past its reach it goes straight and the foot comes up with the body, unstretched");
var legThighTurn = Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.1f);
var legWant = LimbRules.DesiredModel(Quaternion.Identity, legFootTurn);
var legReached = LimbRules.SolveTurns(legTop, legKnee, legFoot, legThighTurn, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0.03f, 0.25f, 0.05f), legWant,
    out var turnThigh, out var turnCalf, out var turnFoot);
// turn the bones the way LivePose does (onto each bone's own rotation, what hangs off it following) and see where the foot ends up
var swungThigh = (legThighTurn * turnThigh) * Quaternion.Inverse(legThighTurn);
var kneeAfter = legTop + Vector3.Transform(legKnee - legTop, swungThigh);
var calfCarried = swungThigh * legKneeTurn;
var foldedCalf = (calfCarried * turnCalf) * Quaternion.Inverse(calfCarried);
var footAfter = kneeAfter + Vector3.Transform(Vector3.Transform(legFoot - legKnee, swungThigh), foldedCalf);
var footFacing = foldedCalf * swungThigh * legFootTurn * turnFoot;
Check(legReached && Vector3.Distance(footAfter, legFoot + new Vector3(0.03f, 0.25f, 0.05f)) < 2e-3f && LimbRules.Angle(footFacing, legFootTurn) < 0.2f &&
    !LimbRules.SolveTurns(legTop, legKnee, legFoot, legThighTurn, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0, -0.2f, 0), legWant, out _, out _, out var gaveUp) &&
    gaveUp == Quaternion.Identity,
    "With turns only, the thigh and calf turn to put a locked foot back and the foot stays flat; out of reach, it gives up and turns nothing");
var turnJson = System.Text.Json.Nodes.JsonNode.Parse(LimbRules.PoseJson("", 7, 2, new[] { new LimbStack("j_asi_a_l", Vector3.Zero, turnThigh, true, false) }))!;
Check(turnJson["BodyPoses"]![0]!["Pose"]![0]!["Stacks"]![0]!["IK_Enabled"] == null && (int)turnJson["BodyPoses"]![0]!["Pose"]![0]!["Stacks"]![0]!["Propogate"]! == 3 &&
    (double)turnJson["BodyPoses"]![0]!["Pose"]![0]!["Stacks"]![0]!["Transform"]!["Position"]!["Y"]! == 0,
    "A turn-only entry carries no IK and no movement");
var (legBack, _) = LimbRules.Solve(legTop, legKnee, legFoot, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0, 0, -1.5f), legFootTurn, true);
var (legDeep, _) = LimbRules.Solve(legTop, legKnee, legFoot, legKneeTurn, legFootTurn, -Vector3.UnitZ, new Vector3(0, -0.6f, -0.2f), legFootTurn, true);
Check(MathF.Abs(legBack.Y) < 1e-5f && MathF.Abs(Vector3.Distance(legFoot + legBack, legTop) - legLength * 0.998f) < 1e-4f && legBack.Z < -0.05f && legBack.Z > -1.5f &&
    MathF.Abs(Vector3.Distance(legFoot + legDeep, legTop) - legLength * 0.998f) < 1e-4f && legDeep.Y > -0.6f,
    "A foot put on the floor stays at floor height when its spot is too far: it comes in under the body; only a floor out of reach leaves it hanging");
var freeRoot = limbRoot + new Vector3(0.3f, -0.1f, 0.2f);
var freeAll = LimbRules.Wanted(limbWorld, limbFoot, limbRoot, limbTurn, freeRoot, limbTurn, limbScale, true, true, true, true);
var freeHeight = LimbRules.Wanted(limbWorld, limbFoot, limbRoot, limbTurn, freeRoot, limbTurn, limbScale, false, true, true, true);
var freeTurn = LimbRules.Wanted(limbWorld, limbFoot, limbRoot, limbTurn, limbRoot, limbTurned, limbScale, true, true, true, false);
var keptTurn = LimbRules.Wanted(limbWorld, limbFoot, limbRoot, limbTurn, limbRoot, limbTurned, limbScale, true, true, true, true);
Check(Vector3.Distance(freeAll, LimbRules.Offset(limbWorld, limbFoot, freeRoot, limbTurn, limbScale)) < 1e-6f && freeHeight.Y == 0 && freeHeight.X == freeAll.X && freeHeight.Z == freeAll.Z &&
    MathF.Abs(freeAll.Y - 0.1f / 1.1f) < 1e-4f && freeTurn.Length() < 1e-4f && keptTurn.Length() > 0.01f &&
    LimbRules.Angle(LimbRules.Facing(limbTurn, limbTurn, limbTurned, false), limbTurned) < 0.01f && LimbRules.Angle(LimbRules.Facing(limbTurn, limbTurn, limbTurned, true), limbTurn) < 0.01f,
    "A lock can be let go on one slider: unlocked on height the limb rises and falls with the body, unlocked on rotation it turns with it");

var brioText = System.Text.Json.Nodes.JsonNode.Parse("\"0.1, 0.2, 0.3, 0.9\"");
var brioFields = System.Text.Json.Nodes.JsonNode.Parse("{\"X\":0.1,\"Y\":0.2,\"Z\":0.3,\"W\":0.9}");
var brioTurn = Quaternion.Normalize(new Quaternion(0.3f, -0.2f, 0.5f, 0.7f));
Check(LimbRules.TryReadQuaternion(brioText, out var brioA) && LimbRules.TryReadQuaternion(brioFields, out var brioB) && LimbRules.Angle(brioA, brioB) < 0.01f &&
    LimbRules.TryReadQuaternion(LimbRules.WriteQuaternion(brioTurn, brioText), out var brioC) && LimbRules.Angle(brioC, brioTurn) < 0.01f &&
    LimbRules.TryReadQuaternion(LimbRules.WriteQuaternion(brioTurn, brioFields), out var brioD) && LimbRules.Angle(brioD, brioTurn) < 0.01f &&
    LimbRules.WriteQuaternion(brioTurn, brioFields) is System.Text.Json.Nodes.JsonObject && !LimbRules.TryReadQuaternion(System.Text.Json.Nodes.JsonNode.Parse("\"nonsense\""), out _),
    "Brio pose rotations are read and written back in the shape they came in (text, fields); garbage is refused");
