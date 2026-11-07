using System.Collections.Generic;

namespace AnimSync;

internal static class TimelineGroups {
    // Call in row order: a character belongs to the earliest matching timeline only.
    public static HashSet<ulong> Select(AnimationSample anchor, IReadOnlyList<AnimationSample> samples,
        float distance, ISet<ulong> claimed) {
        var group = new HashSet<ulong>();
        if(claimed.Contains(anchor.Id)) return group;
        group.Add(anchor.Id);
        foreach(var sample in samples)
            if(!claimed.Contains(sample.Id) && TimelineRules.Matches(anchor, sample, distance)) group.Add(sample.Id);
        return group;
    }
}
