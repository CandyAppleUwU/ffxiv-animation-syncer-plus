using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Buffers.Binary;

namespace AnimSync;

internal sealed record PapSoundReference(ushort HavokIndex, string Path, uint SoundIndex);
internal sealed record PapVfxReference(ushort HavokIndex, string Path);
internal static class PapSoundReferences {
    // Preserve all binding references. The caller verifies the live body binding;
    // mod PAP entries marked Face may still carry its VFX/sound dependencies.
    public static List<PapSoundReference> Read(byte[] bytes, List<PapVfxReference>? vfx = null) {
        void Bounds(int p, int n) { if(p < 0 || n < 0 || (long)p + n > bytes.Length) throw new InvalidDataException("Invalid PAP/TMB offsets."); }
        int I32(int p) { Bounds(p, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4)); }
        ushort U16(int p) { Bounds(p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2)); }
        Bounds(0, 26);
        if(!bytes.AsSpan(0, 4).SequenceEqual("pap "u8) || bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Unsupported PAP file.");
        var count = U16(8); var info = I32(14); var havok = I32(18); var footer = I32(22);
        if(count is 0 or > 512 || info < 26 || havok < info || footer < havok) throw new InvalidDataException("Invalid PAP header.");
        Bounds(info, count * 40); Bounds(footer, 12);
        if(info + count * 40 > havok) throw new InvalidDataException("PAP animation table overlaps Havok data.");
        var result = new List<PapSoundReference>();
        var tmb = footer;
        for(var anim = 0; anim < count; anim++) {
            Bounds(tmb, 12);
            if(!bytes.AsSpan(tmb, 4).SequenceEqual("TMLB"u8)) throw new InvalidDataException("Missing embedded TMB.");
            var size = I32(tmb + 4); var entries = I32(tmb + 8);
            Bounds(tmb, size);
            if(size < 12 || entries is < 0 or > 20000) throw new InvalidDataException("Invalid TMB header.");
            var end = checked(tmb + size); var p = tmb + 12;
            for(var item = 0; item < entries; item++) {
                if(p + 8 > end) throw new InvalidDataException("Truncated TMB entry.");
                var entrySize = I32(p + 4);
                if(entrySize < 8 || (long)p + entrySize > end) throw new InvalidDataException("Invalid TMB entry length.");
                if(bytes.AsSpan(p, 4).SequenceEqual("C063"u8) && entrySize == 32) {
                    var start = checked(p + 8 + I32(p + 20));
                    if(start < tmb || start >= end) throw new InvalidDataException("Invalid sound path offset.");
                    var stop = Array.IndexOf(bytes, (byte)0, start, Math.Min(1024, end - start));
                    if(stop < start) throw new InvalidDataException("Unterminated sound path.");
                    var path = Encoding.UTF8.GetString(bytes, start, stop - start).Replace('\\', '/');
                    if(path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                        result.Add(new(U16(info + anim * 40 + 34), path, unchecked((uint)I32(p + 24))));
                }
                if(vfx != null && (bytes.AsSpan(p, 4).SequenceEqual("C012"u8) && entrySize == 0x48 ||
                    bytes.AsSpan(p, 4).SequenceEqual("C173"u8) && entrySize == 0x44)) {
                    var start = checked(p + 8 + I32(p + 20));
                    if(start < tmb || start >= end) throw new InvalidDataException("Invalid VFX path offset.");
                    var stop = Array.IndexOf(bytes, (byte)0, start, Math.Min(1024, end - start));
                    if(stop < start) throw new InvalidDataException("Unterminated VFX path.");
                    var path = Encoding.UTF8.GetString(bytes, start, stop - start).Replace('\\', '/');
                    if(path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) vfx.Add(new(U16(info + anim * 40 + 34), path));
                }
                p += entrySize;
            }
            tmb = end;
            if(anim < count - 1) tmb += (4 - (tmb - footer) % 4) % 4;
        }
        return result;
    }
}
