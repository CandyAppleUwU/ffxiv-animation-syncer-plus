using System;

namespace AnimSync;

// Only correlate a Penumbra event occurring inside the SAME sound call on the SAME thread.
// A resource-load notification by itself is not proof of a playing sound instance.
internal sealed class SoundCallOwnership : IDisposable {
    [ThreadStatic] private static SoundCallOwnership? current;
    private readonly SoundCallOwnership? previous;
    private readonly string path;
    public MusicOwner? Owner { get; private set; }
    public string? ResolvedPath { get; private set; }
    public bool Conflict { get; private set; }
    private SoundCallOwnership(string path) { this.path = MusicInference.Normalize(path); previous = current; current = this; }
    public static SoundCallOwnership Begin(string path) => new(path);
    public static void Observe(MusicOwner? owner, string gamePath, string filePath) {
        var call = current;
        if(call == null || call.path.Length == 0 ||
            (call.path != MusicInference.Normalize(gamePath) && call.path != MusicInference.Normalize(filePath))) return;
        if(owner == null || call.Owner != null && (call.Owner != owner ||
            MusicInference.Normalize(call.ResolvedPath!) != MusicInference.Normalize(filePath))) { call.Conflict = true; return; }
        call.Owner = owner; call.ResolvedPath = filePath;
    }
    public void Dispose() => current = previous;
}
