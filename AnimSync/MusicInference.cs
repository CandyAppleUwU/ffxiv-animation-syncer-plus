using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimSync;

internal sealed record MusicCandidate(MusicOwner Owner, string VirtualPath, string ResolvedPath, uint SoundIndex, long FirstSeen, long LastSeen);
internal static class MusicInference {
    public static string Normalize(string path) {
        var pipe = path.LastIndexOf('|');
        return (pipe >= 0 ? path[(pipe + 1)..] : path).Replace('\\', '/').ToLowerInvariant();
    }
    public static List<MusicCandidate> Matches(string path, uint index, long triggered, IEnumerable<MusicCandidate> candidates) {
        path = Normalize(path);
        return candidates.Where(c => c.SoundIndex == index && c.FirstSeen <= triggered + 500 && c.LastSeen >= triggered - 2000 &&
                (Normalize(c.VirtualPath) == path || Normalize(c.ResolvedPath) == path))
            .GroupBy(c => (c.Owner.Id, c.Owner.Address, c.Owner.Index, c.Owner.Territory, c.Owner.Gpose))
            .Select(g => g.OrderByDescending(c => c.LastSeen).First()).ToList();
    }
}
