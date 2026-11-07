using System;

namespace AnimSync;

internal sealed record NativeSoundIdentity(nint Address, nint Resource, int SoundId, uint Index);

internal static class MusicControlRules {
    // Sound IDs are opaque 32-bit tokens, not signed nonnegative indices.
    public static NativeSoundIdentity? ReadyIdentity(bool active, bool loading, NativeSoundIdentity identity) =>
        active && !loading && identity.Resource != 0 ? identity : null;
    public static bool PoolContains(nint address, nint pool, int size) {
        if((long)pool <= 0 || (long)address < (long)pool || size <= 0) return false;
        var delta = (long)address - (long)pool;
        return delta < 256L * size && delta % size == 0;
    }
    public static bool RestoreVolume(float current) => float.IsFinite(current) && Math.Abs(current) < .00001f;
}
