using System;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace AnimSync;

// A lease mutes one observed instance only; no category/bus/global-volume changes.
internal sealed unsafe class NativeSoundLease {
    private readonly NativeSoundIdentity identity;
    private readonly float volume;
    private readonly Func<bool> current;
    private bool muted;
    public float OriginalVolume => volume;
    public bool IsCurrent {
        get { if(!current()) return false; var sound = Resolve(identity); return sound != null && sound->IsPlaying(); }
    }
    private NativeSoundLease(NativeSoundIdentity identity, float volume, Func<bool> current) { this.identity = identity; this.volume = volume; this.current = current; }
    public static NativeSoundIdentity? Snapshot(SoundManager* manager, SoundData* sound) {
        if(!InPool(manager, (nint)sound)) return null;
        return MusicControlRules.ReadyIdentity(sound->IsActive, sound->IsLoadingSoundResource,
            new((nint)sound, (nint)sound->SoundResourceHandle, sound->SoundController.SoundId, sound->SoundNumber));
    }
    private static bool InPool(SoundManager* manager, nint address) {
        if(manager == null || manager->SoundDataPool == null || address == 0) return false;
        return MusicControlRules.PoolContains(address, (nint)manager->SoundDataPool, sizeof(SoundData));
    }
    public static string Describe(SoundManager* manager, nint address) {
        if(!InPool(manager, address)) return "slot is outside the current sound pool";
        var sound = (SoundData*)address;
        return $"active={sound->IsActive}, loading={sound->IsLoadingSoundResource}, resource={sound->SoundResourceHandle != null}, sound ID={sound->SoundController.SoundId}, sound index={sound->SoundNumber}";
    }
    private static SoundData* Resolve(NativeSoundIdentity identity) {
        var manager = SoundManager.Instance();
        if(!InPool(manager, identity.Address)) return null;
        var sound = (SoundData*)identity.Address;
        return Snapshot(manager, sound) == identity ? sound : null;
    }
    public static bool Available(NativeSoundIdentity? identity) => identity != null && Resolve(identity) != null;
    public static NativeSoundLease Acquire(NativeSoundIdentity identity, Func<bool> current, out float elapsed) {
        if(!current()) throw new InvalidOperationException("The sound allocation changed before takeover.");
        var sound = Resolve(identity);
        if(sound == null) throw new InvalidOperationException("The captured game sound ended or changed. Trigger the dance again.");
        if(!sound->IsPlaying()) throw new InvalidOperationException("The captured sound has not started playing or has already stopped.");
        var volume = sound->Volume;
        elapsed = sound->GetElapsedTime();
        if(!float.IsFinite(volume) || volume < 0 || !float.IsFinite(elapsed) || elapsed < 0)
            throw new InvalidOperationException("The captured sound state could not be verified.");
        var lease = new NativeSoundLease(identity, volume, current);
        sound->SetVolume(0, 0);
        if(Math.Abs(sound->Volume) > .00001f) {
            sound->SetVolume(volume, 0);
            throw new InvalidOperationException("Could not verify per-instance muting; takeover was cancelled.");
        }
        lease.muted = true;
        return lease;
    }
    public void Release(bool restore) {
        if(!muted) return;
        muted = false;
        if(!restore || !current()) return;
        var sound = Resolve(identity);
        // Preserve later volume changes made by the game or other tools.
        if(sound != null && MusicControlRules.RestoreVolume(sound->Volume)) sound->SetVolume(volume, 0);
    }
}
