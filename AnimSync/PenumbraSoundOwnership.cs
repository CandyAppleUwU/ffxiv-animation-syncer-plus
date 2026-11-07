using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace AnimSync;

public sealed partial class AnimSync {
    private ICallGateSubscriber<nint, string, string, object?>? soundOwnerSubscriber;
    private volatile Dictionary<nint, MusicOwner> soundOwnerSnapshot = new();
    private void PrepareSoundOwners() {
        // Publish immutable managed snapshots. IPC callbacks never dereference game pointers.
        soundOwnerSnapshot = Objects.Where(IsManualPlayer).ToDictionary(a => a.Address, a =>
            new MusicOwner(AllTimelines().FirstOrDefault(t => TrackSource(t)?.Address == a.Address)?.Key ?? -1,
                a.GameObjectId, a.Address, a.ObjectIndex, Client.TerritoryType, Client.IsGPosing));
        if(soundOwnerSubscriber != null) return;
        var subscriber = Interface.GetIpcSubscriber<nint, string, string, object?>("Penumbra.GameObjectResourcePathResolved");
        subscriber.Subscribe(OnSoundResourceResolved);
        soundOwnerSubscriber = subscriber;
    }
    private void OnSoundResourceResolved(nint actor, string gamePath, string filePath) {
        try {
            if(actor == 0 || !SoundProbeRules.Relevant(gamePath)) return;
            soundOwnerSnapshot.TryGetValue(actor, out var owner);
            SoundCallOwnership.Observe(owner, gamePath, filePath);
        } catch(Exception) { } // Must not interrupt Penumbra or the original game sound call.
    }
    private void StopSoundOwners() {
        soundOwnerSubscriber?.Unsubscribe(OnSoundResourceResolved);
        soundOwnerSubscriber = null;
        soundOwnerSnapshot = new();
    }
}
