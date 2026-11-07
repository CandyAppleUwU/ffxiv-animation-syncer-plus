using System;
using NAudio.Wave;

namespace AnimSync;

// Fill the same output buffer across the loop boundary, without waiting for a framework tick.
internal sealed class LoopingWaveProvider(IWaveProvider source, Action rewind, object gate, bool loop) : IWaveProvider {
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(byte[] buffer, int offset, int count) {
        lock(gate) {
            var total = 0;
            var rewoundWithoutData = false;
            while(total < count) {
                var read = source.Read(buffer, offset + total, count - total);
                if(read > 0) { total += read; rewoundWithoutData = false; continue; }
                if(!loop || rewoundWithoutData) break; // An empty/broken stream cannot spin forever.
                rewind();
                rewoundWithoutData = true;
            }
            return total;
        }
    }
}
