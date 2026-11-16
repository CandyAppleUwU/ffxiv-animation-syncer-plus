using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AnimSync;

/// <summary>A layered height map of the area: per grid point, the flat surfaces found from above (highest first).
/// A node is cell * MaxLayers + layer.</summary>
internal sealed class AreaGrid {
	public const int MaxLayers = 6;
	public readonly int Size;
	public readonly float Step, X0, Z0;
	public readonly float[] Y;       // NaN = no surface in this slot
	public readonly float[] Flat;    // how level the surface is there (1 = level); below 0 = the UNDERSIDE of something

	public readonly float[] Room;    // open space straight above the surface, when known (else infinite)

	public AreaGrid(int size, float step, float x0, float z0) {
		Size = size; Step = step; X0 = x0; Z0 = z0;
		Y = new float[size * size * MaxLayers];
		Flat = new float[Y.Length];
		Room = new float[Y.Length];
		Array.Fill(Y, float.NaN);
		Array.Fill(Room, float.PositiveInfinity);
	}

	public int Cells => Size * Size;
	public Vector2 Centre(int cell) => new(X0 + cell % Size * Step, Z0 + cell / Size * Step);

	public int CellAt(float x, float z) {
		var gx = (int)MathF.Round((x - X0) / Step);
		var gz = (int)MathF.Round((z - Z0) / Step);
		return gx < 0 || gz < 0 || gx >= Size || gz >= Size ? -1 : gz * Size + gx;
	}

	/// <summary>Adds the next (lower) surface of a cell; false when the cell is full.</summary>
	public bool Add(int cell, float y, float flat, float room = float.PositiveInfinity) {
		for(var layer = 0; layer < MaxLayers; layer++) {
			var node = cell * MaxLayers + layer;
			if(!float.IsNaN(Y[node])) continue;
			Y[node] = y;
			Flat[node] = flat;
			Room[node] = room;
			return true;
		}
		return false;
	}

	/// <summary>Models are drawn one-sided, so each face has a front. Which way round that is isn't known up front, but
	/// the first thing a ray from above meets is nearly always a front (a top): make that the positive side.</summary>
	public void OrientUp() {
		var vote = 0;
		for(var cell = 0; cell < Cells; cell++) {
			var node = cell * MaxLayers;
			if(!float.IsNaN(Y[node])) vote += MathF.Sign(Flat[node]);
		}
		if(vote >= 0) return;
		for(var i = 0; i < Flat.Length; i++) Flat[i] = -Flat[i];
	}

	public int LayerCount() => Y.Count(y => !float.IsNaN(y));
}

internal static class AreaRules {
	/// <summary>A surface counts when there's room over it: no other surface closer above than headroom
	/// (drops the undersides and inner shelves of furniture).</summary>
	public static bool Usable(AreaGrid grid, int node, float headroom) {
		if(float.IsNaN(grid.Y[node]) || grid.Flat[node] <= 0) return false;   // nothing there, or the underside of something
		var layer = node % AreaGrid.MaxLayers;
		return grid.Room[node] >= headroom && (layer == 0 || grid.Y[node - 1] - grid.Y[node] >= headroom);
	}

	/// <summary>How much of a surface has something else close over it (0..1): the foot of a table under its top,
	/// a shelf inside a cabinet. Nothing you could sit on or lean over.</summary>
	public static float Covered(AreaGrid grid, IReadOnlyList<int> nodes, float room) {
		if(nodes.Count == 0) return 0;
		var covered = 0;
		foreach(var node in nodes)
			if(grid.Room[node] < room || node % AreaGrid.MaxLayers > 0 && grid.Y[node - 1] - grid.Y[node] < room) covered++;
		return covered / (float)nodes.Count;
	}

