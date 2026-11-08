using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AnimSync;

internal static class TmbPropModels {
    public static HashSet<ushort> Read(ReadOnlySpan<byte> data) {
        var models = new HashSet<ushort>();
        if(data.Length < 12 || !data[..4].SequenceEqual("TMLB"u8)) return models;
        var size = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if(size < 12 || size > data.Length || count is < 0 or > 20000) return models;
        var offset = 12;
        for(var index = 0; index < count; index++) {
            if(size - offset < 8) return new();
            var entry = data[offset..size];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            if(length < 8 || length > entry.Length) return new();
            if(length == 0x28 && entry[..4].SequenceEqual("C198"u8)) {
                var model = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x20..]);
                if(model != 0) models.Add(model);
            }
            offset += length;
        }
        return models;
    }
}
