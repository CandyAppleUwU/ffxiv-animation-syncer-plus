using System;
using System.Buffers.Binary;
using System.IO;

namespace AnimSync;

// Sound entry routing is separate from both the Vorbis audio entry and Player/Party volume categories.
internal sealed record ScdSoundInfo(int Bus, float Volume) {
    public static ScdSoundInfo Read(byte[] bytes, uint soundIndex) {
        void Bounds(int p, int size) {
            if(p < 0 || size < 0 || (long)p + size > bytes.Length) throw new InvalidDataException("Invalid SCD sound table.");
        }
        int U16(int p) { Bounds(p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2)); }
        int I32(int p) { Bounds(p, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4)); }
        if(bytes.Length < 80 || !bytes.AsSpan(0, 8).SequenceEqual("SEDBSSCF"u8) || bytes[12] != 0)
            throw new InvalidDataException("Unsupported SCD sound header.");
        var header = U16(14);
        Bounds(header, 32);
        if(soundIndex >= U16(header)) throw new InvalidDataException("SCD sound index is missing.");
        var entry = I32(checked(header + 32 + (int)soundIndex * 4));
        if(entry <= 0) throw new InvalidDataException("SCD sound entry is empty.");
        Bounds(entry, 16);
        var bus = bytes[entry + 1];
        var volume = BitConverter.Int32BitsToSingle(I32(entry + 8));
        if(bus is < 1 or > 20 || !float.IsFinite(volume) || volume < 0)
            throw new InvalidDataException("SCD sound channel or volume is unsupported.");
        // Bus-ducking describes changes to OTHER buses; it does not replace this entry's BusNumber.
        return new(bus, volume);
    }
}