	/// <summary>Usable surfaces of neighbouring points within tolerance of each other's height join into one surface
	/// (a floor with its rugs and gentle slopes, a table top, a bench seat).</summary>
	public static List<List<int>> Components(AreaGrid grid, float tolerance, float headroom) {
		var components = new List<List<int>>();
		var seen = new bool[grid.Y.Length];
		var queue = new Queue<int>();
		for(var start = 0; start < grid.Y.Length; start++) {
			if(seen[start] || !Usable(grid, start, headroom)) continue;
			var component = new List<int>();
			seen[start] = true;
			queue.Enqueue(start);
			while(queue.Count > 0) {
				var node = queue.Dequeue();
				component.Add(node);
				var cell = node / AreaGrid.MaxLayers;
				int x = cell % grid.Size, z = cell / grid.Size;
				for(var side = 0; side < 9; side++) {   // the 8 points around it (a narrow rail runs corner to corner)
					if(side == 4) continue;
					var nx = x + side % 3 - 1;
					var nz = z + side / 3 - 1;
					if(nx < 0 || nz < 0 || nx >= grid.Size || nz >= grid.Size) continue;
					var first = (nz * grid.Size + nx) * AreaGrid.MaxLayers;
					for(var layer = 0; layer < AreaGrid.MaxLayers; layer++) {
						var other = first + layer;
						if(seen[other] || !Usable(grid, other, headroom) || MathF.Abs(grid.Y[other] - grid.Y[node]) >= tolerance) continue;
						seen[other] = true;
						queue.Enqueue(other);
					}
				}
			}
			components.Add(component);
		}
		return components;
	}

	public static float MeanY(AreaGrid grid, IReadOnlyList<int> nodes) {
		double sum = 0;
		foreach(var node in nodes) sum += grid.Y[node];
		return nodes.Count == 0 ? float.NaN : (float)(sum / nodes.Count);
	}

	/// <summary>The floor: the biggest surface that isn't above what the character stands on (index into components, -1 = none).</summary>
	/// <remarks>What you stand on is the floor when it's big enough to be one (a raised stage, an upper level with
	/// the real floor somewhere below it) - otherwise it's the biggest surface that isn't above you.</remarks>
	public static int FloorIndex(IReadOnlyList<List<int>> components, AreaGrid grid, float maxY, int standing = -1, int floorCells = int.MaxValue) {
		if(standing >= 0 && standing < components.Count && components[standing].Count >= floorCells) return standing;
		var best = -1;
		for(var i = 0; i < components.Count; i++) {
			if(MeanY(grid, components[i]) > maxY) continue;
			if(best < 0 || components[i].Count > components[best].Count) best = i;
		}
		return best;
	}

	/// <summary>Everything you can walk onto from the floor: surfaces that touch it (or each other) with no more than a
	/// step's height between them - stairs, landings, raised floors. Small things (a footstool) stay objects.
	/// Returns the indexes of those surfaces, the floor included.</summary>
	public static HashSet<int> Ground(AreaGrid grid, IReadOnlyList<List<int>> components, int floorIndex, float stepMax, int minCells, int minContacts = 3, float room = 0) {
		var ground = new HashSet<int>();
		if(floorIndex < 0 || floorIndex >= components.Count) return ground;
		var owner = new int[grid.Y.Length];
		Array.Fill(owner, -1);
		for(var i = 0; i < components.Count; i++) foreach(var node in components[i]) owner[node] = i;
		// how many neighbouring point pairs join each two surfaces within a step's height
		var contacts = new Dictionary<(int, int), int>();
		for(var node = 0; node < owner.Length; node++) {
			var mine = owner[node];
			if(mine < 0) continue;
			var cell = node / AreaGrid.MaxLayers;
			int x = cell % grid.Size, z = cell / grid.Size;
			for(var side = 0; side < 2; side++) {   // right and down: each pair once
				int nx = x + (side == 0 ? 1 : 0), nz = z + (side == 1 ? 1 : 0);
				if(nx >= grid.Size || nz >= grid.Size) continue;
				var first = (nz * grid.Size + nx) * AreaGrid.MaxLayers;
				for(var layer = 0; layer < AreaGrid.MaxLayers; layer++) {
					var other = owner[first + layer];
					if(other < 0 || other == mine || MathF.Abs(grid.Y[first + layer] - grid.Y[node]) > stepMax) continue;
					var key = mine < other ? (mine, other) : (other, mine);
					contacts[key] = contacts.TryGetValue(key, out var count) ? count + 1 : 1;
				}
			}
		}
		ground.Add(floorIndex);
		var queue = new Queue<int>();
		queue.Enqueue(floorIndex);
		while(queue.Count > 0) {
			var current = queue.Dequeue();
			foreach(var (pair, count) in contacts) {
				if(count < minContacts) continue;
				var next = pair.Item1 == current ? pair.Item2 : pair.Item2 == current ? pair.Item1 : -1;
				if(next < 0 || components[next].Count < minCells || ground.Contains(next)) continue;
				// you can't walk onto something tucked under something else (a table's foot leading up to its top)
				if(room > 0 && Covered(grid, components[next], room) > 0.5f) continue;
				ground.Add(next);
				queue.Enqueue(next);
			}
		}
		return ground;
	}

