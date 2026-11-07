using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using NAudio.Vorbis;

namespace AnimSync;

public sealed partial class AnimSync {
    [PluginService] public static IGameInteropProvider Interop { get; private set; } = null!;
    private readonly PapSoundProbe soundProbe = new();
    private readonly List<PapSoundProbe.Trigger> recentSoundTriggers = new();
    private bool timelineMusicFaulted;

    private sealed record MusicObservation(MusicOwner Owner, string Path, double Duration, long Started,
        string EvidencePath, uint Index, NativeSoundTicket? Native, ScdSoundInfo? Sound, bool PenumbraOwner) {
        public int Bus => Sound?.Bus ?? -1;
        public MusicAutoTakeover Auto { get; } = new(Environment.TickCount64);
        public string ControlError = string.Empty;
        public bool WasControlled;
    }
    private readonly List<MusicObservation> musicObservations = new();
    private readonly Dictionary<string, (long Length, DateTime Written, double Duration, ScdSoundInfo? Sound)> musicDurations = new(StringComparer.OrdinalIgnoreCase);

    private bool PrepareTimelineMusicDetection(bool rescan = false) {
        if(timelineMusicFaulted) return false;
        try {
            PrepareSoundOwners();
            if(!soundProbe.Enabled) soundProbe.Enable(Interop);
            if(rescan) nextMusicScan = 0;
            ScanMusicCandidates(Environment.TickCount64);
            return true;
        } catch(Exception ex) {
            TimelineMusicFailed(ex);
            return false;
        }
    }

    private void ProcessTimelineMusic() {
        if(!TimelineAvailable) {
            if(soundProbe.Enabled) StopTimelineMusicDetection();
            timelineMusicFaulted = false;
            return;
        }
        if(!PrepareTimelineMusicDetection()) return;

        try {
            musicObservations.RemoveAll(m => !ValidMusicOwner(m.Owner) || m.WasControlled &&
                (m.Native == null || soundProbe.ReadyIdentity(m.Native) == null ||
                 musicControls.TryGetValue(m.Owner.Track, out var active) && !active.Lease.IsCurrent));
            while(soundProbe.TryRead(out var trigger)) {
                if(trigger == null) continue;
                if(trigger.Native != null && recentSoundTriggers.Any(t => t.Native == trigger.Native &&
                    MusicInference.Normalize(t.Path) == MusicInference.Normalize(trigger.Path))) continue;
                recentSoundTriggers.Insert(0, trigger);
                if(recentSoundTriggers.Count > 24) recentSoundTriggers.RemoveAt(recentSoundTriggers.Count - 1);
                if(pendingMusicInference.Count < 128) pendingMusicInference.Add(trigger);
            }
            InferPendingMusic(Environment.TickCount64);
            ProcessMusicControls();
        } catch(Exception ex) { TimelineMusicFailed(ex); }
    }

    private void TimelineMusicFailed(Exception ex) {
        timelineMusicFaulted = true;
        StopTimelineMusicDetection();
        Logger.Warning(ex, "Timeline music detection stopped. Reopen Animation Timeline to retry.");
    }

    private void StopTimelineMusicDetection(bool restoreNative = true) {
        // Restore captured game audio before disabling the hook that validates native sound tickets.
        ReleaseMusicControl(restoreNative);
        try { StopSoundOwners(); }
        catch(Exception ex) { Logger.Warning(ex, "Could not remove sound ownership subscriber."); }
        try { soundProbe.Disable(); }
        catch(Exception ex) { Logger.Warning(ex, "Could not disable timeline sound capture."); }
        ResetMusicInference();
        musicObservations.Clear();
        recentSoundTriggers.Clear();
    }

    private void DisposeTimelineMusic(bool restoreNative = true) {
        StopTimelineMusicDetection(restoreNative);
        try { soundProbe.Dispose(); }
        catch(Exception ex) { Logger.Warning(ex, "Could not remove timeline sound capture."); }
    }

    private bool ValidMusicOwner(MusicOwner owner) {
        if(!TimelineAvailable || owner.Territory != Client.TerritoryType || owner.Gpose != Client.IsGPosing) return false;
        var track = AllTimelines().FirstOrDefault(t => t.Key == owner.Track);
        var actor = track == null ? null : TrackSource(track);
        if(actor == null || !MusicRules.OwnerMatches(owner, new(track!.Key, actor.GameObjectId, actor.Address,
            actor.ObjectIndex, Client.TerritoryType, Client.IsGPosing))) return false;
        var key = PlayerSoundKey(actor);
        return key != null && !config.DjPlayers.ContainsKey(key);
    }

    private void ClassifyMusic(PapSoundProbe.Trigger trigger, string? inferredPath = null) {
        if(trigger.Owner is not { } owner || !ValidMusicOwner(owner) || trigger.Index != 0) return;
        try {
            var path = ResolveMusicPath(inferredPath ?? trigger.Path, owner.Index);
            if(!Path.IsPathRooted(path) || !File.Exists(path)) return;
            var info = new FileInfo(path);
            if(!musicDurations.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.Written != info.LastWriteTimeUtc) {
                if(info.Length > 64 * 1024 * 1024) return;
                var bytes = File.ReadAllBytes(path);
                var clip = ScdVorbisClip.Read(bytes);
                ScdSoundInfo? sound = null;
                try { sound = ScdSoundInfo.Read(bytes, trigger.Index); }
                catch(InvalidDataException) { } // Keep the row without guessing its channel.
                using var reader = new VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
                cached = (info.Length, info.LastWriteTimeUtc, reader.TotalTime.TotalSeconds, sound);
                if(musicDurations.Count >= 128) musicDurations.Clear();
                musicDurations[path] = cached;
            }
            if(!MusicRules.Eligible(cached.Duration, true, false)) return;
            if(musicObservations.Any(m => m.Owner.Track == owner.Track && m.Started > trigger.Time)) return;
            ReleaseMusicTrack(owner.Track);
            musicObservations.RemoveAll(m => m.Owner.Track == owner.Track);
            musicObservations.Insert(0, new(owner, path, cached.Duration, trigger.Time, trigger.Path, trigger.Index, trigger.Native,
                cached.Sound, trigger.OwnerPath != null));
            if(musicObservations.Count > 16) musicObservations.RemoveAt(musicObservations.Count - 1);
        } catch(Exception) { /* An unreadable or unsupported sound cannot be verified as music. */ }
    }
}
