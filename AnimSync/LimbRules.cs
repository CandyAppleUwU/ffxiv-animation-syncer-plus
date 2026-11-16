using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace AnimSync;

/// <summary>One locked limb's entry for LivePose: the end bone (foot or hand), how far to move it (model space) and
/// how to turn it. LivePose bends the limb to get it there (its own two-joint IK), every frame, on top of the animation.</summary>
/// <remarks>Ik = false: a plain turn of the bone (what it carries follows), no IK.</remarks>
public sealed record LimbStack(string Bone, Vector3 Offset, Quaternion Rotation, bool Foot, bool Ik = true);

/// <summary>The maths of locking a foot or hand in place while the body is moved (see LimbLock.cs). No game access.</summary>
public static class LimbRules {
	public const string FootBone = "j_asi_d_";
	public const string HandBone = "j_te_";

	/// <summary>A world point in the character's model space (the space bones and LivePose offsets are in).</summary>
	public static Vector3 ModelPoint(Vector3 world, Vector3 rootPosition, Quaternion rootRotation, Vector3 rootScale) {
		var local = Vector3.Transform(world - rootPosition, Quaternion.Inverse(rootRotation));
		return new Vector3(Divide(local.X, rootScale.X), Divide(local.Y, rootScale.Y), Divide(local.Z, rootScale.Z));
	}

	private static float Divide(float value, float by) => MathF.Abs(by) < 1e-6f ? value : value / by;

	public static Vector3 WorldPoint(Vector3 model, Vector3 rootPosition, Quaternion rootRotation, Vector3 rootScale) =>
		rootPosition + Vector3.Transform(model * rootScale, rootRotation);

	/// <summary>How far the bone has to be moved (model space) to be back at the world spot it was locked at, now that
	/// the character stands somewhere else. Zero while the character hasn't moved.</summary>
	public static Vector3 Offset(Vector3 lockedWorld, Vector3 modelAtLock, Vector3 rootPosition, Quaternion rootRotation, Vector3 rootScale) =>
		ModelPoint(lockedWorld, rootPosition, rootRotation, rootScale) - modelAtLock;

	/// <summary>The offset wanted for a locked limb, with the axes that have been let go left out: height, forward /
	/// back and left / right are the character's own (as the offset sliders are); with rotation let go, the limb's
	/// spot swings round with the character when it turns, as if it weren't locked against turning.</summary>
	public static Vector3 Wanted(Vector3 lockedWorld, Vector3 modelAtLock, Vector3 rootAtLock, Quaternion rootRotationAtLock,
		Vector3 rootPosition, Quaternion rootRotation, Vector3 rootScale, bool height, bool forward, bool left, bool rotation) {
		var target = rotation ? lockedWorld : rootAtLock + Vector3.Transform(lockedWorld - rootAtLock, rootRotation * Quaternion.Inverse(rootRotationAtLock));
		var offset = Offset(target, modelAtLock, rootPosition, rootRotation, rootScale);
		return new Vector3(left ? offset.X : 0, height ? offset.Y : 0, forward ? offset.Z : 0);
	}

	/// <summary>The way a locked limb should face (world): as when locked - or turned along with the character when
	/// rotation has been let go.</summary>
	public static Quaternion Facing(Quaternion worldAtLock, Quaternion rootRotationAtLock, Quaternion rootRotation, bool rotation) =>
		rotation ? worldAtLock : Quaternion.Normalize(rootRotation * Quaternion.Inverse(rootRotationAtLock) * worldAtLock);

	/// <summary>The model-space rotation the bone needs now to face the way it did (in the world) when locked.</summary>
	public static Quaternion DesiredModel(Quaternion rootRotation, Quaternion worldAtLock) =>
		Quaternion.Normalize(Quaternion.Inverse(rootRotation) * worldAtLock);

	/// <summary>The turn to give LivePose (it multiplies it onto the bone's rotation) so the bone, measured with the
	/// turn already sent, ends up at the wanted rotation.</summary>
	public static Quaternion RotationFix(Quaternion sent, Quaternion measured, Quaternion desired) =>
		Quaternion.Normalize(sent * Quaternion.Inverse(measured) * desired);

