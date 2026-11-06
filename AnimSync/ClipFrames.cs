// Added: frame calculations for loaded spline and interleaved Havok clips.
using System;

namespace AnimSync;

internal static class ClipFrames {
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