	/// <summary>The surface under a point: the one closest below (or just above) height y at that grid point. -1 = none.</summary>
	public static int NodeUnder(AreaGrid grid, float x, float y, float z) {
		var cell = grid.CellAt(x, z);
		if(cell < 0) return -1;
		var best = -1;
		for(var layer = 0; layer < AreaGrid.MaxLayers; layer++) {
			var node = cell * AreaGrid.MaxLayers + layer;
			if(float.IsNaN(grid.Y[node]) || grid.Flat[node] <= 0 || grid.Y[node] > y + 0.3f) continue;
			if(best < 0 || MathF.Abs(grid.Y[node] - y) < MathF.Abs(grid.Y[best] - y)) best = node;
		}
		return best;
	}

	/// <summary>The borders of a set of grid cells as closed loops, in order. Each step is one cell side:
	/// the cell inside and the direction (dx, dz) to the cell outside. Longest loop first (the outer border).</summary>
	public static List<List<(int Cell, int Dx, int Dz)>> Outlines(HashSet<int> cells, int size) {
		// each open side is an edge between two cell corners, going clockwise around its cell
		var edges = new List<(int From, int To, int Cell, int Dx, int Dz)>();
		var corners = size + 1;
		foreach(var cell in cells) {
			int x = cell % size, z = cell / size;
			bool Out(int nx, int nz) => nx < 0 || nz < 0 || nx >= size || nz >= size || !cells.Contains(nz * size + nx);
			int Corner(int cx, int cz) => cz * corners + cx;
			if(Out(x, z - 1)) edges.Add((Corner(x, z), Corner(x + 1, z), cell, 0, -1));
			if(Out(x + 1, z)) edges.Add((Corner(x + 1, z), Corner(x + 1, z + 1), cell, 1, 0));
			if(Out(x, z + 1)) edges.Add((Corner(x + 1, z + 1), Corner(x, z + 1), cell, 0, 1));
			if(Out(x - 1, z)) edges.Add((Corner(x, z + 1), Corner(x, z), cell, -1, 0));
		}
		var from = new Dictionary<int, List<int>>();
		for(var i = 0; i < edges.Count; i++) {
			if(!from.TryGetValue(edges[i].From, out var list)) from[edges[i].From] = list = new List<int>();
			list.Add(i);
		}
		var used = new bool[edges.Count];
		var loops = new List<List<(int, int, int)>>();
		for(var start = 0; start < edges.Count; start++) {
			if(used[start]) continue;
			var loop = new List<(int, int, int)>();
			var current = start;
			while(current >= 0 && !used[current]) {
				used[current] = true;
				loop.Add((edges[current].Cell, edges[current].Dx, edges[current].Dz));
				var next = -1;
				if(from.TryGetValue(edges[current].To, out var options))
					foreach(var option in options) if(!used[option]) { next = option; break; }
				current = next;
			}
			loops.Add(loop);
		}
		loops.Sort((a, b) => b.Count.CompareTo(a.Count));
		return loops;
	}

