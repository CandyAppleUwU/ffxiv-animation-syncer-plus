using System.Collections.Concurrent;
using System.Threading;

namespace AnimSync;

internal sealed record NativeSoundTicket(nint Address, nint Pool, long Generation, uint Index);
internal sealed class SoundSlotTracker {
    private readonly ConcurrentDictionary<nint, (nint Pool, long Generation)> slots = new();
    private long generation;
    public void Allocated(nint address, nint pool) {
        if(address != 0 && pool != 0) slots[address] = (pool, Interlocked.Increment(ref generation));
    }
    public NativeSoundTicket? Capture(nint address, nint pool, uint index) =>
        slots.TryGetValue(address, out var value) && value.Pool == pool ? new(address, pool, value.Generation, index) : null;
    public bool Current(NativeSoundTicket ticket) => slots.TryGetValue(ticket.Address, out var value) &&
        value.Pool == ticket.Pool && value.Generation == ticket.Generation;
    public void Clear() => slots.Clear();
}
