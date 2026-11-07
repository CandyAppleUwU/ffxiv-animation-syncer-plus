using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnimSync;

public sealed partial class AnimSync {
    private readonly List<MusicCandidate> musicCandidates = new();
    private readonly Dictionary<string, (long Length, DateTime Write, List<PapSoundReference> References, List<PapVfxReference> Vfx)> papSoundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Write, List<VfxSoundReference> References)> vfxSoundCache = new(StringComparer.OrdinalIgnoreCase);
    private List<VfxSoundReference> ReadVfxSounds(string path, int index) {
        var file = ResolveMusicPath(path, index);
        var info = Path.IsPathRooted(file) ? new FileInfo(file) : null;
        if(vfxSoundCache.TryGetValue(file, out var cached) && (info == null || cached.Length == info.Length && cached.Write == info.LastWriteTimeUtc)) return cached.References;
        if(info != null && info.Length > 32 * 1024 * 1024) throw new InvalidDataException("VFX exceeds scan limit.");
        var bytes = info == null ? Data.GetFile(file)?.Data : File.ReadAllBytes(file);
        if(bytes == null) throw new InvalidDataException("Referenced VFX is unavailable.");
        var references = AvfxSoundReferences.Read(bytes);
        if(vfxSoundCache.Count >= 128) vfxSoundCache.Clear();
        vfxSoundCache[file] = (bytes.Length, info?.LastWriteTimeUtc ?? DateTime.MinValue, references);
        return references;
    }
    private readonly List<PapSoundProbe.Trigger> pendingMusicInference = new();
    private long nextMusicScan;
    private bool musicScanIncomplete;
    private static string UnwrapMusicPath(string path) { var i = path.LastIndexOf('|'); return i < 0 ? path : path[(i + 1)..]; }
    private string ResolveMusicPath(string path, int index) {
        path = UnwrapMusicPath(path);
        return Path.IsPathRooted(path) ? path : Interface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath").InvokeFunc(path, index);
    }
    private unsafe void ScanMusicCandidates(long now) {
        if(now < nextMusicScan) return;
        nextMusicScan = now + 250;
        musicCandidates.RemoveAll(c => now - c.LastSeen > 10000 || c.Owner.Territory != Client.TerritoryType || c.Owner.Gpose != Client.IsGPosing);
        var unavailable = 0;
        foreach(var actor in Objects) {
            if(!IsManualPlayer(actor)) continue;
            var character = (Character*)actor.Address;
            if(character->GameObject.DrawObject == null || !character->GameObject.DrawObject->IsVisible) continue;
            AnimationSampling.IsExcluded(character, ResolveTimelineKey, out var timelineKey);
            if(!timelineKey.Replace('\\', '/').Contains("emote/", StringComparison.OrdinalIgnoreCase) || ((Actor*)actor.Address)->Control == null) continue;
            try {
                if(AnimationSampling.CurrentPap(actor) is not { } pap) { unavailable++; continue; }
                var file = ResolveMusicPath(pap.Path, actor.ObjectIndex);
                var physical = Path.IsPathRooted(file);
                var info = physical ? new FileInfo(file) : null;
                if(!papSoundCache.TryGetValue(file, out var cached) || info != null &&
                    (cached.Length != info.Length || cached.Write != info.LastWriteTimeUtc)) {
                    if(info != null && info.Length > 32 * 1024 * 1024) { unavailable++; continue; }
                    var bytes = physical ? File.ReadAllBytes(file) : Data.GetFile(file)?.Data;
                    if(bytes == null) { unavailable++; continue; }
                    var vfx = new List<PapVfxReference>();
                    var references = PapSoundReferences.Read(bytes, vfx);
                    cached = (bytes.Length, info?.LastWriteTimeUtc ?? DateTime.MinValue, references, vfx);
                    if(papSoundCache.Count >= 128) papSoundCache.Clear();
                    papSoundCache[file] = cached;
                }
                var track = AllTimelines().FirstOrDefault(t => TrackSource(t)?.Address == actor.Address);
                var owner = new MusicOwner(track?.Key ?? -1, actor.GameObjectId, actor.Address, actor.ObjectIndex, Client.TerritoryType, Client.IsGPosing);
                var allReferences = cached.References.Where(r => r.HavokIndex == pap.Binding).ToList();
                var linkedVfx = cached.Vfx.Where(v => v.HavokIndex == pap.Binding).Distinct().ToArray();
                if(linkedVfx.Length > 64) throw new InvalidDataException("Too many VFX dependencies to verify ownership.");
                foreach(var vfx in linkedVfx)
                    foreach(var sound in ReadVfxSounds(vfx.Path, actor.ObjectIndex))
                        allReferences.Add(new(pap.Binding, sound.Path, sound.SoundIndex));
                if(allReferences.Count > 4096) throw new InvalidDataException("Too many sound dependencies to verify ownership.");
                foreach(var reference in allReferences.Distinct()) {
                    var resolved = ResolveMusicPath(reference.Path, actor.ObjectIndex);
                    var existing = musicCandidates.FindIndex(c => c.Owner.Id == owner.Id && c.Owner.Address == owner.Address &&
                        c.VirtualPath == reference.Path && c.ResolvedPath == resolved && c.SoundIndex == reference.SoundIndex);
                    var first = existing >= 0 ? musicCandidates[existing].FirstSeen : now;
                    if(existing >= 0) musicCandidates.RemoveAt(existing);
                    musicCandidates.Add(new(owner, reference.Path, resolved, reference.SoundIndex, first, now));
                }
            } catch(Exception) { unavailable++; }
        }
        if(musicCandidates.Count > 1024) musicCandidates.RemoveRange(0, musicCandidates.Count - 1024);
        soundProbe.SetReferencePaths(musicCandidates.SelectMany(c => new[] {c.VirtualPath, c.ResolvedPath}));
        musicScanIncomplete = unavailable > 0;
    }
    private void InferPendingMusic(long now) {
        foreach(var trigger in pendingMusicInference.ToArray()) {
            if(trigger.Owner == null && !trigger.OwnerConflict && now - trigger.Time < 750) continue; // PAP inference needs a polling snapshot; direct owners do not.
            pendingMusicInference.Remove(trigger);
            if(trigger.OwnerConflict) continue;
            if(trigger.Owner != null && trigger.OwnerPath != null) {
                ClassifyMusic(trigger, trigger.OwnerPath);
                continue;
            }
            if(musicScanIncomplete) continue;
            var matches = MusicInference.Matches(trigger.Path, trigger.Index, trigger.Time, musicCandidates)
                .Where(c => Objects.Any(o => o.GameObjectId == c.Owner.Id && o.Address == c.Owner.Address && o.ObjectIndex == c.Owner.Index)).ToList();
            if(matches.Count != 1) continue;
            var candidate = matches[0];
            if(candidate.Owner.Track < 0 || !ValidMusicOwner(candidate.Owner)) continue;
            // Use that candidate's player-specific resolved file for duration, never guess from a basename.
            ClassifyMusic(trigger with { Owner = candidate.Owner }, candidate.ResolvedPath);
        }
    }
    private void ResetMusicInference() {
        musicCandidates.Clear(); pendingMusicInference.Clear(); nextMusicScan = 0;
        musicScanIncomplete = false;
        soundProbe.SetReferencePaths(Array.Empty<string>());
    }
}
