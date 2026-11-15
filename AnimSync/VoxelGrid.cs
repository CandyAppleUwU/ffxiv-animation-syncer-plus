using System;
using System.Numerics;

namespace AnimSync;

/// <summary>A solid 3D picture of an area: small cubes, each marked when any model triangle passes through it
/// (and which way that triangle faces), then flood-filled from where the character stands so open space is told
/// apart from the insides of things. Like firing rays from every direction at once.</summary>
internal sealed class VoxelGrid {
	public const byte Solid = 1;    // a triangle passes through
	public const byte Level = 2;    // ...one that is level enough to be a top or an underside
	public const byte Flat = 4;     // ...and close to truly level
	public const byte Side = 8;     // ...one that stands upright
	public const byte Free = 16;    // open space you could reach from where the character stands

	public readonly int SizeX, SizeY, SizeZ;
	public readonly float Cube;
	public Vector3 Origin { get; private set; }
	public readonly byte[] Cells;
	private int[]? stack;
	private int stackTop;

	public VoxelGrid(Vector3 origin, int sizeX, int sizeY, int sizeZ, float cube) {
		Origin = origin; SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ; Cube = cube;
		Cells = new byte[sizeX * sizeY * sizeZ];
	}

	/// <summary>Empties the grid for a new scan somewhere else (keeps its memory).</summary>
	public void Reset(Vector3 origin) {
		Origin = origin;
		Array.Clear(Cells);
		stackTop = 0;
	}

