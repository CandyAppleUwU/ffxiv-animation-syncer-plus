// Added: frame calculations for loaded spline and interleaved Havok clips.
using System;
using System.Buffers.Binary;

namespace AnimSync;

internal static class ClipFrames {
	public const int QuantizedHeaderSize = 40;
	public static int QuantizedFrameCount(ReadOnlySpan<byte> header, int dataLength, float duration) {
		if(header.Length < QuantizedHeaderSize) return 0;
		var size = BinaryPrimitives.ReadUInt16LittleEndian(header);
		var storedDuration = BinaryPrimitives.ReadSingleLittleEndian(header[8..]);
		if(size < QuantizedHeaderSize || size > dataLength || !float.IsFinite(storedDuration) || !float.IsFinite(duration) ||
			Math.Abs(storedDuration - duration) >= .001f) return 0;
		return BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
	}
	public static int PredictiveFrameCount(int frames, int bones, int floatSlots, int transformTracks, int floatTracks) =>
		frames is >= 1 and <= 1000000 && bones is >= 0 and <= 4096 && floatSlots is >= 0 and <= 4096 &&
		transformTracks >= 0 && transformTracks <= bones && floatTracks >= 0 && floatTracks <= floatSlots &&
		(transformTracks > 0 || floatTracks > 0) ? frames : 0;
	public static int InterleavedFrameCount(int transforms, int transformTracks, int floats, int floatTracks) {
		if(transforms < 0 || floats < 0 || transformTracks < 0 || floatTracks < 0) return 0;
		if(transformTracks == 0 && transforms != 0 || floatTracks == 0 && floats != 0) return 0;
		if(transformTracks > 0 && transforms % transformTracks != 0 || floatTracks > 0 && floats % floatTracks != 0) return 0;
		var transformFrames = transformTracks > 0 ? transforms / transformTracks : 0;
		var floatFrames = floatTracks > 0 ? floats / floatTracks : 0;
		if(transformTracks > 0 && floatTracks > 0 && transformFrames != floatFrames) return 0;
		return Math.Max(transformFrames, floatFrames);
	}
	public static int CurrentFrame(float time, float duration, int count) =>
		(int)Math.Clamp(Math.Floor((double)time / duration * (count - 1)), 0, count - 1);
}
