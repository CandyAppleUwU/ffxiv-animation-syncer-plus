using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimSync;

internal sealed record VfxSoundReference(string Path, uint SoundIndex);
internal static class AvfxSoundReferences {
    // AVFX is a padded chunk tree. Read sound name/index from the SAME emitter only.
    public static List<VfxSoundReference> Read(byte[] bytes) {
        int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
        if(bytes.Length < 8 || bytes.Length > 32 * 1024 * 1024 || !bytes.AsSpan(0, 4).SequenceEqual("XFVA"u8))
            throw new InvalidDataException("Invalid AVFX header.");
        var size = I32(4);
        if(size < 0 || (long)size + 8 != bytes.Length) throw new InvalidDataException("Invalid AVFX size.");
        List<(int Start, int Size, string Tag)> Chunks(int start, int end) {
            var chunks = new List<(int, int, string)>();
            for(var p = start; p < end;) {
                if(end - p < 8) throw new InvalidDataException("Truncated AVFX chunk.");
                var n = I32(p + 4);
                var next = (long)p + 8 + n + ((4 - n % 4) % 4);
                if(n < 0 || next > end) throw new InvalidDataException("Invalid AVFX chunk size.");
                var tag = Encoding.ASCII.GetString(bytes, p, 4);
                chunks.Add((p + 8, n, tag));
                if(chunks.Count > 20000) throw new InvalidDataException("AVFX chunk limit exceeded.");
                p = (int)next;
            }
            return chunks;
        }
        var result = new List<VfxSoundReference>();
        foreach(var chunk in Chunks(8, bytes.Length).Where(c => c.Tag == "timE")) {
            string? sound = null; int? index = null;
            foreach(var field in Chunks(chunk.Start, chunk.Start + chunk.Size)) {
                if(field.Tag == "mNdS") {
                    if(sound != null || field.Size > 1024) throw new InvalidDataException("Invalid AVFX sound name.");
                    if(field.Size == 0) { sound = string.Empty; continue; }
                    var stop = Array.IndexOf(bytes, (byte)0, field.Start, field.Size);
                    if(stop < 0) throw new InvalidDataException("Unterminated AVFX sound name.");
                    sound = Encoding.UTF8.GetString(bytes, field.Start, stop - field.Start).Replace('\\', '/');
                } else if(field.Tag == "oNdS") {
                    if(index != null || field.Size != 4) throw new InvalidDataException("Invalid AVFX sound index.");
                    index = I32(field.Start);
                }
            }
            if(!string.IsNullOrEmpty(sound) && sound.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && index is >= 0)
                result.Add(new(sound, (uint)index.Value));
            if(result.Count > 512) throw new InvalidDataException("AVFX sound limit exceeded.");
        }
        return result.Distinct().ToList();
    }
}
