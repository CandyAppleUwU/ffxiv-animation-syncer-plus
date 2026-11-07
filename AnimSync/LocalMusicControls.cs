using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace AnimSync;

public sealed partial class AnimSync {
    private sealed class LocalMusicControl(MusicObservation observation, SoundPreviewPlayer player, NativeSoundLease lease) {
        public readonly MusicObservation Observation = observation;
        public readonly SoundPreviewPlayer Player = player;
        public readonly NativeSoundLease Lease = lease;
        public bool Dragging, ResumeAfterDrag;
        public float Seek;
        public LinkedTimelineClock? Link;
        public long LinkRetryAt;
    }
    private readonly Dictionary<int, LocalMusicControl> musicControls = new();
    private sealed record MusicCommand(MusicObservation Observation, string Action, double Value = 0);
    private readonly ConcurrentQueue<MusicCommand> musicCommands = new();
    private static unsafe float GameMusicVolume(int bus) {
        if(bus is < 1 or > 20) return 0;
        var manager = SoundManager.Instance();
        return manager == null || manager->Disabled ? 0 : manager->GetEffectiveVolume((SoundBus)bus);
    }
    private float TimelineMusicLevel(int bus) => MusicVolume.Resolve(config.TimelineMusicVolume, GameMusicVolume(bus));
    private static unsafe float MusicMasterGain() {
        var manager = SoundManager.Instance();
        if(manager == null || manager->Disabled || manager->IsSndMaster) return 0;
        return MusicVolume.Clamp(manager->MasterVolume) * MusicVolume.Clamp(manager->ActiveVolume);
    }
    private unsafe float MusicGain(MusicObservation observation, NativeSoundLease lease) {
        var bus = observation.Bus;
        var manager = SoundManager.Instance();
        if(manager == null || bus is < 1 or > 20) return 0;
        // Match GetEffectiveVolume's native mute predicate. Despite the legacy field names,
        // these bytes are inactive-MUTE flags, not the checked state of the UI checkboxes.
        if(!MusicVolume.PlaybackAllowed(manager->WindowInactive, manager->PlaySoundsWhenWindowInactive,
            manager->BusAlwaysOn[bus], manager->BusMuted[bus])) return 0;
        return MusicVolume.OutputGain(TimelineMusicLevel(bus), MusicMasterGain(), lease.OriginalVolume, observation.Sound?.Volume ?? 1);
    }
    private bool CanControlMusic(MusicObservation observation) => ValidMusicOwner(observation.Owner) &&
        MusicRules.Eligible(observation.Duration, true, false) && (observation.PenumbraOwner
            ? observation.Native != null && soundProbe.TicketCurrent(observation.Native)
            : !musicScanIncomplete && MusicInference.Matches(observation.EvidencePath, observation.Index, Environment.TickCount64, musicCandidates).Count == 1);
    private void ReleaseMusicControl(bool restoreNative = true) {
        foreach(var track in musicControls.Keys.ToArray()) {
            try { ReleaseMusicTrack(track, restoreNative); }
            catch(Exception ex) { Logger.Warning(ex, "Could not release music control for timeline {Track}.", track); }
        }
        while(musicCommands.TryDequeue(out _)) { }
    }
    private void ReleaseMusicTrack(int track, bool restoreNative = true) {
        if(!musicControls.Remove(track, out var control)) return;
        try { UnlinkMusicTimeline(control); }
        finally {
            try { control.Player.Dispose(); }
            finally { control.Lease.Release(restoreNative); }
        }
    }
    private void ProcessMusicControls() {
        foreach(var current in musicControls.Values.ToArray())
            if(!musicObservations.Contains(current.Observation) || !CanControlMusic(current.Observation)) ReleaseMusicTrack(current.Observation.Owner.Track);
        while(musicCommands.TryDequeue(out var command)) {
            try {
                if(!musicObservations.Contains(command.Observation)) continue;
                if(command.Action == "release") {
                    command.Observation.Auto.Release();
                    command.Observation.ControlError = string.Empty;
                    ReleaseMusicTrack(command.Observation.Owner.Track); continue;
                }
                if(!CanControlMusic(command.Observation)) continue;
                if(command.Action == "take") {
                    command.Observation.Auto.Retry(Environment.TickCount64);
                } else if(musicControls.TryGetValue(command.Observation.Owner.Track, out var active) && active.Observation == command.Observation) {
                    switch(command.Action) {
                        case "link": ToggleMusicTimelineLink(active); break;
                        case "seek-master": active.Link?.Seek(command.Value, Environment.TickCount64); break;
                        case "play":
                            if(active.Link != null) active.Link.SetPlaying(true, Environment.TickCount64); else active.Player.Play(); break;
                        case "pause":
                            if(active.Link != null) active.Link.SetPlaying(false, Environment.TickCount64); else active.Player.Pause(); break;
                        case "seek":
                            if(active.Link == null) active.Player.Seek(command.Value);
                            else if(active.Link.MusicMaster) active.Link.Seek(command.Value, Environment.TickCount64);
                            break;
                        case "seek-animation":
                            if(active.Link is { MusicMaster: false }) active.Link.Seek(command.Value, Environment.TickCount64);
                            break;
                    }
                }
                command.Observation.ControlError = string.Empty;
            } catch(Exception ex) {
                ReleaseMusicTrack(command.Observation.Owner.Track);
                command.Observation.ControlError = ex.Message;
                Logger.Warning(ex, "Local music control test failed.");
            }
        }
        foreach(var observation in musicObservations) {
            var now = Environment.TickCount64;
            if(musicControls.ContainsKey(observation.Owner.Track) || !observation.Auto.CanAttempt(now) || !CanControlMusic(observation)) continue;
            observation.Auto.Attempted(now);
            try { TakeMusicControl(observation); observation.ControlError = string.Empty; }
            catch(Exception ex) { observation.ControlError = ex.Message; }
        }
        foreach(var playing in musicControls.Values) {
            try { UpdateMusicTimelineLink(playing); playing.Player.Volume = MusicGain(playing.Observation, playing.Lease); }
            catch(Exception ex) { UnlinkMusicTimeline(playing); playing.LinkRetryAt = Environment.TickCount64 + 1000; playing.Observation.ControlError = "Link stopped: " + ex.Message; }
        }
    }
    private void ApplyMusicEffects(SoundPreviewPlayer player) => player.SetAudioEffects(config.MusicEqualizerGains, config.MusicReverbAmount);

