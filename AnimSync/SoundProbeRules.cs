using System;

namespace AnimSync;

internal static class SoundProbeRules {
    // Capture before the next PAP scan, so a newly started animation isn't missed.
    // Ownership is established later by exact PAP path/cue matching, never by this filter.
    public static bool Relevant(string path) => MusicInference.Normalize(path).EndsWith(".scd", StringComparison.Ordinal);
}
