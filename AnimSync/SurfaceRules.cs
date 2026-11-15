using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AnimSync;

/// <summary>How an animation meets the world: back or front against a wall (feet on the floor), or just the floor.</summary>
public enum SurfaceKind { WallBack, WallFront, Floor, Table, Pole, Low }

/// <summary>"When this mod plays this animation, put the character against a surface."
/// Mod = part of the Penumbra mod folder name (empty = any mod, or none); Animation = ActionTimeline key.</summary>
[Serializable]
public sealed class SurfaceProfile {
	public string Name = string.Empty;
	public string Mod = string.Empty;
	public string Animation = string.Empty;
	public SurfaceKind Kind = SurfaceKind.WallBack;
	public bool Enabled = true;

	public SurfaceProfile() { }
	public SurfaceProfile(string name, string mod, string animation, SurfaceKind kind) {
		Name = name; Mod = mod; Animation = animation; Kind = kind;
	}

	public bool Matches(string? modFolder, string? animationKey) =>
		Enabled && SurfaceRules.SameKey(Animation, animationKey) &&
		(Mod.Trim().Length == 0 || (modFolder ?? string.Empty).Contains(Mod.Trim(), StringComparison.OrdinalIgnoreCase));
}

internal static class SurfaceRules {
	/// <summary>Built-in profiles (they can be switched off, user ones are added after them).</summary>
	public static readonly IReadOnlyList<SurfaceProfile> BuiltIn = new[] {
		// [Candy] Wall Ride: /groundsit 1 (the "giver") rides the wall - back and arms on it, feet on the floor.
		new SurfaceProfile("Wall Ride (gsit 1)", "Wall Ride", "emote/j_pose01_loop", SurfaceKind.WallBack),
	};

	// Bones that touch the surface. Gaps = how far the bone sits inside the body from the skin there (yalms, at scale 1).
	public static readonly (string Bone, float Gap)[] BackContacts = {
		("j_sebo_c", 0.10f), ("j_sebo_b", 0.11f), ("j_te_l", 0.03f), ("j_te_r", 0.03f),
	};
	public static readonly (string Bone, float Gap)[] FrontContacts = {
		("j_te_l", 0.03f), ("j_te_r", 0.03f),
	};
	/// <summary>Everything that may touch the wall (and must never end up inside it): back, elbows, hands, fingertips.</summary>
	public static readonly (string Bone, float Gap)[] WallTouch = {
		("j_sebo_c", 0.10f), ("j_sebo_b", 0.11f), ("j_kosi", 0.10f),
		("j_ude_b_l", 0.04f), ("j_ude_b_r", 0.04f), ("j_te_l", 0.03f), ("j_te_r", 0.03f),
		// last finger joints; the fingertip reaches ~2 cm past them
		("j_hito_b_l", 0.02f), ("j_hito_b_r", 0.02f), ("j_naka_b_l", 0.02f), ("j_naka_b_r", 0.02f),
		("j_kusu_b_l", 0.02f), ("j_kusu_b_r", 0.02f), ("j_ko_b_l", 0.02f), ("j_ko_b_r", 0.02f),
		("j_oya_b_l", 0.02f), ("j_oya_b_r", 0.02f),
	};
	/// <summary>The touch bones with how far the skin is from each: bones sit inside the body, so the back/hips keep the
	/// body's thickness and the elbows/hands/fingers the arm's (fingers a bit less, elbows a bit more).</summary>
	public static IEnumerable<(string Bone, float Gap)> WallTouchBones(float body, float hand) {
		foreach(var (bone, _) in WallTouch) {
			var gap = bone.StartsWith("j_sebo", StringComparison.Ordinal) || bone == "j_kosi" ? body
				: bone.StartsWith("j_ude", StringComparison.Ordinal) ? hand + 0.01f
				: bone.StartsWith("j_te", StringComparison.Ordinal) ? hand
				: MathF.Max(0.005f, hand - 0.01f);
			yield return (bone, gap);
		}
	}

	/// <summary>Foot bones that must stay above the floor: ankles and toes.</summary>
	public static readonly string[] Feet = { "j_asi_d_l", "j_asi_d_r", "j_asi_e_l", "j_asi_e_r" };

