// Added: deterministic nearby grouping, independent of native game memory.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AnimSync;

internal readonly record struct AnimationSample(ulong Id, string Name, Vector3 Position,
	ushort Timeline, int Frame, int FrameCount, float Duration, float Speed, float LocalTime = 0);

internal static class NearbyGroups {
	public static AnimationSample Leader(IReadOnlyList<AnimationSample> group) => group
		.OrderByDescending(s => s.Frame).ThenByDescending(s => s.LocalTime).ThenBy(s => s.Id).First();
	public static List<List<AnimationSample>> Find(IReadOnlyList<AnimationSample> samples, float distance) {
		var result = new List<List<AnimationSample>>();
		var remaining = samples.OrderBy(s => s.Id).ToList();
		while(remaining.Count > 0) {
			var group = new List<AnimationSample> { remaining[0] };
			remaining.RemoveAt(0);
			for(var i = 0; i < remaining.Count;) {
				var candidate = remaining[i];
				if(group.All(member => Compatible(member, candidate) &&
					Vector3.DistanceSquared(member.Position, candidate.Position) <= distance * distance)) {
					group.Add(candidate);
					remaining.RemoveAt(i);
				} else i++;
			}
			if(group.Count >= 2 && group.Max(s => s.Frame) != group.Min(s => s.Frame))
				result.Add(group);
		}
		return result;
	}
	private static bool Compatible(AnimationSample a, AnimationSample b) =>
		a.FrameCount == b.FrameCount &&
		Math.Abs(a.Duration - b.Duration) < 0.001f && Math.Abs(a.Speed - b.Speed) < 0.001f;
}