	/// <summary>Fills the small gaps in a shape seen through a coarse grid (a narrow rail the grid points only
	/// partly land on): gaps of up to two points per reach between its pieces are bridged; its outside is left as it is.</summary>
	public static HashSet<int> Close(HashSet<int> cells, int size, int reach = 1) {
		var grown = new HashSet<int>(cells);
		foreach(var cell in cells) {
			int x = cell % size, z = cell / size;
			for(var dz = -reach; dz <= reach; dz++)
				for(var dx = -reach; dx <= reach; dx++) {
					int nx = x + dx, nz = z + dz;
					if(nx >= 0 && nz >= 0 && nx < size && nz < size) grown.Add(nz * size + nx);
				}
		}
		var closed = new HashSet<int>(cells);
		foreach(var cell in grown) {
			if(cells.Contains(cell)) continue;
			int x = cell % size, z = cell / size;
			var inside = true;
			for(var dz = -reach; dz <= reach && inside; dz++)
				for(var dx = -reach; dx <= reach && inside; dx++) {
					int nx = x + dx, nz = z + dz;
					inside = nx >= 0 && nz >= 0 && nx < size && nz < size && grown.Contains(nz * size + nx);
				}
			if(inside) closed.Add(cell);
		}
		return closed;
	}

	/// <summary>Rounds off the stair-steps of an outline traced along grid squares (a closed loop), then drops the
	/// points that add nothing (closer than tolerance to the line between their neighbours).</summary>
	public static List<Vector3> Smooth(IReadOnlyList<Vector3> loop, int passes, float tolerance) {
		var points = new List<Vector3>(loop);
		if(points.Count < 4) return points;
		for(var pass = 0; pass < passes; pass++) {
			var next = new List<Vector3>(points.Count);
			for(var i = 0; i < points.Count; i++) {
				var before = points[(i + points.Count - 1) % points.Count];
				var after = points[(i + 1) % points.Count];
				next.Add((before + points[i] * 2 + after) / 4);
			}
			points = next;
		}
		var kept = new List<Vector3> { points[0] };
		for(var i = 1; i < points.Count; i++) {
			var start = kept[^1];
			var to = points[(i + 1) % points.Count];
			var line = to - start;
			var length = line.Length();
			var off = length > 1e-6f ? Vector3.Cross(points[i] - start, line).Length() / length : Vector3.Distance(points[i], start);
			if(off >= tolerance || Vector3.Distance(points[i], start) > 1.5f) kept.Add(points[i]);
		}
		return kept.Count >= 3 ? kept : points;
	}

	/// <summary>How long and how wide a patch of points is along its own main direction (plus one grid step for the cells' size).</summary>
	public static (float Long, float Short) Extents(IReadOnlyList<Vector2> points, float step) {
		if(points.Count == 0) return (0, 0);
		var mean = Vector2.Zero;
		foreach(var p in points) mean += p;
		mean /= points.Count;
		float xx = 0, xz = 0, zz = 0;
		foreach(var p in points) {
			var d = p - mean;
			xx += d.X * d.X; xz += d.X * d.Y; zz += d.Y * d.Y;
		}
		var angle = 0.5f * MathF.Atan2(2 * xz, xx - zz);
		var along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
		var across = new Vector2(-along.Y, along.X);
		float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
		foreach(var p in points) {
			var a = Vector2.Dot(p - mean, along);
			var b = Vector2.Dot(p - mean, across);
			minA = MathF.Min(minA, a); maxA = MathF.Max(maxA, a);
			minB = MathF.Min(minB, b); maxB = MathF.Max(maxB, b);
		}
		var first = maxA - minA + step;
		var second = maxB - minB + step;
		return (MathF.Max(first, second), MathF.Min(first, second));
	}
}

