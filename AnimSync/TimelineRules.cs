using System;
using System.Numerics;

namespace AnimSync;

internal static class TimelineRules {
    public static int FrameAtTime(float time, int count, float duration) =>
        !float.IsFinite(time) || !float.IsFinite(duration) || count < 2 || duration <= 0 ? 0 :
        (int)Math.Clamp(Math.Floor((double)time / duration * (count - 1) + .0001), 0, count - 1);
    public static float TimeAtFrame(int frame, int count, float duration) =>
        count < 2 || !float.IsFinite(duration) || duration <= 0 ? 0 :
        (float)((double)Math.Clamp(frame, 0, count - 1) / (count - 1) * duration);
    public static bool Matches(AnimationSample self, AnimationSample other, float distance) =>
        float.IsFinite(distance) && distance > 0 && self.FrameCount == other.FrameCount &&
        Math.Abs(self.Duration - other.Duration) < 0.001f &&
        Vector3.DistanceSquared(self.Position, other.Position) <= distance * distance;
    public static float PlaySpeed(float original) => original != 0 && float.IsFinite(original) ? MathF.Abs(original) : 1;
    public static float HoldTime(float time, float duration) => Math.Clamp(time, 0, MathF.BitDecrement(duration));
    public static float DrivenLayerTime(float time, float duration) => HoldTime(time > duration ? time % duration : time, duration);
    public static double AdvanceLoop(double time, double delta, float duration) {
        var phase = (time + delta) % duration;
        return phase < 0 ? phase + duration : phase;
    }
    public static bool CanRestore(float current, float applied) => current == applied;
}