    private void TakeMusicControl(MusicObservation observation) {
        var ticket = observation.Native;
        if(ticket == null) throw new InvalidOperationException("No sound instance captured. A fresh animation sound trigger is needed.");
        var identity = soundProbe.ReadyIdentity(ticket);
        if(identity == null) throw new InvalidOperationException(soundProbe.TicketCurrent(ticket)
            ? "Waiting for the captured game sound to finish loading…" : "The captured sound ended or changed. Waiting for a fresh trigger.");
        var player = new SoundPreviewPlayer();
        NativeSoundLease? lease = null;
        try {
            player.Load(observation.Path, 0, loop: true);
            lease = NativeSoundLease.Acquire(identity, () => soundProbe.TicketCurrent(ticket), out var elapsed);
            player.Seek(elapsed % observation.Duration);
            ApplyMusicEffects(player);
            player.Volume = MusicGain(observation, lease);
            player.Play();
            musicControls.Add(observation.Owner.Track, new(observation, player, lease));
            observation.WasControlled = true;
        } catch { try { player.Dispose(); } finally { lease?.Release(true); } throw; }
    }
    private void QueueMusicControl(MusicObservation observation, string action, double value = 0) {
        if(musicCommands.Count < 64) musicCommands.Enqueue(new(observation, action, value));
    }
    private float DrawMusicControls(MusicObservation observation) {
        ImGui.PushID(observation.Path);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var sliderWidth = ImGui.GetFontSize() * 8;
        var volumeWidth = sliderWidth + ImGui.CalcTextSize("Volume").X + ImGui.GetStyle().ItemInnerSpacing.X;
        var rowWidth = ImGui.CalcTextSize("Preparing music controls…").X;
        var title = $"Music | {observation.Duration:0.00}s";
        ImGui.TextUnformatted(title);
        ImGui.SameLine();
        var titleWidth = ImGui.CalcTextSize(title).X + spacing + DrawMusicLink(observation);
        ImGui.SameLine();
        DrawMusicEqualizer();
        titleWidth += spacing + EqualizerButtonWidth();
        if(!musicControls.TryGetValue(observation.Owner.Track, out var active) || active.Observation != observation) {
            if(observation.Auto.Released) {
                if(ImGui.Button("Resume control")) QueueMusicControl(observation, "take");
            } else {
                var expired = observation.Auto.Expired(Environment.TickCount64);
                ImGui.TextUnformatted(expired ? "Music controls unavailable" : "Preparing music controls…");
                if(expired) {
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Retry")) QueueMusicControl(observation, "take");
                }
            }
        } else {
            if(!active.Dragging) active.Seek = (float)(active.Link?.MusicTime ?? active.Player.Position);
            ImGui.SetNextItemWidth(-1);
            ImGui.BeginDisabled(active.Link is { MusicMaster: false });
            var changed = ImGui.SliderFloat("##MusicSeconds", ref active.Seek, 0, (float)active.Player.Duration, "%.2f s");
            if(ImGui.IsItemActivated()) {
                active.Dragging = true; active.ResumeAfterDrag = active.Player.Playing;
                QueueMusicControl(observation, "pause");
            }
            if(changed) QueueMusicControl(observation, "seek", active.Seek);
            if(ImGui.IsItemDeactivated() && active.Dragging) {
                active.Dragging = false;
                if(active.ResumeAfterDrag) QueueMusicControl(observation, "play");
            }
            ImGui.EndDisabled();
            var playing = active.Link?.Playing ?? active.Player.Playing;
            var time = $"{(active.Link?.MusicTime ?? active.Player.Position):0.00}s / {active.Player.Duration:0.00}s";
            var buttonSize = ImGui.GetFrameHeight();
            rowWidth = ImGui.CalcTextSize(time).X + buttonSize + spacing;
            var rowY = ImGui.GetCursorPosY();
            if(CompactIconButton("MusicPlay", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, playing ? "Pause music" : "Play music"))
                QueueMusicControl(observation, playing ? "pause" : "play");
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY + (buttonSize - ImGui.GetFontSize()) / 2);
            ImGui.TextUnformatted(time);
        }
        ImGui.SameLine();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - volumeWidth));
        DrawTimelineVolume("Volume", sliderWidth, observation.Bus);
        if(!string.IsNullOrEmpty(observation.ControlError)) ImGui.TextWrapped(observation.ControlError);
        ImGui.PopID();
        return Math.Max(titleWidth, rowWidth + spacing + volumeWidth) + ImGui.GetStyle().WindowPadding.X * 2;
    }

    private void DrawTimelineVolume(string label, float width, int bus) {
        var percent = TimelineMusicLevel(bus) * 100;
        ImGui.SetNextItemWidth(width);
        if(ImGui.SliderFloat(label, ref percent, 0, MusicVolume.MaxGain * 100, "%.0f%%", ImGuiSliderFlags.AlwaysClamp)) {
            config.TimelineMusicVolume = MusicVolume.ClampGain(percent / 100);
            ScheduleAudioSettingsSave();
        }
        if(!ImGui.IsItemHovered()) return;
        ImGui.SetTooltip("Shared volume for all timeline music. Right-click to follow the game's sound channels.");
        if(ImGui.IsMouseClicked(ImGuiMouseButton.Right) && config.TimelineMusicVolume.HasValue) {
            config.TimelineMusicVolume = null;
            Save();
        }
    }
}
