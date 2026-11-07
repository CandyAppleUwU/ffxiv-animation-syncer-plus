using System;

namespace AnimSync;

internal sealed class LinkedTimelineClock {
    public double AnimationDuration { get; }
    public double MusicDuration { get; }
    public bool MusicMaster => MusicDuration >= AnimationDuration;
    public double Duration => Math.Max(AnimationDuration, MusicDuration);
    public double Position { get; private set; }
    public bool Playing { get; private set; }
    public bool NeedsSeek { get; private set; } = true;
    private long previous;
    public LinkedTimelineClock(double animationDuration, double musicDuration, double position, bool playing, long now) {
        if(!double.IsFinite(animationDuration) || !double.IsFinite(musicDuration) || animationDuration <= 0 || musicDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(animationDuration));
        AnimationDuration = animationDuration; MusicDuration = musicDuration;
        Position = double.IsFinite(position) ? Math.Clamp(position, 0, Duration) : 0;
        Playing = playing; previous = now;
    }
    public static double Loop(double time, double duration) => time % duration;
    public double AnimationTime => MusicMaster ? Loop(Position, AnimationDuration) : Position;
    public double MusicTime => MusicMaster ? Position : Loop(Position, MusicDuration);
    public void Tick(long now) {
        if(Playing && now > previous) Position = Loop(Position + (now - previous) / 1000d, Duration);
        previous = now;
    }
    public void Seek(double time, long now) {
        Tick(now);
        if(!double.IsFinite(time)) return;
        Position = Math.Clamp(time, 0, Duration); NeedsSeek = true;
    }
    public void SetPlaying(bool play, long now) { Tick(now); Playing = play; NeedsSeek = true; }
    public void Sought() => NeedsSeek = false;
    public static double LoopDistance(double a, double b, double duration) {
        var delta = Math.Abs(a - b) % duration;
        return Math.Min(delta, duration - delta);
    }
}
