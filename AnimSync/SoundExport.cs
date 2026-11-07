using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;

namespace AnimSync;

internal static class SoundExport {
    public static string Wav(string source, string directory, string characterName = "Sound") {
        var info = new FileInfo(source);
        if(info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Audio file exceeds the export size limit.");
        var clip = ScdVorbisClip.Read(File.ReadAllBytes(source));
        using var reader = new VorbisWaveReader(new MemoryStream(clip.Ogg, false), true);
        var format = new WaveFormat(clip.SampleRate, 16, clip.Channels);
        if(reader.TotalTime.TotalSeconds <= 0 || reader.TotalTime.TotalSeconds * format.AverageBytesPerSecond > 1024L * 1024 * 1024)
            throw new InvalidDataException("Decoded audio exceeds the WAV export limit.");
        Directory.CreateDirectory(directory);
        var name = characterName;
        foreach(var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        if(name.Length > 80) name = name[..80];
        if(string.IsNullOrWhiteSpace(name)) name = "Sound";
        var path = Path.Combine(directory, $"{name}_{Guid.NewGuid():N}.wav");
        var created = false;
        try {
            using(var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                created = true;
                using var writer = new WaveFileWriter(stream, format);
                var samples = reader.ToSampleProvider();
                var buffer = new float[8192];
                int count;
                while((count = samples.Read(buffer, 0, buffer.Length)) > 0) writer.WriteSamples(buffer, 0, count);
            }
            return path;
        } catch {
            if(created) { try { File.Delete(path); } catch { } }
            throw;
        }
    }
}
