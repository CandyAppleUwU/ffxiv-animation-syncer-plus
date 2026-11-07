using System;

namespace AnimSync;

internal sealed record MusicOwner(int Track, ulong Id, nint Address, int Index, uint Territory, bool Gpose);

internal static class MusicRules {
    public static bool OwnerMatches(MusicOwner captured, MusicOwner? current) => current != null && captured == current;
    public static bool Eligible(double duration, bool ownerMatches, bool dj) =>
        ownerMatches && !dj && double.IsFinite(duration) && duration > 10;
}
