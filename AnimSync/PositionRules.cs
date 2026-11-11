// Added: range/selection rules adapted from the user's CoordMatch alignment tool.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimSync;

internal readonly record struct PositionCandidate(ulong Id, Vector3 Position);
internal static class PositionRules {
	public const float MaxDistance = 0.5f;
	public static Vector3 LocalOffset(Vector3 source, Vector3 destination, float yaw) =>
		Vector3.Transform(destination - source, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw));
	/// <summary>Reads the line Blender's "Copy Align Offset" puts on the clipboard (or just numbers):
	/// "x y z" = offset from the partner bone in the partner's facing axes (X side, Y up, Z forward, yalms),
	/// and an optional 4th number = how far you face turned from your partner, in degrees.</summary>
	public static bool TryParseBlenderOffset(string? text, out Vector3 offset) => TryParseBlenderOffset(text, out offset, out _);
	public static bool TryParseBlenderOffset(string? text, out Vector3 offset, out float yawDegrees) {
		offset = default;
		yawDegrees = 0;
		if(string.IsNullOrWhiteSpace(text)) return false;
		var numbers = new List<float>();
		foreach(var token in text.Split(new[] { ' ', '\t', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
			if(!float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;
			if(!float.IsFinite(value)) return false;
			numbers.Add(value);
		}
		if(numbers.Count < 3 || numbers.Count > 4) return false;
		offset = new Vector3(numbers[0], numbers[1], numbers[2]);
		if(numbers.Count == 4) yawDegrees = numbers[3];
		return offset.Length() <= 5f && MathF.Abs(yawDegrees) <= 360f;
	}
	/// <summary>Reads "AnimSyncAlign &lt;your bones joined by +&gt; &lt;partner bone&gt; x y z [yaw]". Bone names
	/// are optional (defaults: j_kao and iv_ochinko_f).</summary>
	public static bool TryParseBlenderAlign(string? text, out string[] selfBones, out string partnerBone, out Vector3 offset) =>
		TryParseBlenderAlign(text, out selfBones, out partnerBone, out offset, out _);
	public static bool TryParseBlenderAlign(string? text, out string[] selfBones, out string partnerBone, out Vector3 offset, out float yawDegrees) {
		selfBones = new[] { "j_kao" };
		partnerBone = "iv_ochinko_f";
		if(!TryParseBlenderOffset(text, out offset, out yawDegrees)) return false;
		var words = text!.Split(new[] { ' ', '\t', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		var names = new List<string>();
		foreach(var word in words) {
			if(word.Equals("AnimSyncAlign", StringComparison.OrdinalIgnoreCase)) continue;
			if(float.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
			names.Add(word);
		}
		if(names.Count >= 2) {
			selfBones = names[0].Split('+', StringSplitOptions.RemoveEmptyEntries);
			partnerBone = names[1];
		}
		return selfBones.Length > 0 && selfBones.Length <= 16;
	}
	/// <summary>SimpleHeels chat command that ADDS a Bone Align delta to the temp offset
	/// (its axes: Y height, Z forward, X left; values parsed with the invariant culture).</summary>
	public static string SimpleHeelsAddCommand(Vector3 delta) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
		$"/heels temp add height {delta.Y:0.#####} forward {delta.Z:0.#####} left {delta.X:0.#####} silent");
	/// <summary>Reversed roles: you own the "partner bone" and the partner owns the "your bones" of
	/// the preset. Your bone must sit at their point minus the Blender offset, turned with the facing
	/// you should have.</summary>
	public static Vector3 ReverseAlignTarget(Vector3 theirPoint, Vector3 offset, float yourTargetYaw) =>
		theirPoint - Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yourTargetYaw));
	/// <summary>Where your bone must be: the partner's bone plus the Blender offset, turned with the partner.</summary>
	public static Vector3 AlignTarget(Vector3 partnerBone, Vector3 offset, Quaternion partnerRotation) =>
		partnerBone + Vector3.Transform(offset, partnerRotation);
	public static bool AllowsAnimation(string? papPath) {
		if(string.IsNullOrEmpty(papPath) || !papPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) return false;
		var path = "/" + papPath.Replace('\\', '/');
		if(path.EndsWith("/resident/idle.pap", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith("/resident/move_a.pap", StringComparison.OrdinalIgnoreCase)) return true;
		var fileStart = path.LastIndexOf('/') + 1;
		return path.AsSpan(0, fileStart).EndsWith("/emote/", StringComparison.OrdinalIgnoreCase) &&
			path.AsSpan(fileStart).StartsWith("pose", StringComparison.OrdinalIgnoreCase);
	}
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