	/// <summary>The shortest turn taking one direction to another.</summary>
	public static Quaternion FromTo(Vector3 from, Vector3 to) {
		if(from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Quaternion.Identity;
		from = Vector3.Normalize(from);
		to = Vector3.Normalize(to);
		var dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
		if(dot > 0.999999f) return Quaternion.Identity;
		var axis = Vector3.Cross(from, to);
		if(axis.LengthSquared() < 1e-12f) {   // opposite: any axis square to it
			axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
		}
		return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
	}

	/// <summary>What to send LivePose for one limb, worked out the way its two-joint IK will bend it (all in model
	/// space, from the pose when the lock took hold): the middle joint (knee / elbow) turns on its hinge until the limb
	/// is the right length, then the whole limb swings from the top joint (hip / shoulder) onto the target.
	///  - The offset is cut short where the limb can't reach: the leg goes straight and the foot comes up with the
	///    body, instead of the bone being stretched to a spot it can't get to.
	///  - The turn undoes what that bending does to the foot / hand, so it keeps facing the way it did.</summary>
	public static (Vector3 Offset, Quaternion Rotation) Solve(Vector3 top, Vector3 middle, Vector3 end, Quaternion middleRotation, Quaternion endRotation,
		Vector3 hingeLocal, Vector3 wantedOffset, Quaternion desiredEndRotation, bool keepHeight = false) {
		float upper = Vector3.Distance(top, middle), lower = Vector3.Distance(middle, end);
		if(upper < 1e-4f || lower < 1e-4f) return (wantedOffset, Quaternion.Identity);
		var target = end + wantedOffset;
		var reach = (upper + lower) * 0.998f;
		var away = target - top;
		if(away.Length() > reach) {
			var drop = target.Y - top.Y;
			var across = new Vector3(away.X, 0, away.Z);
			// a foot put on the floor stays on the floor: it comes in under the body as far as it must, at that height.
			// Otherwise (and when even straight down is too far) the limb goes straight towards the spot.
			if(keepHeight && MathF.Abs(drop) < reach && across.Length() > 1e-5f)
				target = top + new Vector3(0, drop, 0) + Vector3.Normalize(across) * MathF.Sqrt(reach * reach - drop * drop);
			else target = top + away * (reach / away.Length());
		}
		var far = MathF.Max(Vector3.Distance(target, top), MathF.Abs(upper - lower) + 1e-4f);
		// the hinge: from the bend it has to the bend that makes the limb that long
		static float Bend(float a, float b, float c) => MathF.Acos(Math.Clamp((a * a + b * b - c * c) / (2 * a * b), -1, 1));
		var change = Bend(upper, lower, far) - Bend(upper, lower, Vector3.Distance(top, end));
		var hinge = Vector3.Transform(hingeLocal, middleRotation);
		hinge = hinge.LengthSquared() < 1e-10f ? Vector3.UnitX : Vector3.Normalize(hinge);
		var folded = Quaternion.Identity;
		var miss = float.PositiveInfinity;
		foreach(var sign in new[] { 1f, -1f }) {
			var turn = Quaternion.CreateFromAxisAngle(hinge, sign * change);
			var error = MathF.Abs(Vector3.Distance(middle + Vector3.Transform(end - middle, turn), top) - far);
			if(error >= miss) continue;
			miss = error;
			folded = turn;
		}
		var bent = middle + Vector3.Transform(end - middle, folded);
		var swing = FromTo(bent - top, target - top);
		var endNow = Quaternion.Normalize(swing * folded * endRotation);   // how the bending leaves the foot / hand
		return (target - end, Quaternion.Normalize(Quaternion.Inverse(endNow) * desiredEndRotation));
	}

	/// <summary>The same lock done with turns only - nothing is moved and LivePose's IK isn't used: a turn for the top
	/// joint (thigh / upper arm), one for the middle joint (calf / forearm) and one for the end (foot / hand), each to
	/// be multiplied onto that bone's own rotation, in that order, with what hangs off it following.
	/// False when turning alone can't put the end back (out of reach, or the joint doesn't hinge that way): give up
	/// and leave that limb as the animation has it.</summary>
	public static bool SolveTurns(Vector3 top, Vector3 middle, Vector3 end, Quaternion topRotation, Quaternion middleRotation, Quaternion endRotation,
		Vector3 hingeLocal, Vector3 wantedOffset, Quaternion desiredEndRotation, out Quaternion topTurn, out Quaternion middleTurn, out Quaternion endTurn) {
		topTurn = middleTurn = endTurn = Quaternion.Identity;
		float upper = Vector3.Distance(top, middle), lower = Vector3.Distance(middle, end);
		if(upper < 1e-4f || lower < 1e-4f) return false;
		var target = end + wantedOffset;
		var far = Vector3.Distance(target, top);
		if(far > (upper + lower) * 0.999f || far < MathF.Abs(upper - lower) + 1e-3f) return false;
		static float Bend(float a, float b, float c) => MathF.Acos(Math.Clamp((a * a + b * b - c * c) / (2 * a * b), -1, 1));
		var change = Bend(upper, lower, far) - Bend(upper, lower, Vector3.Distance(top, end));
		var hinge = Vector3.Transform(hingeLocal, middleRotation);
		if(hinge.LengthSquared() < 1e-10f) return false;
		hinge = Vector3.Normalize(hinge);
		float Miss(float angle) => Vector3.Distance(middle + Vector3.Transform(end - middle, Quaternion.CreateFromAxisAngle(hinge, angle)), top) - far;
		// which way round the hinge, then home in on the exact angle (the hinge is rarely dead square to the limb)
		var angle = MathF.Abs(Miss(change)) <= MathF.Abs(Miss(-change)) ? change : -change;
		for(var pass = 0; pass < 12; pass++) {
			var miss = Miss(angle);
			if(MathF.Abs(miss) < 1e-5f) break;
			var slope = (Miss(angle + 0.001f) - miss) / 0.001f;
			if(MathF.Abs(slope) < 1e-6f) break;
			angle -= Math.Clamp(miss / slope, -0.3f, 0.3f);
		}
		if(MathF.Abs(Miss(angle)) > 0.002f || MathF.Abs(angle) > MathF.PI) return false;
		var folded = Quaternion.CreateFromAxisAngle(hinge, angle);
		var swing = FromTo(middle + Vector3.Transform(end - middle, folded) - top, target - top);
		topTurn = Quaternion.Normalize(Quaternion.Inverse(topRotation) * swing * topRotation);
		middleTurn = Quaternion.Normalize(Quaternion.Inverse(middleRotation) * folded * middleRotation);
		endTurn = Quaternion.Normalize(Quaternion.Inverse(Quaternion.Normalize(swing * folded * endRotation)) * desiredEndRotation);
		return true;
	}

	/// <summary>The angle between two rotations, in degrees.</summary>
	public static float Angle(Quaternion a, Quaternion b) {
		var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
		return 2 * MathF.Acos(Math.Clamp(dot, 0, 1)) * 180 / MathF.PI;
	}

	public static bool Negligible(LimbStack stack) => stack.Offset.Length() < 0.0004f && Angle(stack.Rotation, Quaternion.Identity) < 0.2f;

	/// <summary>LivePose's pose data (what LivePose.GetPose gave) with the locked limbs added for the animation playing
	/// (its two timeline ids). What was already there is kept; a bone that already has an entry gets one more stack.</summary>
	public static string PoseJson(string? baseJson, ushort timeline, ushort secondTimeline, IReadOnlyList<LimbStack> stacks) {
		JsonObject root;
		try { root = string.IsNullOrWhiteSpace(baseJson) ? new JsonObject() : JsonNode.Parse(baseJson) as JsonObject ?? new JsonObject(); }
		catch(System.Text.Json.JsonException) { root = new JsonObject(); }
		JsonArray pose;
		if(root["CursedPose"] is JsonArray cursed) pose = cursed;   // LivePose's one-pose-for-everything mode
		else {
			if(root["BodyPoses"] is not JsonArray bodies) root["BodyPoses"] = bodies = new JsonArray();
			JsonObject? entry = null;
			foreach(var node in bodies)
				if(node is JsonObject o && Number(o["TimelineId"]) == timeline && Number(o["SecondaryTimelineId"]) == secondTimeline) { entry = o; break; }
			if(entry == null) bodies.Add(entry = new JsonObject { ["TimelineId"] = timeline, ["SecondaryTimelineId"] = secondTimeline, ["Pose"] = new JsonArray() });
			if(entry["Pose"] is not JsonArray list) entry["Pose"] = list = new JsonArray();
			pose = list;
		}
		foreach(var stack in stacks) {
			if(Negligible(stack)) continue;
			JsonObject? bone = null;
			foreach(var node in pose)
				if(node is JsonObject o && o["BonePoseInfoId"] is JsonObject id && (string?)id["BoneName"] == stack.Bone && Number(id["Partial"]) == 0 && Number(id["Slot"]) == 0) { bone = o; break; }
			if(bone == null) pose.Add(bone = new JsonObject {
				["BonePoseInfoId"] = new JsonObject { ["BoneName"] = stack.Bone, ["Partial"] = 0, ["Slot"] = 0 }, ["Stacks"] = new JsonArray() });
			if(bone["Stacks"] is not JsonArray entries) bone["Stacks"] = entries = new JsonArray();
			entries.Add(new JsonObject {
				["Transform"] = new JsonObject {
					["Position"] = Vector(stack.Offset),
					["Rotation"] = new JsonObject { ["X"] = Round(stack.Rotation.X), ["Y"] = Round(stack.Rotation.Y), ["Z"] = Round(stack.Rotation.Z), ["W"] = Round(stack.Rotation.W) },
					["Scale"] = Vector(Vector3.Zero),
				},
				["Propogate"] = 3,   // position + rotation carry on to what hangs off the bone (LivePose's own spelling)
			});
			if(!stack.Ik) continue;
			var added = (JsonObject)entries[^1]!;
			added["IK_Enabled"] = true;
			added["IK_Type"] = 1;     // two-joint: thigh-knee-foot, or upper arm-forearm-hand (LivePose's defaults for these bones)
			added["IK_Arg0"] = stack.Foot ? 3 : 2;
			added["IK_Arg1"] = 1;
			added["IK_Arg2"] = 0;
			added["IK_RotationAxis"] = Vector(stack.Foot ? -Vector3.UnitZ : Vector3.UnitZ);
		}
		return root.ToJsonString();
	}


	/// <summary>A rotation as Brio writes it in pose JSON: "x, y, z, w" text, {"X":..} fields or a list of four numbers.</summary>
	public static bool TryReadQuaternion(JsonNode? node, out Quaternion rotation) {
		rotation = Quaternion.Identity;
		float[]? v = null;
		try {
			if(node is JsonValue value && value.TryGetValue<string>(out var text)) {
				var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if(parts.Length == 4) v = Array.ConvertAll(parts, p => float.Parse(p, CultureInfo.InvariantCulture));
			} else if(node is JsonObject o) {
				float Field(string k) { foreach(var (name, n) in o) if(string.Equals(name, k, StringComparison.OrdinalIgnoreCase) && n != null) return n.GetValue<float>(); throw new FormatException(); }
				v = new[] { Field("X"), Field("Y"), Field("Z"), Field("W") };
			} else if(node is JsonArray a && a.Count == 4) v = new[] { a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>(), a[3]!.GetValue<float>() };
		} catch(Exception ex) when(ex is FormatException or InvalidOperationException or OverflowException) { return false; }
		if(v == null || !v.All(float.IsFinite)) return false;
		var q = new Quaternion(v[0], v[1], v[2], v[3]);
		if(q.LengthSquared() < 1e-8f) return false;
		rotation = Quaternion.Normalize(q);
		return true;
	}

	/// <summary>The same rotation in the same shape as <paramref name="like"/> (what was there before).</summary>
	public static JsonNode WriteQuaternion(Quaternion q, JsonNode? like) {
		if(like is JsonObject o) {
			var copy = new JsonObject();
			foreach(var (name, _) in o) {
				var key = name.ToUpperInvariant();
				copy[name] = key switch { "X" => JsonValue.Create(q.X), "Y" => JsonValue.Create(q.Y), "Z" => JsonValue.Create(q.Z), "W" => JsonValue.Create(q.W), _ => (JsonNode?)o[name]?.DeepClone() };
			}
			return copy;
		}
		if(like is JsonArray) return new JsonArray(JsonValue.Create(q.X), JsonValue.Create(q.Y), JsonValue.Create(q.Z), JsonValue.Create(q.W));
		return JsonValue.Create(string.Create(CultureInfo.InvariantCulture, $"{q.X:0.#######}, {q.Y:0.#######}, {q.Z:0.#######}, {q.W:0.#######}"))!;
	}

	private static double Round(float value) => Math.Round(value, 5);
	private static JsonObject Vector(Vector3 v) => new() { ["X"] = Round(v.X), ["Y"] = Round(v.Y), ["Z"] = Round(v.Z) };

	private static int Number(JsonNode? node) {
		if(node is not JsonValue value) return -1;
		if(value.TryGetValue<int>(out var number)) return number;
		if(value.TryGetValue<double>(out var real)) return (int)real;
		return value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : -1;
	}
}
