using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using InteropGenerator.Runtime;

namespace AnimSync;

internal sealed unsafe class PapSoundProbe : IDisposable {
    internal sealed record Trigger(string Path, uint Index, long Time, MusicOwner? Owner, NativeSoundTicket? Native = null,
        string? OwnerPath = null, bool OwnerConflict = false);
    // No retained game pointers or writes. The callback copies only valid call arguments.
    private delegate SoundData* PlaySoundDelegate(SoundManager* manager, CStringPointer path, float volume,
        uint fade, float x, float y, float z, float speed, int priority, uint index,
        [MarshalAs(UnmanagedType.I1)] bool autoRelease, SoundVolumeCategory category,
        [MarshalAs(UnmanagedType.I1)] bool a13, int midi, [MarshalAs(UnmanagedType.I1)] bool a15,
        [MarshalAs(UnmanagedType.I1)] bool defaultFade, [MarshalAs(UnmanagedType.I1)] bool positional,
        [MarshalAs(UnmanagedType.I1)] bool a18);
    private Hook<PlaySoundDelegate>? hook;
    private delegate SoundData* ClipDelegate(SoundManager* manager, CStringPointer path, float volume,
        uint fade, float x, float y, float z, float speed, int priority, uint index,
        [MarshalAs(UnmanagedType.I1)] bool autoRelease, [MarshalAs(UnmanagedType.I1)] bool a12);
    private Hook<ClipDelegate>? clipHook;
    private delegate SoundData* AcquireDelegate(SoundManager* manager);
    private Hook<AcquireDelegate>? acquireHook;
    private readonly SoundSlotTracker slots = new();
    public bool TicketCurrent(NativeSoundTicket ticket) => slots.Current(ticket);
    public string TicketStatus(NativeSoundTicket ticket) => !slots.Current(ticket) ? "allocation was replaced" :
        NativeSoundLease.Describe(SoundManager.Instance(), ticket.Address);
    public NativeSoundIdentity? ReadyIdentity(NativeSoundTicket ticket) {
        var manager = SoundManager.Instance();
        if(!slots.Current(ticket) || manager == null || (nint)manager->SoundDataPool != ticket.Pool) return null;
        var identity = NativeSoundLease.Snapshot(manager, (SoundData*)ticket.Address);
        return identity?.Index == ticket.Index ? identity : null;
    }
    private readonly ConcurrentQueue<Trigger> events = new();
    private readonly ConcurrentQueue<Trigger> unindexedEvents = new();
    private volatile bool disposed;
    private HashSet<string> referencePaths = new();
    public void SetReferencePaths(IEnumerable<string> paths) => Volatile.Write(ref referencePaths,
        paths.Select(MusicInference.Normalize).ToHashSet(StringComparer.Ordinal));
    public bool Enabled => hook?.IsEnabled == true;
    public void Enable(IGameInteropProvider interop) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if(acquireHook == null) acquireHook = interop.HookFromAddress<AcquireDelegate>((nint)SoundManager.MemberFunctionPointers.AcquireSoundData, AcquireDetour);
        acquireHook.Enable(); // All allocation routes invalidate old tickets before slot reuse.
        if(hook == null) hook = interop.HookFromAddress<PlaySoundDelegate>((nint)SoundManager.MemberFunctionPointers.PlaySound, Detour);
        hook.Enable();
        if(clipHook == null) clipHook = interop.HookFromAddress<ClipDelegate>((nint)SoundManager.MemberFunctionPointers.PlayClipSound, ClipDetour);
        clipHook.Enable();
    }
    public void Disable() { clipHook?.Disable(); hook?.Disable(); acquireHook?.Disable(); slots.Clear(); ClearEvents(); }
    private void ClearEvents() { while(events.TryDequeue(out _)) { } while(unindexedEvents.TryDequeue(out _)) { } }
    private SoundData* AcquireDetour(SoundManager* manager) {
        var result = acquireHook!.Original(manager);
        try { if(!disposed && result != null && manager != null) slots.Allocated((nint)result, (nint)manager->SoundDataPool); }
        catch(Exception) { }
        return result;
    }
    public bool TryRead(out Trigger? trigger) => events.TryDequeue(out trigger) || unindexedEvents.TryDequeue(out trigger);
    private SoundData* ClipDetour(SoundManager* manager, CStringPointer path, float volume, uint fade,
        float x, float y, float z, float speed, int priority, uint index, bool autoRelease, bool a12) {
        Trigger? captured = null;
        try {
            if(!disposed && path.HasValue && SoundProbeRules.Relevant(path.ToString()))
                captured = new(path.ToString(), index, Environment.TickCount64, null);
        } catch(Exception) { }
        var ownership = SoundCallOwnership.Begin(captured?.Path ?? string.Empty);
        SoundData* result;
        try { result = clipHook!.Original(manager, path, volume, fade, x, y, z, speed, priority, index, autoRelease, a12); }
        finally { ownership.Dispose(); }
        try { Enqueue(manager, result, captured, ownership); } catch(Exception) { }
        return result;
    }
    private SoundData* Detour(SoundManager* manager, CStringPointer path, float volume, uint fade,
        float x, float y, float z, float speed, int priority, uint index, bool autoRelease,
        SoundVolumeCategory category, bool a13, int midi, bool a15, bool defaultFade, bool positional, bool a18) {
        // Logging failure must never prevent or duplicate the original game call.
        Trigger? captured = null;
        try {
            if(!disposed && path.HasValue) {
                var text = path.ToString();
                // Owner attribution stays unavailable until a non-disruptive mechanism is verified.
                if(SoundProbeRules.Relevant(text))
                    captured = new(text, index, Environment.TickCount64, null);
            }
        } catch(Exception) { }
        var ownership = SoundCallOwnership.Begin(captured?.Path ?? string.Empty);
        SoundData* result;
        try { result = hook!.Original(manager, path, volume, fade, x, y, z, speed, priority, index,
            autoRelease, category, a13, midi, a15, defaultFade, positional, a18); }
        finally { ownership.Dispose(); }
        try { Enqueue(manager, result, captured, ownership); } catch(Exception) { }
        return result;
    }
    private void Enqueue(SoundManager* manager, SoundData* result, Trigger? captured, SoundCallOwnership ownership) {
        if(result != null && manager != null && captured != null) {
            captured = captured with { Owner = ownership.Conflict ? null : ownership.Owner, OwnerPath = ownership.ResolvedPath,
                OwnerConflict = ownership.Conflict };
            try { captured = captured with { Native = slots.Capture((nint)result, (nint)manager->SoundDataPool, captured.Index) }; }
            catch(Exception) { /* Keep detection available when instance capture cannot be verified. */ }
            // Reserve a separate queue for known PAP paths so unrelated sounds cannot crowd them out.
            var queue = captured.Owner != null || Volatile.Read(ref referencePaths).Contains(MusicInference.Normalize(captured.Path)) ? events : unindexedEvents;
            if(queue.Count < 64) queue.Enqueue(captured);
        }
    }
    public void Dispose() { disposed = true; clipHook?.Dispose(); hook?.Dispose(); acquireHook?.Dispose(); slots.Clear(); ClearEvents(); }
}