	/// <summary>How far to move out of the wall (along its normal) so the deepest bone just touches:
	/// distances = each bone's distance in front of the wall, gaps = how far in front it must stay.
	/// Bones far from the wall never decide it (the closest one always wins); positive = away from the wall.</summary>
	public static float WallPush(IReadOnlyList<(float Distance, float Gap)> bones) {
		var push = float.NegativeInfinity;
		foreach(var (distance, gap) in bones) if(float.IsFinite(distance)) push = MathF.Max(push, gap - distance);
		return float.IsFinite(push) ? push : 0;
	}

	/// <summary>Height change so no foot bone is in the floor, then the shoes' heel height on top.
	/// clearances = each bone's height over the floor minus its rest-pose height (0 = touching).</summary>
	public static float? FloorLift(IReadOnlyList<float> clearances, float heel) {
		var lowest = float.PositiveInfinity;
		foreach(var c in clearances) if(float.IsFinite(c)) lowest = MathF.Min(lowest, c);
		return float.IsFinite(lowest) ? heel - lowest : null;
	}
	/// <summary>Bones that show which side of the wall the body is on (knees, hips, head stick out away from it).</summary>
	public static readonly string[] BodyBulk = { "j_kosi", "j_asi_b_l", "j_asi_b_r", "j_kao" };

	public static bool SameKey(string? a, string? b) =>
		string.Equals((a ?? string.Empty).Trim().Trim('/'), (b ?? string.Empty).Trim().Trim('/'), StringComparison.OrdinalIgnoreCase);

	public static SurfaceProfile? Find(IEnumerable<SurfaceProfile> profiles, string? modFolder, string? animationKey) =>
		string.IsNullOrWhiteSpace(animationKey) ? null :
		// a profile naming a mod beats one that applies to any mod
		profiles.Where(p => p.Matches(modFolder, animationKey)).OrderByDescending(p => p.Mod.Trim().Length).FirstOrDefault();

	/// <summary>The skeleton code a human's animations are filed under (c0101 Midlander male ... c1801 Viera female).</summary>
	public static string? RaceCode(byte race, byte sex, byte tribe) {
		int? baseId = race switch {
			1 => tribe == 2 ? 3 : 1,   // Hyur: Midlander / Highlander
			2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17,
			_ => null,
		};
		if(baseId is not { } id || sex > 1) return null;
		return $"c{id + sex:00}01";
	}

	public static string AnimationPath(string raceCode, string key) =>
		$"chara/human/{raceCode}/animation/a0001/bt_common/{key.Trim().Trim('/')}.pap";