/// <summary>The tightest rectangle around a patch of points: its middle, its long and short directions and half sizes.
/// Sides are numbered 0 = +Long end, 1 = -Long end, 2 = +Short side, 3 = -Short side.</summary>
internal readonly record struct AreaBox(Vector2 Centre, Vector2 Long, Vector2 Short, float HalfLong, float HalfShort) {
	public static AreaBox Fit(IReadOnlyList<Vector2> points, float pad) {
		if(points.Count == 0) return new AreaBox(Vector2.Zero, Vector2.UnitX, Vector2.UnitY, pad, pad);
		var stride = Math.Max(1, points.Count / 400);
		var best = float.MaxValue;
		var result = default(AreaBox);
		for(var degrees = 0; degrees < 90; degrees += 5) {
			var angle = degrees * MathF.PI / 180;
			var a = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
			var b = new Vector2(-a.Y, a.X);
			float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
			for(var i = 0; i < points.Count; i += stride) {
				var pa = Vector2.Dot(points[i], a);
				var pb = Vector2.Dot(points[i], b);
				minA = MathF.Min(minA, pa); maxA = MathF.Max(maxA, pa);
				minB = MathF.Min(minB, pb); maxB = MathF.Max(maxB, pb);
			}
			var area = (maxA - minA) * (maxB - minB);
			if(area >= best) continue;
			best = area;
			var centre = a * ((minA + maxA) / 2) + b * ((minB + maxB) / 2);
			float halfA = (maxA - minA) / 2 + pad, halfB = (maxB - minB) / 2 + pad;
			result = halfA >= halfB ? new AreaBox(centre, a, b, halfA, halfB) : new AreaBox(centre, b, -a, halfB, halfA);
		}
		return result;
	}

	/// <summary>Which side of the rectangle a point lies towards.</summary>
	public int SideOf(Vector2 point) {
		var local = point - Centre;
		var along = Vector2.Dot(local, Long) / MathF.Max(HalfLong, 1e-4f);
		var across = Vector2.Dot(local, Short) / MathF.Max(HalfShort, 1e-4f);
		return MathF.Abs(along) > MathF.Abs(across) ? (along > 0 ? 0 : 1) : (across > 0 ? 2 : 3);
	}

	public Vector2 Outward(int side) => side == 0 ? Long : side == 1 ? -Long : side == 2 ? Short : -Short;
	public float Reach(int side) => side < 2 ? HalfLong : HalfShort;                // from the middle out to that side
	public Vector2 AlongSide(int side) => side < 2 ? Short : Long;                  // the direction the side runs in
	public float HalfSide(int side) => side < 2 ? HalfShort : HalfLong;             // half that side's length
}

/// <summary>What a seat is, from what stands around it: which side is the backrest, which are armrests, where the front is.</summary>
internal sealed record SeatRead(string Type, int Back, int[] Arms, int Front, float Depth, float Width);

internal static class SeatRules {
	/// <summary>rise = how far each side stands up above the seat (the typical value along it), share = how much of that
	/// side is raised at all, nearest = the side closest to the character (the front of a seat with no back).</summary>
	public static SeatRead Read(IReadOnlyList<float> rise, IReadOnlyList<float> share, float longSide, float shortSide, int nearest, float scale) {
		var raised = new bool[4];
		for(var i = 0; i < 4; i++) raised[i] = share[i] >= 0.5f && rise[i] >= 0.08f * scale;
		// the backrest: the tallest raised side; a long side wins a close call
		var back = -1;
		var tallest = 0f;
		for(var i = 0; i < 4; i++) {
			var weight = rise[i] * (i >= 2 ? 1.15f : 1f);
			if(!raised[i] || rise[i] < 0.2f * scale || weight <= tallest) continue;
			tallest = weight;
			back = i;
		}
		var arms = new List<int>();
		int front;
		if(back >= 0) {
			for(var i = 0; i < 4; i++) if(raised[i] && i != back && i != (back ^ 1)) arms.Add(i);
			front = raised[back ^ 1] ? -1 : back ^ 1;
		} else {
			front = !raised[nearest] ? nearest : Array.FindIndex(raised, r => !r);
		}
		// across the seat from front to back, and from side to side
		var facing = back >= 0 ? back : front;
		var depth = facing < 0 || facing >= 2 ? shortSide : longSide;
		var width = facing < 0 || facing >= 2 ? longSide : shortSide;
		var type = back < 0 ? (longSide >= 1.0f * scale ? "bench" : "stool")
			: arms.Count >= 2 ? (width >= 1.2f * scale ? "couch" : "armchair")
			: arms.Count == 1 ? "seat with a back and one arm"
			: width >= 1.2f * scale ? "bench with a back" : "chair";
		return new SeatRead(type, back, arms.ToArray(), front, depth, width);
	}
}
