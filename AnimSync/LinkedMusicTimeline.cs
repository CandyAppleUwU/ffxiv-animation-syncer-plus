using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AnimSync;

public sealed partial class AnimSync {
    private bool MusicDrivesAnimation(TimelineTrack track) => musicControls.TryGetValue(track.Key, out var c) && c.Link?.MusicMaster == true;
    private void UnlinkMusicTimeline(LocalMusicControl control) {
        if(control.Link == null) return;
        control.Link = null;
        var track = AllTimelines().FirstOrDefault(t => t.Key == control.Observation.Owner.Track);
        if(track != null) {
            track.timelineScrub.Cancel(); track.Repeat.Cancel(); track.timelineRequest = null;
            ReleaseTrack(track);
        }
    }
    private void ToggleMusicTimelineLink(LocalMusicControl control) {
        var track = AllTimelines().FirstOrDefault(t => t.Key == control.Observation.Owner.Track);
        if(track == null) return;
        track.MusicLinkWanted = !track.MusicLinkWanted;
        if(!track.MusicLinkWanted) { UnlinkMusicTimeline(control); return; }
        StartMusicTimelineLink(track, control);
    }
    private void StartMusicTimelineLink(TimelineTrack track, LocalMusicControl control) {
        if(track?.timelineView is not { } view || track.timelineScrub.Active || track.timelineRequest != null || control.Dragging) return;
        track.timelineScrub.Cancel(); track.Repeat.Cancel(); track.timelineRequest = null;
        track.SpeedPercent = 100;
        control.Dragging = false;
        var musicMaster = control.Player.Duration >= view.Duration;
        control.Link = new(view.Duration, control.Player.Duration, musicMaster ? control.Player.Position : view.Time,
            musicMaster ? control.Player.Playing : view.Playing, Environment.TickCount64);
    }
    private void UpdateMusicTimelineLink(LocalMusicControl control) {
        var track = AllTimelines().FirstOrDefault(t => t.Key == control.Observation.Owner.Track);
        if(control.Link == null && track?.MusicLinkWanted == true && Environment.TickCount64 >= control.LinkRetryAt)
            StartMusicTimelineLink(track, control);
        if(control.Link is not { } link) return;
        var source = track == null ? null : TrackSource(track);
        if(track == null || source == null || track.timelineView == null ||
            !AnimationSampling.TryReadTimeline(source, out var sample, out _) || Math.Abs(sample.Duration - link.AnimationDuration) >= .001) {
            UnlinkMusicTimeline(control); return;
        }
        link.Tick(Environment.TickCount64);
        foreach(var actor in Objects) {
            if(!track.Scope.Contains(actor.Address) || !IsManualPlayer(actor) || ClaimedByEarlierTrack(track, actor) ||
                !AnimationSampling.TryReadTimeline(actor, out var other, out _) ||
                actor.Address != source.Address && !TimelineRules.Matches(sample, other, config.Distance)) continue;
            var playback = track.timelineActors.FirstOrDefault(p => p.IsActor(actor));
            if(playback == null) { playback = new TimelinePlayback(actor, other); track.timelineActors.Add(playback); }
            if(playback.Valid(actor, other)) playback.ApplyLinked((float)link.AnimationTime, link.Playing, CanSyncSound(actor));
        }
        // Keep the song running normally; only flush/seek on user actions or meaningful drift.
        if(!link.Playing && control.Player.Playing) control.Player.Pause();
        if(link.NeedsSeek || LinkedTimelineClock.LoopDistance(control.Player.Position, link.MusicTime, link.MusicDuration) > .15)
            control.Player.Seek(link.MusicTime);
        if(link.Playing && !control.Player.Playing) control.Player.Play();
        link.Sought();
        track.timelineView = track.timelineView with {
            Time = (float)link.AnimationTime, Frame = TimelineRules.FrameAtTime((float)link.AnimationTime, sample.FrameCount, sample.Duration),
            Playing = link.Playing, Actors = Math.Max(1, track.timelineActors.Count),
        };
    }
    private float DrawMergedMusicTimeline(TimelineTrack track, LocalMusicControl control) {
        var link = control.Link!;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var time = $"{link.Position:0.00}s / {link.Duration:0.00}s";
        ImGui.PushID("MergedMusic");
        var button = ImGui.GetFrameHeight();
        var volumeWidth = ImGui.GetFontSize() * 6;
        var fixedWidth = button * 3 + volumeWidth + ImGui.CalcTextSize(time).X + EqualizerButtonWidth() + spacing * 6;
        var available = ImGui.GetContentRegionAvail().X;
        if(CompactIconButton("MergedPlay", link.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, link.Playing ? "Pause animation and music" : "Play animation and music"))
            QueueMusicControl(control.Observation, link.Playing ? "pause" : "play");
        ImGui.SameLine();
        if(!control.Dragging) control.Seek = (float)link.Position;
        ImGui.SetNextItemWidth(Math.Max(100, available - fixedWidth));
        var changed = ImGui.SliderFloat("##MergedSeek", ref control.Seek, 0, (float)link.Duration, link.MusicMaster ? "Music" : "Animation");
        if(ImGui.IsItemActivated()) {
            control.Dragging = true; control.ResumeAfterDrag = link.Playing;
            QueueMusicControl(control.Observation, "pause");
        }
        if(changed) QueueMusicControl(control.Observation, "seek-master", control.Seek);
        if(ImGui.IsItemDeactivated() && control.Dragging) {
            control.Dragging = false;
            if(control.ResumeAfterDrag) QueueMusicControl(control.Observation, "play");
        }
        ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(time);
        ImGui.SameLine(); DrawMusicLink(control.Observation);
        ImGui.SameLine();
        DrawTimelineVolume("##MergedVolume", volumeWidth, control.Observation.Bus);
        ImGui.SameLine(); DrawMusicEqualizer();
        ImGui.SameLine(); DrawTimelineSpeed(track, linked: true);
        if(!string.IsNullOrEmpty(control.Observation.ControlError)) ImGui.TextWrapped(control.Observation.ControlError);
        ImGui.PopID();
        return fixedWidth + 100 + ImGui.GetStyle().WindowPadding.X * 2;
    }
    private float DrawMusicLink(MusicObservation observation) {
        var hasControl = musicControls.TryGetValue(observation.Owner.Track, out var control);
        var linked = control?.Link != null;
        ImGui.BeginDisabled(!hasControl || !AllTimelines().Any(t => t.Key == observation.Owner.Track && t.timelineView != null));
        if(linked) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(.15f, .55f, .3f, 1));
        var size = ImGui.GetFrameHeight();
        var clicked = CompactIconButton("MusicLink", FontAwesomeIcon.Link, string.Empty);
        if(linked) ImGui.PopStyleColor();
        ImGui.EndDisabled();
        if(clicked) QueueMusicControl(observation, "link");
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(linked
            ? $"Linked: {(control!.Link!.MusicMaster ? "music" : "animation")} controls both. The shorter timeline loops. Click to unlink and restore normal animation playback."
            : "Link animation and music. The longer timeline controls seeking; play/pause controls both. The shorter one follows its loop position.");
        return size;
    }
}
