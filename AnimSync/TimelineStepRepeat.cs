namespace AnimSync;

internal sealed class TimelineStepRepeat {
    public bool Active { get; private set; }
    public int Direction { get; private set; } = 1;
    private long epoch;
    private int frame;
    private double next;
    public int Start(long currentEpoch, int currentFrame, int count, double now, int direction = 1) {
        Active = true;
        Direction = direction < 0 ? -1 : 1;
        epoch = currentEpoch;
        frame = System.Math.Clamp(currentFrame + Direction, 0, count - 1);
        next = now + .5;
        return frame;
    }
    public int? Poll(long currentEpoch, int count, double now) {
        if(!Active || epoch != currentEpoch) { Cancel(); return null; }
        if(now < next || (Direction > 0 ? frame >= count - 1 : frame <= 0)) return null;
        next = now + .2; // No catch-up burst after a slow render/update.
        return frame += Direction;
    }
    public void Cancel() => Active = false;
}