	public int Index(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;
	public bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ;
	public byte At(int x, int y, int z) => Inside(x, y, z) ? Cells[Index(x, y, z)] : (byte)0;
	public bool IsFree(int x, int y, int z) => (At(x, y, z) & Free) != 0;
	public Vector3 Centre(int x, int y, int z) => Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * Cube;

	private static float Part(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
	private int SizeOf(int axis) => axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ;

	/// <summary>Marks every cube a triangle passes through (a little generously, so surfaces have no pinholes).</summary>
	public void AddTriangle(Vector3 a, Vector3 b, Vector3 c) {
		a = (a - Origin) / Cube;
		b = (b - Origin) / Cube;
		c = (c - Origin) / Cube;
		var min = Vector3.Min(a, Vector3.Min(b, c));
		var max = Vector3.Max(a, Vector3.Max(b, c));
		if(max.X < 0 || max.Y < 0 || max.Z < 0 || min.X >= SizeX || min.Y >= SizeY || min.Z >= SizeZ) return;
		var n = Vector3.Cross(b - a, c - a);
		var length = n.Length();
		if(!(length > 1e-9f)) return;   // no area (also NaN)
		n /= length;
		var up = MathF.Abs(n.Y);
		var flags = (byte)(Solid | (up >= 0.5f ? Level : Side) | (up >= 0.9f ? Flat : 0));
		// work in the two directions the triangle is widest in; the third (w) follows from its plane
		float ax = MathF.Abs(n.X), az = MathF.Abs(n.Z);
		var axis = ax >= up && ax >= az ? 0 : up >= az ? 1 : 2;
		int ui = (axis + 1) % 3, vi = (axis + 2) % 3;
		float au = Part(a, ui), av = Part(a, vi), aw = Part(a, axis);
		float bu = Part(b, ui), bv = Part(b, vi);
		float cu = Part(c, ui), cv = Part(c, vi);
		float nu = Part(n, ui), nv = Part(n, vi), nw = Part(n, axis);
		var u0 = Math.Max(0, (int)MathF.Floor(Part(min, ui)));
		var u1 = Math.Min(SizeOf(ui) - 1, (int)MathF.Floor(Part(max, ui)));
		var v0 = Math.Max(0, (int)MathF.Floor(Part(min, vi)));
		var v1 = Math.Min(SizeOf(vi) - 1, (int)MathF.Floor(Part(max, vi)));
		var wLow = Math.Max(0, (int)MathF.Floor(Part(min, axis)));
		var wHigh = Math.Min(SizeOf(axis) - 1, (int)MathF.Floor(Part(max, axis)));
		if(u0 > u1 || v0 > v1 || wLow > wHigh) return;
		// signed distance to each edge (positive inside), whichever way round the triangle is
		var turn = (bu - au) * (cv - av) - (bv - av) * (cu - au) >= 0 ? 1f : -1f;
		float e0u = bu - au, e0v = bv - av, e1u = cu - bu, e1v = cv - bv, e2u = au - cu, e2v = av - cv;
		float l0 = MathF.Sqrt(e0u * e0u + e0v * e0v), l1 = MathF.Sqrt(e1u * e1u + e1v * e1v), l2 = MathF.Sqrt(e2u * e2u + e2v * e2v);
		float s0 = l0 > 1e-9f ? turn / l0 : 0, s1 = l1 > 1e-9f ? turn / l1 : 0, s2 = l2 > 1e-9f ? turn / l2 : 0;
		var slope = 0.5f * (MathF.Abs(nu) + MathF.Abs(nv)) / MathF.Abs(nw);   // how far w changes across one cube
		for(var v = v0; v <= v1; v++) {
			var pv = v + 0.5f;
			for(var u = u0; u <= u1; u++) {
				var pu = u + 0.5f;
				if((e0u * (pv - av) - e0v * (pu - au)) * s0 < -0.71f) continue;
				if((e1u * (pv - bv) - e1v * (pu - bu)) * s1 < -0.71f) continue;
				if((e2u * (pv - cv) - e2v * (pu - cu)) * s2 < -0.71f) continue;
				var w = aw - (nu * (pu - au) + nv * (pv - av)) / nw;
				var w0 = Math.Max(wLow, (int)MathF.Floor(w - slope));
				var w1 = Math.Min(wHigh, (int)MathF.Floor(w + slope));
				for(var k = w0; k <= w1; k++) {
					var index = axis == 0 ? Index(k, u, v) : axis == 1 ? Index(v, k, u) : Index(u, v, k);
					Cells[index] |= flags;
				}
			}
		}
	}

	/// <summary>Starts filling open space from a point (the nearest empty cube at or above it). False = none found.</summary>
	public bool BeginFlood(Vector3 from) {
		var g = (from - Origin) / Cube;
		var x = Math.Clamp((int)MathF.Floor(g.X), 0, SizeX - 1);
		var y = Math.Clamp((int)MathF.Floor(g.Y), 0, SizeY - 1);
		var z = Math.Clamp((int)MathF.Floor(g.Z), 0, SizeZ - 1);
		stack ??= new int[Cells.Length];
		stackTop = 0;
		for(var rise = 0; rise < 20 && y + rise < SizeY; rise++) {
			var index = Index(x, y + rise, z);
			if((Cells[index] & Solid) != 0) continue;
			Cells[index] |= Free;
			stack[stackTop++] = index;
			return true;
		}
		return false;
	}

	/// <summary>Spreads the open space a bit further (through the six faces of each cube). True = finished.</summary>
	public bool StepFlood(int budget) {
		if(stack == null) return true;
		var layer = SizeX * SizeZ;
		while(stackTop > 0 && budget-- > 0) {
			var index = stack[--stackTop];
			var x = index % SizeX;
			var z = index / SizeX % SizeZ;
			var y = index / layer;
			if(x > 0) Spread(index - 1);
			if(x < SizeX - 1) Spread(index + 1);
			if(z > 0) Spread(index - SizeX);
			if(z < SizeZ - 1) Spread(index + SizeX);
			if(y > 0) Spread(index - layer);
			if(y < SizeY - 1) Spread(index + layer);
		}
		return stackTop == 0;
	}

	private void Spread(int index) {
		if((Cells[index] & (Solid | Free)) != 0) return;
		Cells[index] |= Free;
		stack![stackTop++] = index;
	}
}
