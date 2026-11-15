using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimSync;

/// <summary>Triangles of one model in its own space, with a small bounding-volume tree for fast ray tests.</summary>
internal sealed class MeshBvh {
	private readonly Vector3[] vertices;
	private readonly int[] triangles;          // 3 vertex indices per triangle, reordered by the tree
	private readonly List<Node> nodes = new();
	public Vector3 Min { get; }
	public Vector3 Max { get; }
	public int TriangleCount => triangles.Length / 3;
	/// <summary>The model's points and triangles (three indexes each), in its own space.</summary>
	public Vector3[] Vertices => vertices;
	public int[] Triangles => triangles;
	/// <summary>Roughly how much memory this model's triangles take.</summary>
	public long Bytes => vertices.Length * 12L + triangles.Length * 4L + nodes.Count * 40L;

	private struct Node {
		public Vector3 Min, Max;
		public int Left, Right;     // child nodes (-1 = leaf)
		public int First, Count;    // leaf: triangle range
	}

	public MeshBvh(Vector3[] vertices, int[] indices) {
		this.vertices = vertices;
		var count = indices.Length / 3;
		var order = new int[count];
		var centres = new Vector3[count];
		for(var t = 0; t < count; t++) {
			order[t] = t;
			centres[t] = (vertices[indices[t * 3]] + vertices[indices[t * 3 + 1]] + vertices[indices[t * 3 + 2]]) / 3;
		}
		if(count > 0) Build(order, 0, count, indices, centres);
		triangles = new int[count * 3];
		for(var t = 0; t < count; t++) {
			triangles[t * 3] = indices[order[t] * 3];
			triangles[t * 3 + 1] = indices[order[t] * 3 + 1];
			triangles[t * 3 + 2] = indices[order[t] * 3 + 2];
		}
		Min = nodes.Count > 0 ? nodes[0].Min : Vector3.Zero;
		Max = nodes.Count > 0 ? nodes[0].Max : Vector3.Zero;
	}

	private int Build(int[] order, int first, int count, int[] indices, Vector3[] centres) {
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for(var i = first; i < first + count; i++) {
			var t = order[i];
			for(var k = 0; k < 3; k++) {
				var v = vertices[indices[t * 3 + k]];
				min = Vector3.Min(min, v);
				max = Vector3.Max(max, v);
			}
		}
		var index = nodes.Count;
		nodes.Add(new Node { Min = min, Max = max, Left = -1, Right = -1, First = first, Count = count });
		if(count <= 8) return index;
		// split on the longest axis at the median centre
		var size = max - min;
		var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
		Array.Sort(order, first, count, Comparer<int>.Create((a, b) => Axis(centres[a], axis).CompareTo(Axis(centres[b], axis))));
		var half = count / 2;
		var left = Build(order, first, half, indices, centres);
		var right = Build(order, first + half, count - half, indices, centres);
		var node = nodes[index];
		node.Left = left; node.Right = right; node.Count = 0;
		nodes[index] = node;
		return index;
	}

	private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	/// <summary>Nearest hit along origin + t·direction for t in [0, maxT] (direction need not be unit length).</summary>
	public bool Raycast(Vector3 origin, Vector3 direction, float maxT, out float hitT, out Vector3 normal) {
		hitT = maxT;
		normal = default;
		if(nodes.Count == 0) return false;
		var inverse = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
		var found = false;
		Span<int> stack = stackalloc int[128];
		var top = 0;
		stack[top++] = 0;
		while(top > 0) {
			var node = nodes[stack[--top]];
			if(!SlabHit(node.Min, node.Max, origin, inverse, hitT)) continue;
			if(node.Left < 0) {
				for(var t = node.First; t < node.First + node.Count; t++) {
					var a = vertices[triangles[t * 3]];
					var b = vertices[triangles[t * 3 + 1]];
					var c = vertices[triangles[t * 3 + 2]];
					if(Triangle(origin, direction, a, b, c, out var distance) && distance < hitT) {
						hitT = distance;
						normal = Vector3.Cross(b - a, c - a);
						found = true;
					}
				}
			} else if(top < stack.Length - 2) {
				stack[top++] = node.Left;
				stack[top++] = node.Right;
			}
		}
		return found;
	}

	private static bool SlabHit(Vector3 min, Vector3 max, Vector3 origin, Vector3 inverse, float maxT) {
		var t1 = (min - origin) * inverse;
		var t2 = (max - origin) * inverse;
		var near = Vector3.Min(t1, t2);
		var far = Vector3.Max(t1, t2);
		var enter = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0));
		var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, maxT));
		// NaN from 0·inf (ray parallel to and on a slab plane) counts as a hit
		return !(enter > exit);
	}

	/// <summary>Möller–Trumbore, both sides.</summary>
	public static bool Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t) {
		t = 0;
		var e1 = b - a;
		var e2 = c - a;
		var p = Vector3.Cross(direction, e2);
		var det = Vector3.Dot(e1, p);
		if(MathF.Abs(det) < 1e-12f) return false;
		var inv = 1 / det;
		var s = origin - a;
		var u = Vector3.Dot(s, p) * inv;
		if(u < 0 || u > 1) return false;
		var q = Vector3.Cross(s, e1);
		var v = Vector3.Dot(direction, q) * inv;
		if(v < 0 || u + v > 1) return false;
		t = Vector3.Dot(e2, q) * inv;
		return t >= 0;
	}
}

/// <summary>A placed copy of a model in the world.</summary>
internal readonly record struct MeshInstance(MeshBvh Mesh, Vector3 Position, Quaternion Rotation, Vector3 Scale, string Path) {
	/// <summary>Ray in world space against this instance: nearest hit point, its world normal (unnormalised side) and distance.</summary>
	public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal, out float distance) {
		point = default; normal = default; distance = maxDistance;
		if(Scale.X == 0 || Scale.Y == 0 || Scale.Z == 0) return false;
		var unrotate = Quaternion.Conjugate(Rotation);
		var unit = Vector3.Normalize(direction);
		// into model space: t stays the same parameter, so world distance = t (unit world direction)
		var localOrigin = Vector3.Transform(origin - Position, unrotate) / Scale;
		var localDirection = Vector3.Transform(unit, unrotate) / Scale;
		if(!Mesh.Raycast(localOrigin, localDirection, maxDistance, out var t, out var localNormal)) return false;
		distance = t;
		point = origin + unit * t;
		// normals take the inverse scale
		normal = Vector3.Transform(localNormal / Scale, Rotation);
		return normal.LengthSquared() > 1e-20f;
	}

	/// <summary>Does the world box of this instance come within reach of a point?</summary>
	public bool Near(Vector3 point, float reach) {
		var local = Vector3.Transform(point - Position, Quaternion.Conjugate(Rotation)) / Scale;
		var pad = new Vector3(reach) / Vector3.Abs(Scale);
		return local.X >= Mesh.Min.X - pad.X && local.X <= Mesh.Max.X + pad.X &&
			local.Y >= Mesh.Min.Y - pad.Y && local.Y <= Mesh.Max.Y + pad.Y &&
			local.Z >= Mesh.Min.Z - pad.Z && local.Z <= Mesh.Max.Z + pad.Z;
	}
}
