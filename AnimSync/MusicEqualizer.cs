using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace AnimSync;

internal sealed class MusicEqualizer(ISampleProvider source) : ISampleProvider {
    public const float MaxBandGain = 12;
    public static ReadOnlySpan<int> Frequencies => [60, 150, 400, 1000, 2400, 10000];
    private const float BandQ = .9f;
    private readonly float[] gains = new float[Frequencies.Length];
    private readonly BiQuadFilter[]?[] filters = new BiQuadFilter[Frequencies.Length][];

    public WaveFormat WaveFormat => source.WaveFormat;

    public static float[] Normalize(float[]? gains) {
        if(gains?.Length != Frequencies.Length) return new float[Frequencies.Length];
        for(var band = 0; band < gains.Length; band++)
            gains[band] = float.IsFinite(gains[band]) ? Math.Clamp(gains[band], -MaxBandGain, MaxBandGain) : 0;
        return gains;
    }

    public void SetGains(ReadOnlySpan<float> newGains) {
        var rate = WaveFormat.SampleRate;
        for(var band = 0; band < gains.Length; band++) {
            var gain = newGains[band];
            if(gain == gains[band]) continue;
            gains[band] = gain;
            var frequency = Frequencies[band];
            if(gain == 0 || frequency * 2 >= rate) {
                filters[band] = null;
                continue;
            }
            var channels = filters[band] ??= new BiQuadFilter[WaveFormat.Channels];
            for(var channel = 0; channel < channels.Length; channel++) {
                if(channels[channel] is { } filter) filter.SetPeakingEq(rate, frequency, BandQ, gain);
                else channels[channel] = BiQuadFilter.PeakingEQ(rate, frequency, BandQ, gain);
            }
        }
    }

    public void Reset() {
        // Drop filter history after a seek so the previous position cannot bleed through.
        for(var band = 0; band < filters.Length; band++) {
            if(filters[band] is not { } channels) continue;
            for(var channel = 0; channel < channels.Length; channel++)
                channels[channel] = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, Frequencies[band], BandQ, gains[band]);
        }
    }

    public int Read(float[] buffer, int offset, int count) {
        var read = source.Read(buffer, offset, count);
        foreach(var channels in filters) {
            if(channels == null) continue;
            for(var channel = 0; channel < channels.Length; channel++) {
                var filter = channels[channel];
                for(var sample = offset + channel; sample < offset + read; sample += channels.Length)
                    buffer[sample] = filter.Transform(buffer[sample]);
            }
        }
        return read;
    }
}
