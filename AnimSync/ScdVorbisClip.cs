using System;
using System.IO;

namespace AnimSync;

// Deliberately limited to the single-entry, unencrypted Vorbis files inspected in Luna Vibes.
internal sealed record ScdVorbisClip(byte[] Ogg, int Channels, int SampleRate) {
    public static ScdVorbisClip Read(byte[] bytes) {
        if(bytes.Length < 80 || bytes.Length > 64 * 1024 * 1024 ||
            !bytes.AsSpan(0, 8).SequenceEqual("SEDBSSCF"u8) || bytes[12] != 0)
            throw new InvalidDataException("Unsupported or invalid SCD header.");
        int U16(int p) { Bounds(bytes, p, 2); return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2)); }
        int I32(int p) { Bounds(bytes, p, 4); return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4)); }
        var header = U16(14);
        Bounds(bytes, header, 32);
        if(U16(header + 4) != 1) throw new InvalidDataException("This test supports one audio entry per SCD.");
        var table = I32(header + 12);
        var audio = I32(table);
        if(audio <= 0) throw new InvalidDataException("The audio entry is empty.");
        Bounds(bytes, audio, 32);
        var length = I32(audio);
        var channels = I32(audio + 4);
        var rate = I32(audio + 8);
        var subSize = I32(audio + 24);
        if(length <= 0 || channels is < 1 or > 2 || rate is < 8000 or > 192000 ||
            I32(audio + 12) != 6 || (I32(audio + 28) & 3) != 0)
            throw new InvalidDataException("This test requires mono/stereo Vorbis without marker data.");
        var info = checked(audio + 32);
        Bounds(bytes, info, 32);
        if(U16(info) != 0) throw new InvalidDataException("Encrypted Vorbis is not supported by this test.");
        var seekBytes = I32(info + 16);
        var oggHeaderBytes = I32(info + 20);
        if(seekBytes < 0 || seekBytes % 4 != 0 || oggHeaderBytes < 0 ||
            subSize != checked(32 + seekBytes + oggHeaderBytes))
            throw new InvalidDataException("Unsupported Vorbis metadata layout.");
        var start = checked(info + 32 + seekBytes);
        var total = checked(oggHeaderBytes + length);
        Bounds(bytes, start, total);
        if(!bytes.AsSpan(start, 4).SequenceEqual("OggS"u8)) throw new InvalidDataException("Missing Ogg Vorbis header.");
        return new(bytes.AsSpan(start, total).ToArray(), channels, rate);
    }
    private static void Bounds(byte[] bytes, int start, int length) {
        if(start < 0 || length < 0 || (long)start + length > bytes.Length)
            throw new InvalidDataException("SCD offsets are outside the file.");
    }
}
