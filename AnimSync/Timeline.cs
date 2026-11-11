using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Conditions;

namespace AnimSync;

public sealed partial class AnimSync {
    private TimelineWindow timelineWindow = null!;
    private sealed record TimelineView(long Epoch, int Frame, int Count, float Time, float Duration, bool Playing, int Actors);
    private sealed class TimelineTrack {
        public readonly int Key;
        public readonly (ulong Id, nint Address, uint Territory, bool Gpose)? Source;
        public readonly string Name;
        public readonly TimelineStepRepeat Repeat = new();
        public readonly HashSet<nint> Scope = new();
        public readonly List<TimelinePlayback> timelineActors = new();
        public readonly TimelineScrubState timelineScrub = new();
        public long timelineEpoch;
        public (nint Actor, nint Body, nint Animation, uint Territory, bool Gpose, string Key)? timelineIdentity;
        public TimelineView? timelineView;
        public string timelineStatus = "Waiting for a loaded animation.";
        public (long Epoch, int? Frame, bool Play)? timelineRequest;
        public int SpeedPercent = 100;
        public float? PausedAt;
        public (double Time, long Tick)? PropSpeedClock;
        public long? PropControlGapSince;
        public bool MusicLinkWanted = true;
        public TimelineTrack(int key, string name, (ulong, nint, uint, bool)? source = null) { Key = key; Name = name; Source = source; }
    }
    private readonly TimelineTrack primaryTimeline = new(0, "You");
    private readonly List<TimelineTrack> extraTimelines = new();
    private int nextTimelineKey;
    private (ulong Id, nint Address, uint Territory, bool Gpose, string Name)? timelineAddCandidate, timelineAddRequest;
    private string timelineAddStatus = "Select another player to add their timeline.";
    private readonly HashSet<int> removeTimelines = new();
    private bool TimelineAvailable => timelineWindow.IsOpen && Client.IsLoggedIn &&
        !Conditions[ConditionFlag.BetweenAreas] && !Conditions[ConditionFlag.BetweenAreas51];
    private IEnumerable<TimelineTrack> AllTimelines() => new[] { primaryTimeline }.Concat(extraTimelines);
    private bool TimelineOwns(IGameObject actor) => BoneAlignOwns(actor.GameObjectId, actor.Address) ||
        timelineWindow.IsOpen && AllTimelines().Any(t => t.timelineActors.Any(a => a.IsActor(actor)));
    private bool TimelineOwns(ulong id, nint address) => AllTimelines().Any(t => t.timelineActors.Any(a => a.Id == id && a.Address == address));
    private bool ClaimedByEarlierTrack(TimelineTrack track, IGameObject actor) => AllTimelines().TakeWhile(t => t != track).Any(t => t.Scope.Contains(actor.Address));
    private void ResetTimeline() {
        foreach(var track in AllTimelines()) {
            ResetTrack(track);
            track.PausedAt = null;
        }
        timelineAddCandidate = timelineAddRequest = null;
    }
    private IGameObject? TrackSource(TimelineTrack track) {
        if(track.Source is not { } source) return ManualSelf();
        if(source.Territory != Client.TerritoryType || source.Gpose != Client.IsGPosing) return null;
        return Objects.FirstOrDefault(a => a.GameObjectId == source.Id && a.Address == source.Address && IsManualPlayer(a));
    }
    private void RemoveExtraTimeline(TimelineTrack track) { ResetTrack(track); extraTimelines.Remove(track); }
    private void ProcessTimeline() {
        if(!TimelineAvailable) {
            ResetTimeline(); extraTimelines.Clear(); return;
        }
        foreach(var track in extraTimelines.ToArray())
            if(removeTimelines.Contains(track.Key) || TrackSource(track) == null) RemoveExtraTimeline(track);
        removeTimelines.Clear();
        var visible = new List<AnimationSample>();
        foreach(var actor in Objects)
            if(IsManualPlayer(actor) && AnimationSampling.TryReadTimeline(actor, out var sample, out _))
                visible.Add(sample with { Id = (ulong)actor.Address });
        var claimed = new HashSet<ulong>();
        foreach(var track in AllTimelines()) {
            var source = TrackSource(track);
            if(source != null && AnimationSampling.TryReadTimeline(source, out var sample, out _)) {
                track.Scope.Clear();
                var group = TimelineGroups.Select(sample with { Id = (ulong)source.Address }, visible, config.Distance, claimed);
                foreach(var id in group) track.Scope.Add((nint)id);
                claimed.UnionWith(group);
            } else if(source != null && CanBridgePropGap(track, source)) {
                track.Scope.RemoveWhere(address => claimed.Contains((ulong)address));
                claimed.UnionWith(track.Scope.Select(address => (ulong)address));
            } else track.Scope.Clear();
        }
        // An added anchor joining an earlier group no longer needs a separate timeline.
        foreach(var track in extraTimelines.ToArray())
            if(TrackSource(track) is { } source && ClaimedByEarlierTrack(track, source)) RemoveExtraTimeline(track);
        // Restore actors leaving a row before another row can take a fresh speed snapshot.
        foreach(var track in AllTimelines()) foreach(var saved in track.timelineActors.ToArray()) {
            if(track.Scope.Contains(saved.Address)) continue;
            var actor = Objects.FirstOrDefault(saved.IsSameActor);
            if(actor != null) saved.Release(actor);
            track.timelineActors.Remove(saved);
        }
        foreach(var track in AllTimelines()) ProcessTrack(track, TrackSource(track));
        var target = Client.IsGPosing ? Targets.GPoseTarget : Targets.SoftTarget ?? Targets.Target;
        timelineAddCandidate = null;
        timelineAddStatus = "Select another player to add their timeline.";
        if(target != null) {
            if(!IsManualPlayer(target)) timelineAddStatus = "Select a loaded player character.";
            else if(AllTimelines().FirstOrDefault(t => t.Scope.Contains(target.Address)) is { } included)
                timelineAddStatus = $"{target.Name.TextValue} is already included in {included.Name}'s matching group.";
            else if(!AnimationSampling.TryReadTimeline(target, out _, out var reason))
                timelineAddStatus = $"{target.Name.TextValue}: {reason}";
            else {
                timelineAddCandidate = (target.GameObjectId, target.Address, Client.TerritoryType, Client.IsGPosing, target.Name.TextValue);
                timelineAddStatus = $"Add {target.Name.TextValue} and their matching nearby group.";
            }
        }
        if(timelineAddRequest is { } requested) {
            timelineAddRequest = null;
            // Clicking the window can clear a soft target. Validate the captured player, not the new selection.
            var selected = Objects.FirstOrDefault(a => a.GameObjectId == requested.Id && a.Address == requested.Address && IsManualPlayer(a));
            if(requested.Territory == Client.TerritoryType && requested.Gpose == Client.IsGPosing && selected != null &&
                !AllTimelines().Any(t => t.Scope.Contains(selected.Address)) && AnimationSampling.TryReadTimeline(selected, out var addedSample, out _)) {
                var track = new TimelineTrack(++nextTimelineKey, requested.Name, (requested.Id, requested.Address, requested.Territory, requested.Gpose));
                extraTimelines.Add(track);
                var addedGroup = TimelineGroups.Select(addedSample with { Id = (ulong)selected.Address }, visible, config.Distance, claimed);
                foreach(var address in addedGroup) track.Scope.Add((nint)address);
                // Register the new anchor's ownership and arm sound capture BEFORE replaying any cues.
                PrepareTimelineMusicDetection(rescan: true);
                RestartAddedTimeline(track, addedGroup);
                ProcessTrack(track, TrackSource(track));
            }
        }
    }
    private unsafe void RestartAddedTimeline(TimelineTrack track, HashSet<ulong> group) {
        var targets = new List<(IGameObject Actor, ushort Timeline)>();
        foreach(var actor in Objects) {
            if(!group.Contains((ulong)actor.Address) || !IsManualPlayer(actor) || ClaimedByEarlierTrack(track, actor) ||
                !AnimationSampling.TryReadTimeline(actor, out _, out _)) continue;
            var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
            var key = AnimationSampling.TimelineKey(character, ResolveTimelineKey);
            targets.Add((actor, ResolveCurrentTimeline(character, key)));
        }
        // Snapshot all timeline IDs before any replay can replace native controls.
        foreach(var target in targets) {
            RestartActor(target.Actor, target.Timeline, resumePaused: true); // Honors sound toggle and per-player DJ exclusion.
            cooldowns[target.Actor.GameObjectId] = DateTime.UtcNow.AddSeconds(config.CooldownSeconds);
        }
    }
    private void ReleaseTrack(TimelineTrack track) {
        foreach(var saved in track.timelineActors) {
            var actor = Objects.FirstOrDefault(saved.IsSameActor);
            if(actor != null) saved.Release(actor);
        }
        track.timelineActors.Clear();
    }
    private void ResetTrack(TimelineTrack track, bool keepLink = false) {
        if(!keepLink && musicControls.TryGetValue(track.Key, out var music) && music.Link is { } link) {
            if(!link.Playing) track.PausedAt = (float)link.AnimationTime;
            music.Link = null;
        }
        track.timelineScrub.Cancel();
        track.Repeat.Cancel();
        ReleaseTrack(track);
        track.timelineIdentity = null;
        track.timelineRequest = null;
        track.timelineView = null;
        track.PropControlGapSince = null;
        if(!keepLink) track.PropSpeedClock = null;
        track.timelineEpoch++;
    }
    private bool CanBridgePropGap(TimelineTrack track, IGameObject actor) => track.PropSpeedClock.HasValue &&
        track.timelineIdentity is { } previous && previous.Actor == actor.Address &&
        previous.Territory == Client.TerritoryType && previous.Gpose == Client.IsGPosing;

