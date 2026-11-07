using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AnimSync;

internal sealed class SoundPreviewPlayer : IDisposable {
    private readonly object gate = new();
    private VorbisWaveReader? reader;
    private WaveOutEvent? output;
    private VolumeSampleProvider? volumeProvider;
    private MusicEqualizer? equalizer;
    private MusicReverb? reverb;
    public bool Playing => output?.PlaybackState == PlaybackState.Playing;
    public double Duration { get { lock(gate) return reader?.TotalTime.TotalSeconds ?? 0; } }
    public double Position { get { lock(gate) return reader?.CurrentTime.TotalSeconds ?? 0; } }
    public float Volume { set { lock(gate) { if(volumeProvider != null) volumeProvider.Volume = MusicVolume.ClampGain(value); } } }
    public void SetAudioEffects(ReadOnlySpan<float> gains, float reverbAmount) {
        lock(gate) {
            equalizer?.SetGains(gains);
            reverb?.SetAmount(reverbAmount);
        }
    }
    public void Load(string path, float volume, bool loop = false) {
        Dispose();
        var file = new FileInfo(path);
        if(file.Length > 64 * 1024 * 1024) throw new InvalidDataException("SCD exceeds the audio size limit.");
        var clip = ScdVorbisClip.Read(File.ReadAllBytes(path));
        try {
            reader = new VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
            if(reader.WaveFormat.Channels != clip.Channels || reader.WaveFormat.SampleRate != clip.SampleRate)
                throw new InvalidDataException("Vorbis stream and SCD audio format disagree.");
            output = new WaveOutEvent { DesiredLatency = 100 };
            equalizer = new MusicEqualizer(reader.ToSampleProvider());
            reverb = new MusicReverb(equalizer);
            volumeProvider = new VolumeSampleProvider(reverb) { Volume = MusicVolume.ClampGain(volume) };
            output.Init(new LoopingWaveProvider(volumeProvider.ToWaveProvider(), () => reader.CurrentTime = TimeSpan.Zero, gate, loop));
        } catch { Dispose(); throw; }
    }
    public void Play() {
        if(output == null || reader == null) return;
        if(Position >= Duration - .01) Seek(0);
        output.Play();
    }
    public void Pause() => output?.Pause();
    public void Seek(double seconds) {
        if(output == null || reader == null || !double.IsFinite(seconds)) return;
        var resume = Playing;
        output.Stop(); // Flush queued preview buffers before moving the decoder.
        lock(gate) {
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, reader.TotalTime.TotalSeconds));
            equalizer?.Reset();
            reverb?.Reset();
        }
        if(resume) output.Play();
    }
    public void Dispose() {
        // Stop/join the output before disposing the stream read by its audio thread.
        var previousOutput = output;
        output = null;
        try { previousOutput?.Stop(); }
        finally {
            try { previousOutput?.Dispose(); }
            finally {
                lock(gate) { reader?.Dispose(); reader = null; volumeProvider = null; equalizer = null; reverb = null; }
            }
        }
    }
}
