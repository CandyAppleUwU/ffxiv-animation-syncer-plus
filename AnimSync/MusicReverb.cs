using System;
using NAudio.Wave;

namespace AnimSync;

internal sealed class MusicReverb(ISampleProvider source) : ISampleProvider {
    private Channel[]? channels;
    private float wet;
    public WaveFormat WaveFormat => source.WaveFormat;

    public void SetAmount(float amount) {
        // Keep some dry signal throughout the slider's range.
        wet = MusicVolume.Clamp(amount) * .4f;
        if(wet == 0) {
            channels = null;
        } else if(channels == null) {
            channels = new Channel[WaveFormat.Channels];
            for(var channel = 0; channel < channels.Length; channel++)
                channels[channel] = new Channel(WaveFormat.SampleRate, channel);
        }
    }

    public void Reset() {
        if(channels == null) return;
        foreach(var channel in channels) channel.Reset();
    }

    public int Read(float[] buffer, int offset, int count) {
        var read = source.Read(buffer, offset, count);
        if(channels == null) return read;
        for(var channel = 0; channel < channels.Length; channel++) {
            var room = channels[channel];
            for(var i = offset + channel; i < offset + read; i += channels.Length) {
                var dry = buffer[i];
                buffer[i] = dry * (1 - wet) + room.Reflect(dry) * wet;
            }
        }
        return read;
    }

    // A Schroeder room uses parallel feedback delays followed by serial diffusers.
    private sealed class Channel(int rate, int channel) {
        private readonly Delay[] reflections = [new(rate, 30, channel), new(rate, 37, channel), new(rate, 41, channel), new(rate, 44, channel)];
        private readonly Delay firstDiffuser = new(rate, 5, channel);
        private readonly Delay secondDiffuser = new(rate, 1.7f, channel);

        public float Reflect(float sample) {
            var sum = 0f;
            foreach(var delay in reflections) sum += delay.Echo(sample);
            return secondDiffuser.Diffuse(firstDiffuser.Diffuse(sum / reflections.Length));
        }

        public void Reset() {
            foreach(var delay in reflections) delay.Reset();
            firstDiffuser.Reset();
            secondDiffuser.Reset();
        }
    }

    private sealed class Delay(int rate, float milliseconds, int channel) {
        private readonly float[] buffer = new float[Math.Max(1, (int)MathF.Round(rate * (milliseconds + channel * .5f) / 1000))];
        private int position;
        private float filtered;

        public float Echo(float sample) {
            var delayed = buffer[position];
            filtered = CleanTail(delayed * .7f + filtered * .3f);
            Write(sample + filtered * .72f);
            return delayed;
        }

        public float Diffuse(float sample) {
            const float feedback = .5f;
            var result = buffer[position] - sample * feedback;
            Write(sample + result * feedback);
            return result;
        }

        private void Write(float sample) {
            buffer[position] = CleanTail(sample);
            if(++position == buffer.Length) position = 0;
        }

        private static float CleanTail(float sample) => MathF.Abs(sample) < 1e-20f ? 0 : sample;

        public void Reset() {
            Array.Clear(buffer);
            position = 0;
            filtered = 0;
        }
    }
}