    private unsafe void ProcessTrack(TimelineTrack track, IGameObject? self) {
        if(!TimelineAvailable) {
            ResetTrack(track); return;
        }

        if(self == null) {
            ResetTrack(track);
            track.timelineStatus = "The character is not loaded.";
            return;
        }
        if(!AnimationSampling.TryReadTimeline(self, out var sample, out var reason)) {
            if(CanBridgePropGap(track, self)) {
                var gapNow = Environment.TickCount64;
                track.PropControlGapSince ??= gapNow;
                // A loop can retire the old controller one update before the new one is ready.
                if(gapNow - track.PropControlGapSince.Value < 150) return;
            }
            ResetTrack(track);
            track.timelineStatus = reason;
            return;
        }
        track.PropControlGapSince = null;
        var body = ((Actor*)self.Address)->Control;
        var key = AnimationSampling.TimelineKey((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)self.Address, ResolveTimelineKey);
        var wasLinkedPlaying = musicControls.TryGetValue(track.Key, out var music) && music.Link?.Playing == true;
        var identity = (self.Address, (nint)body, (nint)body->hkaAnimationControl.Binding.ptr->Animation.ptr,
            Client.TerritoryType, Client.IsGPosing, key);
        var continuing = false;
        var reattachProps = false;
        if(track.timelineIdentity != identity) {
            // Some emote loops replace the Havok controller even though the action is still running.
            var sameAction = track.timelineIdentity is { } prior && prior.Actor == self.Address &&
                prior.Territory == Client.TerritoryType && prior.Gpose == Client.IsGPosing &&
                !string.IsNullOrEmpty(key) && prior.Key == key && track.timelineView is { } previous &&
                previous.Count == sample.FrameCount && Math.Abs(previous.Duration - sample.Duration) < .001f;
            continuing = sameAction && track.timelineView?.Playing == true;
            reattachProps = continuing && track.timelineActors.Any(playback => playback.HasProps);
            var pending = track.timelineRequest;
            ResetTrack(track, keepLink: sameAction);
            track.timelineIdentity = identity;
            if(pending is { } savedRequest && (sameAction || track.PausedAt.HasValue))
                track.timelineRequest = (track.timelineEpoch, savedRequest.Frame, savedRequest.Play);
        }

        // A manual reset takes priority and releases a paused animation before restarting it.
        var restarting = restartRequested || bodyRequested || nearYouRequested || nearbyRequested || targetSyncRequest.HasValue;
        if(restarting) {
            if(musicControls.TryGetValue(track.Key, out var linkedMusic)) linkedMusic.Link = null;
            track.timelineScrub.Cancel();
            track.Repeat.Cancel();
            ReleaseTrack(track); track.timelineRequest = null;
            track.PausedAt = null;
            track.PropSpeedClock = null;
        }
        var multiplier = track.SpeedPercent / 100f;
        var linked = music?.Link != null;
        var groupRate = track.timelineActors.FirstOrDefault()?.PlayRate ?? 1;
        var now = Environment.TickCount64;
        if(track.PropSpeedClock is { } clock && track.timelineRequest == null)
            track.PropSpeedClock = (TimelineRules.AdvanceLoop(clock.Time, (now - clock.Tick) / 1000d * groupRate * multiplier, sample.Duration), now);
        foreach(var saved in track.timelineActors.ToArray()) {
            var actor = Objects.FirstOrDefault(saved.IsSameActor);
            AnimationSample other = default;
            var valid = actor != null && IsManualPlayer(actor) &&
                AnimationSampling.TryReadTimeline(actor, out other, out _) && saved.Valid(actor, other) &&
                (actor.Address == self.Address || TimelineRules.Matches(sample, other, config.Distance)) && !ClaimedByEarlierTrack(track, actor);
            if(valid) {
                if(saved.PausedAt is { } pausedAt) {
                    saved.HoldPause(CanSyncSound(actor!));
                    if(saved.Address == self.Address) sample = sample with { LocalTime = pausedAt, Speed = 0 };
                } else if(!linked && track.PropSpeedClock is { } driven && track.timelineRequest == null) {
                    saved.ApplyDriven((float)driven.Time, Math.Max(0, multiplier), CanSyncSound(actor!), loopLayers: true);
                    if(saved.Address == self.Address) sample = sample with { LocalTime = (float)driven.Time, Speed = groupRate * multiplier };
                } else if(!linked && (multiplier != 1 || saved.HasProps) && track.timelineRequest == null) {
                    saved.HoldSpeed(groupRate, multiplier, CanSyncSound(actor!), other.LocalTime);
                    if(multiplier != 1 && saved.Address == self.Address) sample = sample with { Speed = groupRate * multiplier };
                }
                continue;
            }
            if(actor != null) saved.Release(actor);
            track.timelineActors.Remove(saved);
            if(saved.Address == self.Address) {
                ResetTrack(track, keepLink: track.PropSpeedClock.HasValue);
                track.timelineIdentity = identity;
                break;
            }
        }
        var playing = music?.Link?.Playing ?? (track.PropSpeedClock.HasValue || sample.Speed != 0 || wasLinkedPlaying || continuing);
        // A new clip or an unlink releases the old native controls, but keeps the selected speed for this row.
        if(!restarting && music?.Link == null && track.timelineActors.Count == 0) {
            if(track.PausedAt.HasValue) track.timelineRequest ??= (track.timelineEpoch, null, false);
            else if(playing && (multiplier != 1 || reattachProps || track.PropSpeedClock.HasValue)) track.timelineRequest ??= (track.timelineEpoch, null, true);
        }
        if(track.timelineRequest is { } request) {
            track.timelineRequest = null;
            if(request.Epoch == track.timelineEpoch) {
                var targets = new List<(IGameObject Actor, AnimationSample Sample)> { (self, sample) };
                foreach(var actor in Objects) {
                    if(actor.Address == self.Address || !IsManualPlayer(actor) ||
                        !AnimationSampling.TryReadTimeline(actor, out var other, out _) ||
                        !TimelineRules.Matches(sample, other, config.Distance) || ClaimedByEarlierTrack(track, actor)) continue;
                    targets.Add((actor, other));
                }
                // A speed-only change can keep the existing controls and their original speed snapshots.
                var reuse = request.Frame == null && targets.Count == track.timelineActors.Count &&
                    targets.Select((target, index) => track.timelineActors[index].Valid(target.Actor, target.Sample)).All(valid => valid);
                if(!reuse) {
                    ReleaseTrack(track);
                    foreach(var target in targets)
                        track.timelineActors.Add(new TimelinePlayback(target.Actor, target.Sample, FindTimelinePropModels(target.Actor)));
                }
                var rate = track.timelineActors[0].PlayRate;
                float? time = request.Frame is { } frame ? TimelineRules.TimeAtFrame(frame, sample.FrameCount, sample.Duration) :
                    request.Play ? null : Math.Clamp(track.PausedAt ?? sample.LocalTime, 0, sample.Duration);
                track.PausedAt = request.Play ? null : time;
                multiplier = track.SpeedPercent / 100f;
                var position = time ?? (float)(track.PropSpeedClock?.Time ?? sample.LocalTime);
                var driveProps = request.Play && track.timelineActors.Any(playback => playback.HasProps);
                var wasDriven = track.PropSpeedClock.HasValue;
                track.PropSpeedClock = driveProps ? (position, now) : null;
                for(var i = 0; i < targets.Count; i++) {
                    var playback = track.timelineActors[i];
                    var syncCues = CanSyncSound(targets[i].Actor);
                    if(driveProps) playback.ApplyDriven(position, Math.Max(0, multiplier), syncCues, loopLayers: true);
                    else playback.Apply(wasDriven ? position : time, targets[i].Sample.LocalTime, request.Play, rate, multiplier, syncCues);
                }
                // Display the applied position now; normal samples update it on following ticks.
                sample = sample with { LocalTime = position, Speed = request.Play ? rate * multiplier : 0 };
                playing = request.Play;
            }
        }
        track.timelineView = new(track.timelineEpoch, TimelineRules.FrameAtTime(sample.LocalTime, sample.FrameCount, sample.Duration),
            sample.FrameCount, sample.LocalTime, sample.Duration, playing, Math.Max(1, track.timelineActors.Count));
        track.timelineStatus = string.Empty;
    }
    private void RequestTimeline(TimelineTrack track, int frame, bool play) {
        if(musicControls.TryGetValue(track.Key, out var music) && music.Link is { } link) {
            if(link.MusicMaster) return;
            QueueMusicControl(music.Observation, "seek-animation", TimelineRules.TimeAtFrame(frame, track.timelineView!.Count, track.timelineView.Duration));
            QueueMusicControl(music.Observation, play ? "play" : "pause");
            return;
        }
        if(track.timelineView is { } view) track.timelineRequest = (view.Epoch, Math.Clamp(frame, 0, view.Count - 1), play);
    }
    private sealed class TimelineWindow : Window {
        private readonly AnimSync plugin;
        public TimelineWindow(AnimSync plugin) : base("Animation Timeline###AnimSyncTimeline") {
            this.plugin = plugin;
            Size = new Vector2(480, 0);
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 0), MaximumSize = new Vector2(1000, float.MaxValue) };
        }
        public override void Draw() {
            var width = 0f;
            foreach(var track in plugin.AllTimelines().ToArray()) {
                ImGui.PushID(track.Key);
                {
                    if(track != plugin.primaryTimeline) ImGui.Separator();
                    var count = Math.Max(1, track.Scope.Count);
                    var label = $"{track.Name}    Synced | {count} {(count > 1 ? "Players" : "Player")}";
                    width = Math.Max(width, ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().WindowPadding.X * 2);
                    var keep = true;
                    var expanded = track == plugin.primaryTimeline
                        ? DrawSelfHeader(label)
                        : ImGui.CollapsingHeader(label + "###TimelineGroupHeader", ref keep, ImGuiTreeNodeFlags.DefaultOpen);
                    if(track != plugin.primaryTimeline && ImGui.IsItemHovered()) ImGui.SetTooltip("Collapse to hide controls. X removes this timeline and releases its animation/music control.");
                    if(!keep) plugin.removeTimelines.Add(track.Key);
                    if(!expanded || !keep) {
                        track.Repeat.Cancel();
                        if(track.timelineScrub.Finish(track.timelineEpoch) is { } scrubbed)
                            plugin.RequestTimeline(track, scrubbed.Frame, scrubbed.Play);
                        foreach(var control in plugin.musicControls.Values.Where(c => c.Observation.Owner.Track == track.Key && c.Dragging)) {
                            control.Dragging = false;
                            if(control.ResumeAfterDrag) plugin.QueueMusicControl(control.Observation, "play");
                        }
                        ImGui.PopID();
                        continue;
                    }
                }
                if(plugin.musicControls.TryGetValue(track.Key, out var linked) && linked.Link != null) {
                    width = Math.Max(width, plugin.DrawMergedMusicTimeline(track, linked));
                } else {
                    width = Math.Max(width, DrawTrack(track));
                    foreach(var music in plugin.musicObservations.Where(m => m.Owner.Track == track.Key))
                        width = Math.Max(width, plugin.DrawMusicControls(music));
                }
                ImGui.PopID();
            }
            width = Math.Max(width, DrawAddButton());
            FitHeight(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y, width);
        }
        private float DrawTrack(TimelineTrack track) {
            var view = track.timelineView;
            if(view == null) {
                ImGui.TextWrapped(track.timelineStatus);
                return 0;
            }
            var scrub = track.timelineScrub;
            if(scrub.Active && scrub.Epoch != view.Epoch) scrub.Cancel();
            var frame = (scrub.Active ? scrub.Frame : view.Frame) + 1;
            ImGui.SetNextItemWidth(-1);
            ImGui.BeginDisabled(plugin.MusicDrivesAnimation(track));
            var changed = ImGui.SliderInt("##Frame", ref frame, 1, view.Count, $"%d / {view.Count}");
            var activated = ImGui.IsItemActivated();
            if(activated) scrub.Begin(view.Epoch, frame - 1, view.Playing);
            if(scrub.Active && (activated || changed)) {
                scrub.Move(frame - 1);
                plugin.RequestTimeline(track, scrub.Frame, false);
            }
            // Deactivated also covers clicking/releasing without changing the selected frame.
            if(ImGui.IsItemDeactivated() && scrub.Finish(view.Epoch) is { } released)
                plugin.RequestTimeline(track, released.Frame, released.Play);
            ImGui.EndDisabled();
            var rowStart = ImGui.GetCursorPosX();
            var rowWidth = ImGui.GetContentRegionAvail().X;
            var buttonSize = ImGui.GetFrameHeight();
            var rowY = ImGui.GetCursorPosY();
            var timeText = $"{view.Time:0.00}s / {view.Duration:0.00}s";
            var buttonStride = buttonSize + ImGui.GetStyle().ItemSpacing.X;
            var controlsWidth = buttonSize * 5 + ImGui.GetStyle().ItemSpacing.X * 4;
            var sideWidth = Math.Max(ImGui.CalcTextSize(timeText).X, buttonSize) + ImGui.GetStyle().ItemSpacing.X;
            var minimumWidth = MathF.Ceiling(controlsWidth + sideWidth * 2 + ImGui.GetStyle().WindowPadding.X * 2);

            var controlsStart = rowStart + (rowWidth - controlsWidth) / 2;
            ImGui.SetCursorPosY(rowY + (buttonSize - ImGui.GetFontSize()) / 2);
            ImGui.TextUnformatted(timeText);
            void ControlPosition(int index) {
                ImGui.SameLine();
                // SameLine can inherit the time label's baseline; position every icon explicitly.
                ImGui.SetCursorPos(new Vector2(controlsStart + buttonStride * index, rowY));
            }
            ControlPosition(0);
            ImGui.BeginDisabled(plugin.MusicDrivesAnimation(track));
            if(IconButton("TimelineStart", FontAwesomeIcon.FastBackward, buttonSize, "First frame")) plugin.RequestTimeline(track, 0, view.Playing);
            ControlPosition(1);
            StepButton(track, view, "TimelinePrevious", FontAwesomeIcon.StepBackward, buttonSize, -1);
            ControlPosition(2);
            if(IconButton("TimelinePlay", view.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, buttonSize,
                view.Playing ? "Pause" : "Play")) plugin.RequestTimeline(track, view.Frame, !view.Playing);
            ControlPosition(3);
            StepButton(track, view, "TimelineNext", FontAwesomeIcon.StepForward, buttonSize, 1);
            ControlPosition(4);
            if(IconButton("TimelineEnd", FontAwesomeIcon.FastForward, buttonSize, "Last frame")) plugin.RequestTimeline(track, view.Count - 1, false);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.SetCursorPos(new Vector2(rowStart + rowWidth - buttonSize, rowY));
            plugin.DrawTimelineSpeed(track, linked: false);
            ImGui.SetCursorPos(new Vector2(rowStart, rowY + buttonSize));
            ImGui.Dummy(Vector2.Zero);
            return minimumWidth;
        }
        private bool DrawSelfHeader(string label) {
            var expanded = ImGui.CollapsingHeader(label + "###TimelineGroupHeader", ImGuiTreeNodeFlags.DefaultOpen);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Collapse to hide your controls while playback continues.");
            return expanded;
        }
        private float DrawAddButton() {
            const string label = "+ Add Target's Timeline";
            var style = ImGui.GetStyle();
            var width = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
            ImGui.BeginDisabled(plugin.timelineAddCandidate == null);
            if(ImGui.Button(label))
                plugin.timelineAddRequest = plugin.timelineAddCandidate;
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(plugin.timelineAddStatus);
            return width + style.WindowPadding.X * 2;
        }
        private void StepButton(TimelineTrack track, TimelineView view, string id, FontAwesomeIcon icon, float size, int direction) {
            IconButton(id, icon, size, $"{(direction < 0 ? "Previous" : "Next")} frame (hold: 5 frames/second)");
            var active = ImGui.IsItemActive();
            if(ImGui.IsItemActivated())
                plugin.RequestTimeline(track, track.Repeat.Start(view.Epoch, view.Frame, view.Count, ImGui.GetTime(), direction), false);
            else if(active && track.Repeat.Direction == direction && track.Repeat.Poll(view.Epoch, view.Count, ImGui.GetTime()) is { } frame)
                plugin.RequestTimeline(track, frame, false);
            if(!active && track.Repeat.Direction == direction) track.Repeat.Cancel();
        }
        private void FitHeight(float contentBottom, float minimumWidth = 0) {
            // Dalamud scales these constraints; measurements above are already in UI pixels.
            var scale = ImGuiHelpers.GlobalScale;
            var height = MathF.Ceiling(contentBottom + ImGui.GetStyle().WindowPadding.Y) / scale;
            var width = Math.Max(480, minimumWidth / scale);
            SizeConstraints = new WindowSizeConstraints {
                MinimumSize = new Vector2(width, height),
                MaximumSize = new Vector2(Math.Max(1000, width), height),
            };
        }
        private static bool IconButton(string id, FontAwesomeIcon icon, float size, string tooltip) {
            return CompactIconButton(id, icon, tooltip);
        }
    }
}
