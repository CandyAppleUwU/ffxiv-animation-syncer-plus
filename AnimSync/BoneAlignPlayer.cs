using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;

namespace AnimSync;

// A small play/pause + frame slider for you and the player you're aligned with,
// shown in the Bone Align window. Requests come from the UI; the playback work runs
// on the framework thread (ProcessAlignPlayer), like the rest of the timeline code.
public sealed partial class AnimSync {
    private sealed record AlignPlayerPair(TimelinePlayback Self, TimelinePlayback Partner);
    private AlignPlayerPair? alignPlayer;
    private bool alignPlayerPaused;
    private bool? alignPlayerPlayRequest;
    private int? alignPlayerSeekRequest;
    private int alignPlayerFrame, alignPlayerFrames;
    private bool alignPlayerAvailable;

    private void ReleaseAlignPlayer() {
        var pair = alignPlayer;
        alignPlayer = null;
        alignPlayerPaused = false;
        if(pair == null) return;
        foreach(var playback in new[] { pair.Self, pair.Partner })
            if(Objects.FirstOrDefault(playback.IsSameActor) is { } actor) playback.Release(actor);
    }

    /// <summary>The closest other player playing an animation that matches yours.</summary>
    private IGameObject? FindAlignPartner(IGameObject self, AnimationSample own, out AnimationSample matched) {
        matched = default;
        if(heelsOffset != null && Objects.FirstOrDefault(o => o.GameObjectId == alignPartnerId) is { } aligned &&
            AnimationSampling.TryReadTimeline(aligned, out var alignedSample, out _) && TimelineRules.Matches(own, alignedSample, config.Distance)) {
            matched = alignedSample;
            return aligned;
        }
        foreach(var candidate in Objects.Where(IsManualPlayer).Where(actor => actor.Address != self.Address)
            .OrderBy(actor => Vector3.DistanceSquared(actor.Position, self.Position)).ThenBy(actor => actor.GameObjectId)) {
            if(!AnimationSampling.TryReadTimeline(candidate, out var sample, out _) || !TimelineRules.Matches(own, sample, config.Distance)) continue;
            matched = sample;
            return candidate;
        }
        return null;
    }

    private void ProcessAlignPlayer() {
        var play = alignPlayerPlayRequest;
        var seek = alignPlayerSeekRequest;
        alignPlayerPlayRequest = null;
        alignPlayerSeekRequest = null;
        alignPlayerAvailable = false;
        var self = ManualSelf();
        if(self == null || !AnimationSampling.TryReadTimeline(self, out var own, out _)) { ReleaseAlignPlayer(); return; }
        var partner = FindAlignPartner(self, own, out var matched);
        if(partner == null) { ReleaseAlignPlayer(); return; }
        if(alignPlayer is { } current && (!current.Self.Valid(self, own) || !current.Partner.IsSameActor(partner) || !current.Partner.Valid(partner, matched)))
            ReleaseAlignPlayer();
        alignPlayerAvailable = true;
        alignPlayerFrames = own.FrameCount;
        if(play != null || seek != null) {
            alignPlayer ??= new(new TimelinePlayback(self, own, FindTimelinePropModels(self)), new TimelinePlayback(partner, matched, FindTimelinePropModels(partner)));
            var pair = alignPlayer;
            var time = seek is { } frame ? TimelineRules.TimeAtFrame(frame, own.FrameCount, own.Duration) : pair.Self.PausedAt ?? own.LocalTime;
            var playing = seek == null && play == true;
            pair.Self.Apply(time, own.LocalTime, playing, pair.Self.PlayRate, 1, CanSyncSound(self));
            pair.Partner.Apply(time, matched.LocalTime, playing, pair.Partner.PlayRate, 1, CanSyncSound(partner));
            alignPlayerPaused = !playing;
        }
        if(alignPlayer is { } held && alignPlayerPaused) {
            held.Self.HoldPause(CanSyncSound(self));
            held.Partner.HoldPause(CanSyncSound(partner));
            alignPlayerFrame = TimelineRules.FrameAtTime(held.Self.PausedAt ?? own.LocalTime, own.FrameCount, own.Duration);
        } else {
            alignPlayerFrame = own.Frame;
        }
    }

    private void DrawAlignPlayer(float width) {
        if(!alignPlayerAvailable || alignPlayerFrames < 2) {
            ImGui.TextDisabled("Play the animation with your partner to use the player.");
            return;
        }
        var paused = alignPlayer != null && alignPlayerPaused;
        if(CompactIconButton("AlignPlayPause", paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, paused ? "Play" : "Pause"))
            alignPlayerPlayRequest = paused;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        var frame = alignPlayerFrame + 1;
        if(ImGui.SliderInt("##AlignFrame", ref frame, 1, alignPlayerFrames, $"%d / {alignPlayerFrames}"))
            alignPlayerSeekRequest = Math.Clamp(frame - 1, 0, alignPlayerFrames - 1);
    }
}
