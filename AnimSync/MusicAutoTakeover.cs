namespace AnimSync;

internal sealed class MusicAutoTakeover(long now) {
    private long deadline = now + 10000;
    private long next = now;
    public bool Released { get; private set; }
    public bool CanAttempt(long time) => !Released && time <= deadline && time >= next;
    public bool Expired(long time) => time > deadline;
    public void Attempted(long time) => next = time + 250;
    public void Release() => Released = true;
    public void Retry(long time) { Released = false; next = time; deadline = time + 10000; }
}
