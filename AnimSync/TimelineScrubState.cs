namespace AnimSync;

// Keep the state from drag start, rather than the now-paused playback samples.
internal sealed class TimelineScrubState {
    public bool Active { get; private set; }
    public long Epoch { get; private set; }
    public int Frame { get; private set; }
    private bool resume;
    public void Begin(long epoch, int frame, bool playing) {
        Active = true;
        Epoch = epoch;
        Frame = frame;
        resume = playing;
    }
    public void Move(int frame) { if(Active) Frame = frame; }
    public (int Frame, bool Play)? Finish(long epoch) {
        var result = Active && Epoch == epoch ? ((int Frame, bool Play)?)(Frame, resume) : null;
        Cancel();
        return result;
    }
    public void Cancel() { Active = false; resume = false; }
}
