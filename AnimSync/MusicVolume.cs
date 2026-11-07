using System;

namespace AnimSync;

internal static class MusicVolume {
    public const float MaxGain = 5f;
    public static float Resolve(float? savedGain, float gameVolume) => savedGain is { } gain ? ClampGain(gain) : Clamp(gameVolume);
    public static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    public static float ClampGain(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, MaxGain) : 0;
    public static float OutputGain(float level, float master, float instance, float entry) => ClampGain(level * master * instance * entry);
    public static bool PlaybackAllowed(bool inactive, bool globalInactiveMute, bool channelInactiveMute, bool channelMuted) =>
        !channelMuted && (!inactive || !(globalInactiveMute || channelInactiveMute));
    public static string Channel(int bus) => bus switch {
        1 or 8 => "Music", 16 => "Music (Orchestrion)",
        2 or 6 or 7 or 9 or 10 => "Sound Effects",
        3 => "Voice", 4 => "System", 5 or 11 or 12 or 19 or 20 => "Ambient",
        13 => "Movie", 17 => "Instruments", 18 => "Vibration",
        14 or 15 => $"Bus {bus}", _ => "Unknown channel",
    };
}