	/// <summary>The Penumbra mod folder a resolved file lives in, or null for a game file.</summary>
	public static string? ModFolder(string? resolvedPath, string? modDirectory) {
		if(string.IsNullOrWhiteSpace(resolvedPath)) return null;
		var path = resolvedPath;
		var bar = path.LastIndexOf('|');
		if(bar >= 0) path = path[(bar + 1)..];
		path = path.Replace('/', '\\');
		if(path.Length < 3 || path[1] != ':' && !path.StartsWith("\\\\", StringComparison.Ordinal)) return null;   // not a file on disk
		var root = (modDirectory ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
		if(root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) {
			var rest = path[(root.Length + 1)..];
			var slash = rest.IndexOf('\\');
			return slash > 0 ? rest[..slash] : null;
		}
		return null;   // Penumbra's mod folder unknown: can't tell which mod
	}

	/// <summary>Direction (on the ground plane, unit) that the animation's wall faces, i.e. from the wall towards the body.
	/// Fits a line through the contact points; the body's bulk (hips, knees, head) says which side is "out".</summary>
	public static Vector2 FitWallNormal(IReadOnlyList<Vector2> contacts, Vector2 bulk, Vector2 fallback, out float spread) {
		spread = 0;
		if(contacts.Count == 0) return Normalize(fallback);
		var mean = Vector2.Zero;
		foreach(var p in contacts) mean += p;
		mean /= contacts.Count;
		float xx = 0, xz = 0, zz = 0;
		foreach(var p in contacts) {
			var d = p - mean;
			xx += d.X * d.X; xz += d.X * d.Y; zz += d.Y * d.Y;
		}
		// principal axis of the points = along the wall
		var angle = 0.5f * MathF.Atan2(2 * xz, xx - zz);
		var along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		foreach(var p in contacts) spread = MathF.Max(spread, MathF.Abs(Vector2.Dot(p - mean, along)));
		Vector2 normal;
		if(spread < 0.08f) normal = Normalize(fallback);   // points too close together to tell the wall's direction
		else {
			normal = new Vector2(-along.Y, along.X);
			if(Vector2.Dot(normal, fallback) < 0) normal = -normal;
		}
		// the body sticks out on the open side of the wall
		var side = Vector2.Dot(bulk - mean, normal);
		if(MathF.Abs(side) > 0.03f && side < 0) normal = -normal;
		return normal;
	}

	public static Vector2 Normalize(Vector2 v) => v.LengthSquared() > 1e-10f ? Vector2.Normalize(v) : new Vector2(0, 1);

	/// <summary>One wall from raycast hits: the nearest vertical hit and the hits on the same surface around it.
	/// Returns a point on the wall and its outward normal (horizontal, towards where the rays came from).</summary>
	public static bool PickWall(IReadOnlyList<(Vector3 Point, Vector3 Normal, float Distance)> hits, out Vector3 point, out Vector2 normal) {
		point = default;
		normal = default;
		var vertical = hits.Where(h => float.IsFinite(h.Distance) && MathF.Abs(h.Normal.Y) < 0.5f &&
			new Vector2(h.Normal.X, h.Normal.Z).LengthSquared() > 0.01f).OrderBy(h => h.Distance).ToList();
		if(vertical.Count == 0) return false;
		var first = vertical[0];
		var firstNormal = Normalize(new Vector2(first.Normal.X, first.Normal.Z));
		var same = vertical.Where(h => h.Distance - first.Distance < 0.35f &&
			Vector2.Dot(Normalize(new Vector2(h.Normal.X, h.Normal.Z)), firstNormal) > 0.85f).ToList();
		var sum = Vector2.Zero;
		var at = Vector3.Zero;
		foreach(var h in same) { sum += Normalize(new Vector2(h.Normal.X, h.Normal.Z)); at += h.Point; }
		normal = Normalize(sum);
		point = at / same.Count;
		return true;
	}

	/// <summary>The wall most hits agree on. Every vertical hit proposes a plane; its support = hits on the same plane
	/// (within 4 cm, facing within ~25°), minus a little for distance from where you stand, plus a little when it's the
	/// side the pose expects. The winner is refined from its supporting hits and faces where you stand.</summary>
	public static bool FitWallPlane(IReadOnlyList<(Vector3 Point, Vector3 Normal, float Distance)> hits, Vector2 stand, Vector2 poseOut,
		out Vector3 point, out Vector2 normal, out int support) {
		point = default; normal = default; support = 0;
		var vertical = hits.Where(h => float.IsFinite(h.Point.X) && MathF.Abs(h.Normal.Y) < 0.5f &&
			new Vector2(h.Normal.X, h.Normal.Z).LengthSquared() > 0.01f).ToList();
		if(vertical.Count == 0) return false;
		var best = float.NegativeInfinity;
		Vector2 bestNormal = default;
		Vector3 bestPoint = default;
		foreach(var candidate in vertical) {
			var cn = Normalize(new Vector2(candidate.Normal.X, candidate.Normal.Z));
			var c3 = new Vector3(cn.X, 0, cn.Y);
			var count = 0;
			foreach(var h in vertical) {
				var hn = Normalize(new Vector2(h.Normal.X, h.Normal.Z));
				if(MathF.Abs(Vector2.Dot(hn, cn)) > 0.9f && MathF.Abs(Vector3.Dot(h.Point - candidate.Point, c3)) < 0.04f) count++;
			}
			var away = MathF.Abs(Vector2.Dot(stand - new Vector2(candidate.Point.X, candidate.Point.Z), cn));
			var facing = new Vector2(stand.X - candidate.Point.X, stand.Y - candidate.Point.Z);
			var outward = Vector2.Dot(facing, cn) >= 0 ? cn : -cn;
			var score = count - away * 3 + (Vector2.Dot(outward, Normalize(poseOut)) > 0.5f ? 2 : 0);
			if(score > best) { best = score; bestNormal = outward; bestPoint = candidate.Point; }
		}
		// refine from the supporting hits
		var b3 = new Vector3(bestNormal.X, 0, bestNormal.Y);
		var used = vertical.Where(h => MathF.Abs(Vector2.Dot(Normalize(new Vector2(h.Normal.X, h.Normal.Z)), bestNormal)) > 0.9f &&
			MathF.Abs(Vector3.Dot(h.Point - bestPoint, b3)) < 0.04f).ToList();
		support = used.Count;
		var sum = Vector2.Zero;
		var at = Vector3.Zero;
		foreach(var h in used) {
			var hn = Normalize(new Vector2(h.Normal.X, h.Normal.Z));
			sum += Vector2.Dot(hn, bestNormal) >= 0 ? hn : -hn;
			at += h.Point;
		}
		normal = Normalize(sum);
		point = at / used.Count;
		// face where you stand
		if(Vector2.Dot(stand - new Vector2(point.X, point.Z), normal) < 0) normal = -normal;
		return true;
	}

	/// <summary>Triangle normal facing back along the ray.</summary>
	public static Vector3 FacingNormal(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 rayDirection, Vector3 fallback) {
		var n = Vector3.Cross(v2 - v1, v3 - v1);
		if(n.LengthSquared() < 1e-12f || !float.IsFinite(n.X)) n = fallback;
		if(n.LengthSquared() < 1e-12f) return Vector3.Zero;
		n = Vector3.Normalize(n);
		return Vector3.Dot(n, rayDirection) > 0 ? -n : n;
	}

	/// <summary>The heel height of the first enabled SimpleHeels preset for this character's FEET slot that matches the
	/// shoes worn (model id, or the model's file path in path mode). Null when SimpleHeels has none for these shoes.</summary>
	public static float? ShoePreset(string? simpleHeelsJson, string name, uint world, ushort feetModelId, string? feetPath) {
		if(string.IsNullOrWhiteSpace(simpleHeelsJson)) return null;
		try {
			using var doc = System.Text.Json.JsonDocument.Parse(simpleHeelsJson);
			if(!doc.RootElement.TryGetProperty("WorldCharacterDictionary", out var worlds) ||
				!worlds.TryGetProperty(world.ToString(System.Globalization.CultureInfo.InvariantCulture), out var characters) ||
				!characters.TryGetProperty(name, out var character)) return null;
			if(character.TryGetProperty("Enabled", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.False) return null;
			if(!character.TryGetProperty("HeelsConfig", out var presets)) return null;
			if(presets.ValueKind == System.Text.Json.JsonValueKind.Object && presets.TryGetProperty("$values", out var values)) presets = values;
			if(presets.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
			var path = CleanModelPath(feetPath);
			foreach(var preset in presets.EnumerateArray()) {
				if(!preset.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind != System.Text.Json.JsonValueKind.True) continue;
				if(!IsFeetSlot(preset)) continue;
				var pathMode = preset.TryGetProperty("PathMode", out var pm) && pm.ValueKind == System.Text.Json.JsonValueKind.True;
				bool matches;
				if(pathMode) {
					var want = preset.TryGetProperty("Path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() : null;
					matches = path != null && want != null && string.Equals(CleanModelPath(want), path, StringComparison.OrdinalIgnoreCase);
				} else {
					matches = preset.TryGetProperty("ModelId", out var id) && id.TryGetInt32(out var model) && model == feetModelId;
				}
				if(!matches) continue;
				if(preset.TryGetProperty("Offset", out var offset) && offset.TryGetSingle(out var height) && float.IsFinite(height)) return Math.Clamp(height, -1f, 1f);
			}
		} catch(Exception) { }
		return null;
	}

	private static bool IsFeetSlot(System.Text.Json.JsonElement preset) {
		if(!preset.TryGetProperty("Slot", out var slot)) return true;   // Feet is SimpleHeels' default
		return slot.ValueKind switch {
			System.Text.Json.JsonValueKind.Number => slot.TryGetInt32(out var n) && n == 4,
			System.Text.Json.JsonValueKind.String => string.Equals(slot.GetString(), "Feet", StringComparison.OrdinalIgnoreCase),
			_ => false,
		};
	}

	public static string? CleanPath(string? path) => CleanModelPath(path);

	private static string? CleanModelPath(string? path) {
		if(string.IsNullOrWhiteSpace(path)) return null;
		var bar = path.LastIndexOf('|');
		return (bar >= 0 ? path[(bar + 1)..] : path).Replace('\\', '/').Trim();
	}

	/// <summary>A shoe model's own heel height ("heels_offset=0.05", or SimpleHeels' encoded "heels_offset_a_bc" form).</summary>
	public static float? ModelHeelAttribute(string? attribute) {
		if(string.IsNullOrEmpty(attribute)) return null;
		if(attribute.StartsWith("heels_offset=", StringComparison.OrdinalIgnoreCase))
			return float.TryParse(attribute[13..].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
		if(attribute.StartsWith("heels_offset_", StringComparison.OrdinalIgnoreCase)) {
			var text = attribute[13..].Replace("n_", "-").Replace('a', '0').Replace('b', '1').Replace('c', '2').Replace('d', '3').Replace('e', '4')
				.Replace('f', '5').Replace('g', '6').Replace('h', '7').Replace('i', '8').Replace('j', '9').Replace('_', '.');
			return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
		}
		return null;
	}

	/// <summary>Yaw (radians, game convention atan2(x, z)) of a ground-plane direction.</summary>
	public static float Yaw(Vector2 xz) => MathF.Atan2(xz.X, xz.Y);
}

/// <summary>The floor as a plane y = A·x + B·z + C (fitted like the wall: many hits, one surface).</summary>
internal readonly record struct FloorPlane(float A, float B, float C) {
	public float HeightAt(float x, float z) => A * x + B * z + C;

	/// <summary>Keeps the hits on the same surface as the floor under the feet (within tolerance of their median
	/// height), then fits a plane through them; too few / degenerate = flat at that height.</summary>
	public static FloorPlane? Fit(IReadOnlyList<Vector3> hits, IReadOnlyList<float> underFeet, out List<Vector3> used, float tolerance = 0.12f) {
		used = new List<Vector3>();
		var heights = (underFeet.Count > 0 ? underFeet : hits.Select(h => h.Y).ToList()).Where(float.IsFinite).OrderBy(y => y).ToList();
		if(heights.Count == 0) return null;
		var base_ = heights[heights.Count / 2];
		used = hits.Where(h => float.IsFinite(h.Y) && MathF.Abs(h.Y - base_) <= tolerance).ToList();
		if(used.Count < 3) return new FloorPlane(0, 0, base_);
		// least squares on x, z (centred for stability)
		var mx = used.Average(p => p.X); var mz = used.Average(p => p.Z); var my = used.Average(p => p.Y);
		double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
		foreach(var p in used) {
			double dx = p.X - mx, dz = p.Z - mz, dy = p.Y - my;
			sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxy += dx * dy; szy += dz * dy;
		}
		var det = sxx * szz - sxz * sxz;
		if(Math.Abs(det) < 1e-6) return new FloorPlane(0, 0, my);
		var a = (float)((sxy * szz - szy * sxz) / det);
		var b = (float)((szy * sxx - sxy * sxz) / det);
		// a floor steeper than ~30 degrees isn't one to stand on: treat as flat
		if(MathF.Sqrt(a * a + b * b) > 0.58f) return new FloorPlane(0, 0, my);
		return new FloorPlane(a, b, my - a * mx - b * mz);
	}
}

/// <summary>One thing detected around the character: a front face up to a top edge, with what's on top.</summary>
internal sealed record SurfaceShape(Vector3 EdgeA, Vector3 EdgeB, Vector2 Inward, float Height, float Depth, float Flatness, float Clearance, string Kind);

internal static class ShapeRules {
	/// <summary>Standing head-bone height of the character size for whom a seat is ~0.45-0.55 tall
	/// (calibrated on a small character; "Size adjustment" in the tab scales it).</summary>
	public const float ReferenceHead = 1.0f;

	/// <summary>How much smaller/bigger this character is than average (standing head height), so the ranges below fit them.</summary>
	public static float SizeScale(float head) => float.IsFinite(head) && head > 0.2f ? Math.Clamp(head / ReferenceHead, 0.35f, 1.6f) : 1;

	/// <summary>The seat height range for a character of this size.</summary>
	public static (float Low, float High) SeatRange(float scale) => (0.38f * scale, 0.62f * scale);

	/// <summary>What a top is, by its height for this character's size (scale 1 = average: seats ~0.45-0.55,
	/// tables ~0.75, counters ~0.9-1.1; a Lalafell's are lower, a Roegadyn's higher).</summary>
	public static string Kind(float height, float flatness, float depth, float scale = 1) {
		var h = height / (scale > 0.1f ? scale : 1);
		if(h > 1.8f) return "wall";
		if(flatness < 0.7f || depth < 0.12f) return h < 0.62f ? "edge" : "ledge";
		if(h < 0.3f) return "step";
		if(h < 0.38f) return "low seat";
		if(h < 0.62f) return "seat";
		if(h < 0.85f) return "table";
		if(h < 1.2f) return "counter";
		return "shelf / high ledge";
	}

	/// <summary>Side hits in neighbouring ring directions that found the same top (height within 5 cm, faces alike,
	/// edges close) are one object; a direction can hold several (a bench, a table behind it). Wraps around the ring.</summary>
	public static List<List<int>> Group(IReadOnlyList<(int Dir, Vector3 Edge, Vector2 Inward, float Height)> found, int ringSize) {
		var parent = Enumerable.Range(0, found.Count).ToArray();
		int Root(int i) { while(parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
		for(var a = 0; a < found.Count; a++)
			for(var b = a + 1; b < found.Count; b++) {
				var step = (found[b].Dir - found[a].Dir + ringSize) % ringSize;
				if(step != 1 && step != ringSize - 1) continue;
				if(MathF.Abs(found[a].Height - found[b].Height) >= 0.05f || Vector2.Dot(found[a].Inward, found[b].Inward) <= 0.7f ||
					Vector3.Distance(found[a].Edge, found[b].Edge) >= 0.6f) continue;
				parent[Root(a)] = Root(b);
			}
		return Enumerable.Range(0, found.Count).GroupBy(Root).Select(g => g.ToList()).ToList();
	}

	/// <summary>Top-down grid (size×size, NaN = no top): neighbouring cells within tolerance of each other's height form
	/// one region; regions smaller than minCells (candles, cups) are dropped.</summary>
	public static List<List<int>> GridRegions(IReadOnlyList<float> heights, int size, float tolerance, int minCells) {
		var regions = new List<List<int>>();
		var seen = new bool[heights.Count];
		var queue = new Queue<int>();
		for(var start = 0; start < heights.Count; start++) {
			if(seen[start] || float.IsNaN(heights[start])) continue;
			var region = new List<int>();
			seen[start] = true;
			queue.Enqueue(start);
			while(queue.Count > 0) {
				var c = queue.Dequeue();
				region.Add(c);
				int x = c % size, z = c / size;
				foreach(var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) }) {
					if(nx < 0 || nz < 0 || nx >= size || nz >= size) continue;
					var n = nz * size + nx;
					if(seen[n] || float.IsNaN(heights[n]) || MathF.Abs(heights[n] - heights[c]) >= tolerance) continue;
					seen[n] = true;
					queue.Enqueue(n);
				}
			}
			if(region.Count >= minCells) regions.Add(region);
		}
		return regions;
	}
}

/// <summary>Fitting a pose to something in the world without a profile for it: where the pose bends, which edge to use.</summary>
internal static class SnapRules {
	/// <summary>Things you can bend over.</summary>
	public static bool IsTable(string kind) => kind is "table" or "counter" || kind.StartsWith("ledge / rail (table", StringComparison.Ordinal) ||
		kind.StartsWith("ledge / rail (counter", StringComparison.Ordinal);

	/// <summary>Snapped to something found nearby (a table, a pole) rather than lined up with a wall or the floor.</summary>
	public static bool IsSnap(SurfaceKind kind) => kind is SurfaceKind.Table or SurfaceKind.Pole or SurfaceKind.Low;

	/// <summary>Bent over or lying on something's top: a table, or something lower like a bed.</summary>
	public static bool IsTop(SurfaceKind kind) => kind is SurfaceKind.Table or SurfaceKind.Low;

	/// <summary>How much a hand looks like it's holding an upright pole. How the hand is turned counts most: the row of
	/// knuckles upright (the pole runs along it) and the palm facing sideways, not up or down. Curled fingers
	/// (curl: 0 = straight, 1 = a right angle or more at the knuckle) add to that, but an open hand turned the right
	/// way still beats a curled one lying palm-up.</summary>
	public static float GripScore(Vector3 wrist, Vector3 knuckle, Vector3 joint, Vector3 index, Vector3 pinky, out float curl) {
		curl = 0;
		Vector3 palm = knuckle - wrist, finger = joint - knuckle;
		if(palm.Length() < 1e-4f) return 0;
		if(finger.Length() >= 1e-4f) {
			var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(palm), Vector3.Normalize(finger)), -1, 1));
			curl = Math.Clamp(angle / (MathF.PI / 2), 0, 1);
		}
		var row = index - pinky;
		var upright = row.Length() < 1e-4f ? 0.5f : MathF.Abs(Vector3.Normalize(row).Y);
		var normal = Vector3.Cross(palm, row);
		var sideways = normal.Length() < 1e-7f ? 0.5f : 1 - MathF.Abs(Vector3.Normalize(normal).Y);
		return (0.25f + 0.75f * upright) * (0.3f + 0.7f * sideways) * (0.5f + 0.5f * curl);
	}

	/// <summary>Where the thing a hand holds runs through it: in front of the palm, on the side the fingers curl to.</summary>
	public static Vector3 GripCentre(Vector3 wrist, Vector3 knuckle, Vector3 joint) {
		var palm = knuckle - wrist;
		var centre = Vector3.Lerp(wrist, knuckle, 0.75f);
		if(palm.Length() < 1e-4f) return centre;
		var along = Vector3.Normalize(palm);
		var finger = joint - knuckle;
		var side = finger - along * Vector3.Dot(finger, along);
		return side.Length() < 0.005f ? centre : centre + Vector3.Normalize(side) * 0.03f;
	}

	/// <summary>A point turned about an upright line through pivot; a positive angle adds to the yaw.</summary>
	public static Vector3 TurnAbout(Vector3 point, Vector3 pivot, float angle) {
		float x = point.X - pivot.X, z = point.Z - pivot.Z, c = MathF.Cos(angle), s = MathF.Sin(angle);
		return new Vector3(pivot.X + x * c + z * s, point.Y, pivot.Z - x * s + z * c);
	}

	/// <summary>Of the turns tried (evenly spaced round the circle, the first being no turn), the one with the least
	/// in the way; between equals, the smallest turn. Returns the turn in radians, -pi..pi.</summary>
	public static float BestTurn(IReadOnlyList<float> clips) {
		if(clips.Count == 0) return 0;
		var best = 0;
		float Turn(int i) { var a = i * MathF.Tau / clips.Count; return a > MathF.PI ? a - MathF.Tau : a; }
		for(var i = 1; i < clips.Count; i++)
			if(clips[i] < clips[best] - 0.01f || (MathF.Abs(clips[i] - clips[best]) <= 0.01f && MathF.Abs(Turn(i)) < MathF.Abs(Turn(best)))) best = i;
		return Turn(best);
	}

	/// <summary>Which side of a pose goes against a wall, read from the pose itself. True = the front (hands on the
	/// wall), false = the back. A pose that leans says it outright: forwards onto the wall, or back against it.
	/// An upright one goes by its hands: held out well in front of the chest means the front.</summary>
	public static bool WallFront(Vector2 facing, Vector3 hips, Vector3 chest, Vector3 head, IReadOnlyList<Vector3> hands) {
		if(BendDirection(hips, chest, head) is { } lean) return Vector2.Dot(lean, facing) > 0;
		if(hands.Count == 0) return false;
		var reach = 0f;
		foreach(var hand in hands) reach += Vector2.Dot(new Vector2(hand.X - chest.X, hand.Z - chest.Z), facing) / hands.Count;
		return reach > 0.2f;
	}

	/// <summary>Whether something is the right height to bend over or lie on: no lower than the middle of your thigh,
	/// no taller than your head (both as you stand).</summary>
	public static bool TableHeightFits(float height, float midThigh, float head) => height >= midThigh - 0.025f && height <= head + 0.025f;

	/// <summary>Second choice when there's no table: anything with a usable top that isn't just a step or a floor.</summary>
	public static bool IsLeanable(string kind) => IsTable(kind) || kind is "seat" or "low seat" or "shelf / high ledge" ||
		kind.StartsWith("ledge / rail (", StringComparison.Ordinal);

	/// <summary>Which way the pose leans its upper body (on the ground plane), or null when it stands upright.</summary>
	public static Vector2? BendDirection(Vector3 hips, Vector3 chest, Vector3 head) {
		var lean = new Vector2(chest.X - hips.X, chest.Z - hips.Z) + new Vector2(head.X - hips.X, head.Z - hips.Z) * 0.5f;
		return lean.Length() < 0.12f ? null : Vector2.Normalize(lean);
	}

	/// <summary>The point on an object's outline nearest to you, and the direction from there into the object:
	/// square to the edge as it runs over a good stretch either side (up to about 0.45 each way), so a round table sends
	/// you towards its middle and one jagged step in the outline doesn't turn you.</summary>
	public static bool NearestEdge(IReadOnlyList<IReadOnlyList<Vector3>> outlines, Vector2 from, Vector2 middle, out Vector3 point, out Vector2 inward, out float distance) {
		point = default; inward = default; distance = float.PositiveInfinity;
		IReadOnlyList<Vector3>? best = null;
		var bestAt = 0;
		foreach(var loop in outlines) {
			if(loop.Count < 2) continue;
			for(var i = 0; i < loop.Count; i++) {
				var a = loop[i];
				var b = loop[(i + 1) % loop.Count];
				var a2 = new Vector2(a.X, a.Z);
				var ab = new Vector2(b.X, b.Z) - a2;
				var t = ab.LengthSquared() > 1e-10f ? Math.Clamp(Vector2.Dot(from - a2, ab) / ab.LengthSquared(), 0, 1) : 0;
				var d = Vector2.Distance(from, a2 + ab * t);
				if(d >= distance) continue;
				distance = d;
				point = Vector3.Lerp(a, b, t);
				best = loop;
				bestAt = i;
			}
		}
		if(best == null) return false;
		var around = 0f;
		for(var i = 0; i < best.Count; i++) around += Flat(best[i], best[(i + 1) % best.Count]);
		var reach = MathF.Min(0.45f, around / 5);
		var here = new Vector2(point.X, point.Z);
		// three stretches, short to long, added up: the jags in the outline average out
		var along = Vector2.Zero;
		for(var part = 1; part <= 3; part++)
			along += Walk(best, here, (bestAt + 1) % best.Count, 1, reach * part / 3) - Walk(best, here, bestAt, -1, reach * part / 3);
		if(along.LengthSquared() < 1e-10f) along = new Vector2(best[(bestAt + 1) % best.Count].X - best[bestAt].X, best[(bestAt + 1) % best.Count].Z - best[bestAt].Z);
		var across = SurfaceRules.Normalize(new Vector2(-along.Y, along.X));
		inward = Vector2.Dot(across, middle - here) >= 0 ? across : -across;
		return true;
	}

	private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

	/// <summary>Where you get to going a given distance along an outline, from a point on it, towards corner "next" and on.</summary>
	private static Vector2 Walk(IReadOnlyList<Vector3> loop, Vector2 from, int next, int step, float far) {
		var at = from;
		for(var n = 0; n < loop.Count && far > 0; n++) {
			var to = new Vector2(loop[next].X, loop[next].Z);
			var leg = Vector2.Distance(at, to);
			if(leg >= far) return leg > 1e-9f ? at + (to - at) * (far / leg) : at;
			far -= leg;
			at = to;
			next = (next + step + loop.Count) % loop.Count;
		}
		return at;
	}

	/// <summary>How good a choice something is to bend over: how far its edge is, and worse the more it is beside or
	/// behind you than in front (lower is better).</summary>
	public static float PickCost(float distance, Vector2 facing, Vector2 toEdge) {
		var ahead = toEdge.Length() < 0.05f ? 1 : Vector2.Dot(Vector2.Normalize(toEdge), facing);
		return distance + 0.6f * (1 - ahead);
	}

	/// <summary>How far to slide towards the table. Leg and hip points still level with the table's top or below it
	/// (once the body is lifted) meet its edge, a body's thickness short of it. With none - the pose lies on the table,
	/// legs in the air - the hips sit just on the edge, so the bottom hangs slightly off.</summary>
	public static float EdgeSlide(float top, float lift, float thickness, float hipsPast, IReadOnlyList<(float Past, float Y, string What)> points, out string what, out float front) {
		front = float.NegativeInfinity;
		what = string.Empty;
		foreach(var point in points) {
			if(point.Y + lift > top + 0.05f || point.Past <= front) continue;
			front = point.Past;
			what = point.What;
		}
		if(float.IsFinite(front)) return -thickness - front;
		front = hipsPast;
		what = "hips on the edge (lying on the table)";
		return LyingHipsPast - hipsPast;
	}

	/// <summary>Lying on a table, the hip bone ends up this far in from its edge.</summary>
	public const float LyingHipsPast = 0.03f;

	public static float TableLift(float top, IReadOnlyList<(float Y, float Gap, bool Torso, float Past)> parts) {
		var torso = float.PositiveInfinity;
		foreach(var part in parts) if(part.Torso) torso = MathF.Min(torso, part.Y - part.Gap);
		var lift = float.IsFinite(torso) ? top - torso : 0;
		foreach(var part in parts) if(!part.Torso && part.Past > 0.03f) lift = MathF.Max(lift, top - (part.Y - part.Gap));
		return lift;
	}
}
