// Added: range/selection rules adapted from the user's CoordMatch alignment tool.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimSync;

internal readonly record struct PositionCandidate(ulong Id, Vector3 Position);
internal static class PositionRules {
	public const float MaxDistance = 0.5f;
	public static bool InRange(Vector3 self, Vector3 other) {
		var distance = Vector3.DistanceSquared(self, other);
		return float.IsFinite(distance) && distance <= MaxDistance * MaxDistance;
	}
	public static ulong? Closest(Vector3 self, ulong selfId, IEnumerable<PositionCandidate> candidates) {
		ulong? result = null;
		var best = float.PositiveInfinity;
		foreach(var candidate in candidates) {
			if(candidate.Id == selfId || !InRange(self, candidate.Position)) continue;
			var distance = Vector3.DistanceSquared(self, candidate.Position);
			if(distance < best || (distance == best && (!result.HasValue || candidate.Id < result.Value))) {
				best = distance;
				result = candidate.Id;
			}
		}
		return result;
	}
	public static float AngleDelta(float a, float b) {
		var delta = (a - b) % MathF.Tau;
		if(delta > MathF.PI) delta -= MathF.Tau;
		if(delta < -MathF.PI) delta += MathF.Tau;
		return delta;
	}
}
