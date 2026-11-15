using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;

namespace AnimSync;

// Debug view only (for now): a map of the area around the character, built from the visible models.
// Rays go straight down on a grid and keep going through each surface, so every point knows its layers
// (table top, then floor). From that: the real floor (also when you stand on something), every object on it
// with its true outline, what it is by height, and where the walls are.
public sealed partial class AnimSync {
    private sealed record AreaObject(List<List<Vector3>> Outlines, Vector3 Centre, float Height, float Flatness, float Long, float Short,
        float Clearance, bool OpenBelow, string Kind, AreaSeat? Seat = null);
    private sealed record AreaSeat(SeatRead Read, AreaBox Box, float Y, float[] Rise);
    private sealed record AreaPole(Vector3 Bottom, float Height, float Thickness, bool GoesOn);
    private sealed record AreaMapView(List<List<Vector3>> FloorOutlines, List<AreaObject> Objects, List<(Vector3 A, Vector3 B)> Walls,
        List<(Vector3 Point, bool Floor, float Height)> Dots, List<AreaPole> Poles, string StandingOn, Vector3 Stand, nint Mover, int Points, int Layers,
        double BuildMs, int Models, int Signature);

    private volatile AreaMapView? areaMap;
    private float surfaceShapeHead;     // the character's standing head height (sizes the seat/table ranges)
    private float surfaceSizeScale = 1;
    private long areaScanAt;
    private IEnumerator<bool>? areaScan;
    private double areaScanMs;
    private VoxelGrid? areaVoxels;                // kept between scans (a few MB)
    private const float PoleThickest = 0.08f;    // a pole is this thick or thinner
    private const float AreaCube = 0.05f;        // the cubes of the solid picture
    private const int AreaHalf = 80;             // cubes each way from the middle: 80 x 0.05 = 4 yalms
    private const int AreaTall = 96;             // cubes up: 4.8 yalms...
    private const float AreaBelow = 1.5f;        // ...starting this far under your feet
    private const double ScanBudgetMs = 1.5;

    /// <summary>Every frame with the debug view on: advance the scan by ~1.5 ms of work (no stutter). A new scan only
    /// starts when there's a reason: no map yet, you moved half a yalm, or the scenery around you changed.</summary>
    private void StepSurfaceShapes() {
        // built for the debug view, or for a while after a command that needs it (/animsync table)
        if(!SurfaceDebugOn && Environment.TickCount64 > areaWantedUntil) { areaScan = null; areaMap = null; return; }
        if(SurfaceBusy) return;
        if(areaScan == null) {
            if(Environment.TickCount64 < areaScanAt || AlignMover() is not { } self) return;
            if(areaMap is { } map && map.Mover == self.Address && map.Signature == AreaSignature(self.Position) &&
                Vector2.Distance(new Vector2(map.Stand.X, map.Stand.Z), new Vector2(self.Position.X, self.Position.Z)) < 0.5f &&
                MathF.Abs(map.Stand.Y - self.Position.Y) < 0.25f) { areaScanAt = Environment.TickCount64 + 250; return; }
            areaScanMs = 0;
            areaScan = ScanArea(self).GetEnumerator();
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try {
            while(clock.Elapsed.TotalMilliseconds < ScanBudgetMs)
                if(!areaScan.MoveNext()) { areaScan = null; areaScanAt = Environment.TickCount64 + 500; break; }
        } catch(Exception ex) {
            Logger.Debug(ex, "Area scan failed.");
            areaScan = null;
            areaScanAt = Environment.TickCount64 + 2000;
        }
        areaScanMs += clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Changes when the scenery that could be in the map changes (models finishing loading, furniture moved in/out).</summary>
    private int AreaSignature(Vector3 stand) {
        RefreshVisualScene(new Vector3(stand.X, stand.Y + 1, stand.Z));
        return HashCode.Combine(visualPlaced.Count, visualModels.Count, VisualLoadingCount);
    }

    /// <summary>One scan, in small steps (each yield is a place it may pause until the next frame). Publishes areaMap at the end.</summary>
    private IEnumerable<bool> ScanArea(IGameObject self) {
        var stand = self.Position;
        var mover = self.Address;
        // the character's size from the STANDING rest pose (head bone x model scale): a sitting or bending pose
        // would make them look smaller than they are
        var head = (RestHeight(self, "j_kao") ?? float.NaN) * SkeletonScale(self);
        var scale = ShapeRules.SizeScale(head) * Math.Clamp(config.SurfaceSizeAdjust, 0.5f, 2f);
        surfaceShapeHead = float.IsFinite(head) ? head : 0;
        surfaceSizeScale = scale;
        var signature = AreaSignature(stand);
        var radius = AreaHalf * AreaCube;
        var instances = VisualNear(new Vector3(stand.X, stand.Y + 1, stand.Z), radius + 1.5f);
        var models = instances.Count;
        bool Ray(Vector3 o, Vector3 d, float max, out Vector3 p, out Vector3 n, out float dist) => VisualRayIn(instances, o, d, max, out p, out n, out dist);
        yield return true;

        // ---- 1. the solid picture: every nearby triangle marks the cubes it passes through ----
        var size = AreaHalf * 2;
        var origin = new Vector3(MathF.Round(stand.X / AreaCube) * AreaCube - radius, MathF.Floor((stand.Y - AreaBelow) / AreaCube) * AreaCube,
            MathF.Round(stand.Z / AreaCube) * AreaCube - radius);
        if(areaVoxels == null) areaVoxels = new VoxelGrid(origin, size, AreaTall, size, AreaCube);
        else areaVoxels.Reset(origin);
        var voxels = areaVoxels;
        var work = 0;
        foreach(var instance in instances) {
            var vertices = instance.Mesh.Vertices;
            var triangles = instance.Mesh.Triangles;
            var matrix = Matrix4x4.CreateScale(instance.Scale) * Matrix4x4.CreateFromQuaternion(instance.Rotation) * Matrix4x4.CreateTranslation(instance.Position);
            for(var t = 0; t + 2 < triangles.Length; t += 3) {
                voxels.AddTriangle(Vector3.Transform(vertices[triangles[t]], matrix), Vector3.Transform(vertices[triangles[t + 1]], matrix),
                    Vector3.Transform(vertices[triangles[t + 2]], matrix));
                if(++work % 256 == 0) yield return true;
            }
        }

        // ---- 2. open space: everything reachable from where you stand (so the insides of things aren't) ----
        if(!voxels.BeginFlood(stand + Vector3.UnitY * MathF.Max(0.5f, scale))) {
            areaMap = new AreaMapView(new(), new(), new(), new(), new(), "no open space found here", stand, mover, voxels.Cells.Length, 0, areaScanMs, models, signature);
            yield break;
        }
        while(!voxels.StepFlood(8000)) yield return true;

        // ---- 3. tops: a level face with open space straight above it. Also the upright runs poles are made of ----
        var grid = new AreaGrid(size, AreaCube, origin.X + AreaCube / 2, origin.Z + AreaCube / 2);
        var need = Math.Max(2, (int)MathF.Ceiling(0.3f * scale / AreaCube));
        var runLow = new int[size * size];
        var runHigh = new int[size * size];
        var cells = voxels.Cells;
        for(var z = 0; z < size; z++) {
            for(var x = 0; x < size; x++) {
                var column = z * size + x;
                int bestLow = -1, bestHigh = -1, low = -1;
                for(var y = AreaTall - 1; y >= 0; y--) {
                    var flags = cells[voxels.Index(x, y, z)];
                    // upright run (counted top-down: "low" is where the current run started, i.e. its top)
                    if((flags & VoxelGrid.Side) != 0) { if(low < 0) low = y; }
                    else if(low >= 0) { if(low - y > bestHigh - bestLow) { bestHigh = low; bestLow = y + 1; } low = -1; }
                    if((flags & VoxelGrid.Level) == 0 || y + 1 >= AreaTall || (cells[voxels.Index(x, y + 1, z)] & VoxelGrid.Free) == 0) continue;
                    var open = 0;
                    var above = y + 1;
                    while(above < AreaTall && (cells[voxels.Index(x, above, z)] & VoxelGrid.Free) != 0) { open++; above++; }
                    if(open < need) continue;
                    grid.Add(column, origin.Y + (y + 0.5f) * AreaCube, (flags & VoxelGrid.Flat) != 0 ? 1f : 0.75f,
                        above >= AreaTall ? float.PositiveInfinity : open * AreaCube);
                }
                if(low >= 0 && low + 1 > bestHigh - bestLow + 1) { bestHigh = low; bestLow = 0; }
                runLow[column] = bestLow;
                runHigh[column] = bestHigh;
            }
            if(z % 4 == 3) yield return true;
        }

        // ---- 4. surfaces, and which one is the floor ----
        var components = AreaRules.Components(grid, 0.06f, 0.3f * scale);
        yield return true;
        var under = AreaRules.NodeUnder(grid, stand.X, stand.Y, stand.Z);
        var underY = under >= 0 ? grid.Y[under] : stand.Y;
        var floorIndex = AreaRules.FloorIndex(components, grid, underY + 0.1f);
        if(floorIndex < 0) {
            areaMap = new AreaMapView(new(), new(), new(), new(), new(), VisualLoadingCount > 0 ? "models still loading..." : "no floor found here",
                stand, mover, voxels.Cells.Length, grid.LayerCount(), areaScanMs, models, signature);
            yield break;
        }
        // stairs, landings and raised floors are ground too: anything you can step onto from the floor
        var ground = AreaRules.Ground(grid, components, floorIndex, 0.32f, 100, 6, 0.9f * scale);
        yield return true;
        var owner = new int[grid.Y.Length];   // which surface each point belongs to
        Array.Fill(owner, -1);
        for(var i = 0; i < components.Count; i++) foreach(var node in components[i]) owner[node] = i;
        var floorAt = new float[grid.Cells];
        Array.Fill(floorAt, float.NaN);
        var floorCells = new HashSet<int>();
        foreach(var index in ground)
            foreach(var node in components[index]) { floorAt[node / AreaGrid.MaxLayers] = grid.Y[node]; floorCells.Add(node / AreaGrid.MaxLayers); }
        var floorMean = AreaRules.MeanY(grid, components[floorIndex]);
        // cubes are 5 cm, so heights come in 5 cm steps: one ray under you gives the floor's exact height
        var floorFix = under >= 0 && Ray(new Vector3(stand.X, stand.Y + 0.3f, stand.Z), -Vector3.UnitY, 0.8f, out var exactFloor, out _, out _) &&
            MathF.Abs(exactFloor.Y - underY) < 0.06f ? exactFloor.Y - underY : 0f;
        // the floor next to (or under) a set of grid points
        float LocalFloor(IReadOnlyList<int> around) {
            double sum = 0;
            var count = 0;
            var stride = Math.Max(1, around.Count / 60);
            for(var k = 0; k < around.Count; k += stride) {
                int x = around[k] % size, z = around[k] / size;
                for(var dz = -6; dz <= 6; dz += 2)
                    for(var dx = -6; dx <= 6; dx += 2) {
                        int nx = x + dx, nz = z + dz;
                        if(nx < 0 || nz < 0 || nx >= size || nz >= size || float.IsNaN(floorAt[nz * size + nx])) continue;
                        sum += floorAt[nz * size + nx];
                        count++;
                    }
            }
            return count > 0 ? (float)(sum / count) : floorMean;
        }
        // an outline along the edges of the grid squares, then rounded off
        List<Vector3> Trace(List<(int Cell, int Dx, int Dz)> loop, Func<int, float> heightOf, float lift) {
            var points = new List<Vector3>(loop.Count);
            foreach(var (inCell, dx, dz) in loop) {
                var edge = grid.Centre(inCell) + new Vector2(dx, dz) * (AreaCube / 2);
                points.Add(new Vector3(edge.X, heightOf(inCell) + lift, edge.Y));
            }
            return AreaRules.Smooth(points, 2, 0.012f);
        }
        bool Blocked(int x, int y, int z) => voxels.Inside(x, y, z) && (cells[voxels.Index(x, y, z)] & VoxelGrid.Free) == 0;

        // ---- 5. what you're standing on ----
        var standingOn = "floor";
        if(under >= 0 && owner[under] >= 0 && owner[under] != floorIndex) {
            if(ground.Contains(owner[under])) standingOn = "stairs / raised floor";
            else {
                var rise = underY - LocalFloor(new[] { under / AreaGrid.MaxLayers });
                if(rise >= 0.08f) {
                    var narrow = AreaRules.Extents(components[owner[under]].Select(node => grid.Centre(node / AreaGrid.MaxLayers)).ToList(), AreaCube).Short;
                    var what = narrow > 3f ? "platform" : ShapeRules.Kind(rise, grid.Flat[under], MathF.Max(narrow, 0.2f), scale);
                    standingOn = FormattableString.Invariant($"{what}, {rise:0.00} above the floor");
                }
            }
        }
        yield return true;

        // ---- 6. the floor's outline, and walls along it ----
        var floorOutlines = new List<List<Vector3>>();
        var walls = new List<(Vector3, Vector3)>();
        var knee = Math.Max(2, (int)MathF.Round(0.5f * scale / AreaCube));
        var shoulder = Math.Max(4, (int)MathF.Round(1.5f * scale / AreaCube));
        foreach(var loop in AreaRules.Outlines(floorCells, size)) {
            if(loop.Count < 24) continue;
            floorOutlines.Add(Trace(loop, inCell => floorAt[inCell] + floorFix, 0.01f));
            // a wall: something solid right past the floor's edge, at knee height and at shoulder height
            var lastDx = 0;
            var lastDz = 0;
            var lastCell = -1;
            foreach(var (inCell, dx, dz) in loop) {
                int ox = inCell % size + dx, oz = inCell / size + dz;
                var level = (int)MathF.Floor((floorAt[inCell] - origin.Y) / AreaCube);
                if(ox < 0 || oz < 0 || ox >= size || oz >= size || !Blocked(ox, level + knee, oz) || !Blocked(ox, level + shoulder, oz)) { lastCell = -1; continue; }
                var middle = grid.Centre(inCell) + new Vector2(dx, dz) * (AreaCube / 2);
                var along = new Vector3(-dz, 0, dx) * (AreaCube / 2);
                var foot = new Vector3(middle.X, floorAt[inCell] + floorFix + 0.02f, middle.Y);
                // straight on from the last piece: make that one longer instead of adding another
                var straight = lastCell >= 0 && dx == lastDx && dz == lastDz && walls.Count > 0 &&
                    Math.Abs(inCell % size - lastCell % size) + Math.Abs(inCell / size - lastCell / size) == 1;
                if(straight) walls[^1] = (walls[^1].Item1, foot + along);
                else walls.Add((foot - along, foot + along));
                lastDx = dx; lastDz = dz; lastCell = inCell;
            }
            yield return true;
        }

        // ---- 7. the objects: every other surface above the floor ----
        var objects = new List<AreaObject>();
        var me = new Vector2(stand.X, stand.Z);
        // pieces of one thing (a rail split by a post): surfaces at the same height within two squares of each other
        var joined = Enumerable.Range(0, components.Count).ToArray();
        int Root(int piece) { while(joined[piece] != piece) piece = joined[piece] = joined[joined[piece]]; return piece; }
        var means = components.Select(part => AreaRules.MeanY(grid, part)).ToArray();
        for(var node = 0; node < owner.Length; node++) {
            var mine = owner[node];
            if(mine < 0 || ground.Contains(mine)) continue;
            int x = node / AreaGrid.MaxLayers % size, z = node / AreaGrid.MaxLayers / size;
            for(var dz = 0; dz <= 2; dz++)
                for(var dx = -2; dx <= 2; dx++) {
                    int nx = x + dx, nz = z + dz;
                    if(nx < 0 || nx >= size || nz >= size) continue;
                    var first = (nz * size + nx) * AreaGrid.MaxLayers;
                    for(var layer = 0; layer < AreaGrid.MaxLayers; layer++) {
                        var other = owner[first + layer];
                        if(other < 0 || other == mine || ground.Contains(other) || MathF.Abs(means[mine] - means[other]) >= 0.08f) continue;
                        joined[Root(mine)] = Root(other);
                    }
                }
            if(node % 4096 == 4095) yield return true;
        }
        var clusters = Enumerable.Range(0, components.Count).Where(index => !ground.Contains(index)).GroupBy(Root)
            .Select(members => members.SelectMany(index => components[index]).ToList()).ToList();
        foreach(var nodes in clusters) {
            if(nodes.Count < 30) continue;
            var heights = new Dictionary<int, float>();
            foreach(var node in nodes) heights[node / AreaGrid.MaxLayers] = grid.Y[node];
            var meanY = AreaRules.MeanY(grid, nodes);
            var floorHere = LocalFloor(heights.Keys.ToList());
            var height = meanY - floorHere;
            if(height < 0.08f || height > 1.8f * scale) continue;
            // mostly tucked under something else (a table's foot under its top): not a place to sit or lean
            if(AreaRules.Covered(grid, nodes, 0.9f * scale) > 0.5f) continue;
            foreach(var filled in AreaRules.Close(new HashSet<int>(heights.Keys), size))
                if(!heights.ContainsKey(filled)) heights[filled] = meanY;
            var centres = heights.Keys.Select(key => grid.Centre(key)).ToList();
            var (longSide, shortSide) = AreaRules.Extents(centres, AreaCube);
            var flatness = nodes.Average(node => grid.Flat[node]);
            var loops = AreaRules.Outlines(new HashSet<int>(heights.Keys), size);
            var middle = centres.Aggregate(Vector2.Zero, (sum, point) => sum + point) / centres.Count;
            var midCell = heights.Keys.OrderBy(key => Vector2.DistanceSquared(grid.Centre(key), middle)).First();
            var midAt = grid.Centre(midCell);
            // the exact height of the top (the cubes only know it to 5 cm)
            var topFix = Ray(new Vector3(midAt.X, heights[midCell] + 0.2f, midAt.Y), -Vector3.UnitY, 0.4f, out var exactTop, out _, out _) &&
                MathF.Abs(exactTop.Y - heights[midCell]) < 0.06f ? exactTop.Y - heights[midCell] : 0f;
            height += topFix - floorFix;
            var outlines = loops.Where(border => border.Count >= 8).Take(12).Select(border => Trace(border, inCell => heights[inCell] + topFix, 0.01f)).ToList();
            if(outlines.Count == 0) continue;
            var outline = outlines.SelectMany(border => border).ToList();
            var midNode = nodes.FirstOrDefault(node => node / AreaGrid.MaxLayers == midCell, -1);
            var clearance = midNode >= 0 ? MathF.Min(grid.Room[midNode], 2.5f) : 2.5f;
            // open underneath (legs) or solid (a box): look in under the nearest edge at half height
            var open = false;
            if(height >= 0.2f && outline.Count > 0) {
                var nearest = outline.OrderBy(point => Vector2.DistanceSquared(new Vector2(point.X, point.Z), me)).First();
                var inward = SurfaceRules.Normalize(middle - new Vector2(nearest.X, nearest.Z));
                var start = new Vector3(nearest.X - inward.X * 0.1f, meanY - height / 2, nearest.Z - inward.Y * 0.1f);
                open = !Ray(start, new Vector3(inward.X, 0, inward.Y), 0.45f, out _, out _, out _);
            }
            var kind = shortSide > 3f ? "platform" : ShapeRules.Kind(height, flatness, shortSide, scale);
            // long and narrow: a rail or ledge, whatever its height
            if(shortSide < 0.5f && longSide > shortSide * 3 && kind != "edge" && kind != "ledge") kind = $"ledge / rail ({kind} height)";
            // a seat: what stands around it? Raised sides are the backrest and armrests, the open side is the front
            AreaSeat? seat = null;
            if(kind is "seat" or "low seat") {
                var box = AreaBox.Fit(centres, AreaCube / 2);
                var rises = new[] { new List<float>(), new List<float>(), new List<float>(), new List<float>() };
                foreach(var (inCell, dx, dz) in loops[0]) {
                    var seatLevel = (int)MathF.Floor((heights[inCell] - origin.Y) / AreaCube);
                    var best = 0;
                    for(var k = 1; k <= 3; k++) {   // just past the edge, up to 15 cm out (a rolled arm bulges)
                        int cx = inCell % size + dx * k, cz = inCell / size + dz * k;
                        int highest = -1, solid = 0;
                        for(var y = seatLevel + 1; y <= seatLevel + 24 && y < AreaTall; y++)
                            if(Blocked(cx, y, cz)) { highest = y; solid++; }
                        // mostly solid from the seat up to there (not something hanging in the air above it)
                        if(highest > seatLevel && solid >= (highest - seatLevel) * 0.6f) best = Math.Max(best, highest - seatLevel);
                    }
                    rises[box.SideOf(grid.Centre(inCell) + new Vector2(dx, dz) * (AreaCube / 2))].Add(best * AreaCube);
                }
                var rise = new float[4];
                var share = new float[4];
                for(var side = 0; side < 4; side++) {
                    var up = rises[side].Where(value => value >= 0.08f * scale).OrderBy(value => value).ToList();
                    share[side] = rises[side].Count > 0 ? up.Count / (float)rises[side].Count : 0;
                    rise[side] = up.Count > 0 ? up[up.Count / 2] : 0;
                }
                seat = new AreaSeat(SeatRules.Read(rise, share, box.HalfLong * 2, box.HalfShort * 2, box.SideOf(me), scale), box, meanY + topFix, rise);
            }
            objects.Add(new AreaObject(outlines, new Vector3(middle.X, meanY + topFix, middle.Y), height, flatness, longSide, shortSide, clearance, open, kind, seat));
            yield return true;
        }

        // ---- 8. poles: a small upright thing, tall, with room around it ----
        var poles = new List<AreaPole>();
        var poleTall = (int)MathF.Round(1.2f / AreaCube);
        var seen = new bool[size * size];
        var queue = new Queue<int>();
        for(var first = 0; first < seen.Length; first++) {
            if(seen[first] || runHigh[first] - runLow[first] + 1 < poleTall) continue;
            int minX = size, maxX = -1, minZ = size, maxZ = -1, bottom = AreaTall, top = -1, count = 0;
            seen[first] = true;
            queue.Enqueue(first);
            while(queue.Count > 0) {
                var column = queue.Dequeue();
                int x = column % size, z = column / size;
                count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                bottom = Math.Min(bottom, runLow[column]); top = Math.Max(top, runHigh[column]);
                for(var side = 0; side < 9; side++) {
                    int nx = x + side % 3 - 1, nz = z + side / 3 - 1;
                    if(side == 4 || nx < 0 || nz < 0 || nx >= size || nz >= size) continue;
                    var next = nz * size + nx;
                    if(seen[next] || runHigh[next] - runLow[next] + 1 < poleTall) continue;
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
            if(maxX - minX > 3 || maxZ - minZ > 3) continue;   // far too wide to be a pole (the cubes round its size up)
            // room around it: the ring of squares a little way out is open for most of its height
            int open = 0, total = 0;
            for(var z = minZ - 3; z <= maxZ + 3; z++)
                for(var x = minX - 3; x <= maxX + 3; x++) {
                    if(x > minX - 3 && x < maxX + 3 && z > minZ - 3 && z < maxZ + 3) continue;   // only the ring
                    if(x < 0 || z < 0 || x >= size || z >= size) continue;
                    for(var y = bottom + 4; y <= top - 4; y += 4) { total++; if((cells[voxels.Index(x, y, z)] & VoxelGrid.Free) != 0) open++; }
                }
            if(total == 0 || open < total * 0.7f) continue;
            var centre = new Vector3(origin.X + (minX + maxX + 1) * AreaCube / 2, origin.Y + bottom * AreaCube, origin.Z + (minZ + maxZ + 1) * AreaCube / 2);
            var thick = (Math.Max(maxX - minX, maxZ - minZ) + 1) * AreaCube;
            // its exact middle and thickness: rays in from eight sides at half height
            var hits = new List<Vector3>();
            var midY = origin.Y + (bottom + top + 1) * AreaCube / 2;
            for(var k = 0; k < 8; k++) {
                var dir = new Vector3(MathF.Sin(k * MathF.Tau / 8), 0, MathF.Cos(k * MathF.Tau / 8));
                if(Ray(new Vector3(centre.X, midY, centre.Z) + dir * (thick / 2 + 0.3f), -dir, thick / 2 + 0.3f, out var hit, out _, out _)) hits.Add(hit);
            }
            if(hits.Count >= 5) {
                var mean = hits.Aggregate(Vector3.Zero, (sum, point) => sum + point) / hits.Count;
                centre = new Vector3(mean.X, centre.Y, mean.Z);
                thick = hits.Average(point => Vector2.Distance(new Vector2(point.X, point.Z), new Vector2(mean.X, mean.Z))) * 2;
            } else thick = MathF.Max(0.02f, thick - 0.07f);   // not measured: the cubes make it look about this much thicker
            if(thick > PoleThickest + 0.005f) continue;   // a post, a pillar, a lamp stand: not a pole
            poles.Add(new AreaPole(centre, (top - bottom + 1) * AreaCube, thick, top >= AreaTall - 1));
            if(poles.Count >= 24) break;
        }
        yield return true;

        // ---- 9. the points themselves, for the "show map points" view ----
        var dots = new List<(Vector3, bool, float)>();
        if(config.SurfaceMapPoints)
            for(var node = 0; node < owner.Length; node++) {
                if(owner[node] < 0) continue;
                var cell = node / AreaGrid.MaxLayers;
                if(cell % size % 3 != 0 || cell / size % 3 != 0) continue;   // every third, or there are too many to draw
                var at = grid.Centre(cell);
                dots.Add((new Vector3(at.X, grid.Y[node], at.Y), ground.Contains(owner[node]), grid.Y[node] - floorMean));
            }
        areaMap = new AreaMapView(floorOutlines, objects, walls, dots, poles, standingOn, stand, mover, voxels.Cells.Length, grid.LayerCount(), areaScanMs, models, signature);
    }

    private static void DrawLoop(ImDrawListPtr draw, List<Vector3> points, uint color, float thickness, uint fill = 0) {
        if(points.Count < 2) return;
        var screens = new Vector2[points.Count];
        var ok = new bool[points.Count];
        for(var i = 0; i < points.Count; i++) ok[i] = Screen(points[i], out screens[i]);
        if(fill != 0) {
            var middle = points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Count;
            if(Screen(middle, out var centre))
                for(var i = 0; i < points.Count; i++) {
                    var next = (i + 1) % points.Count;
                    if(ok[i] && ok[next]) draw.AddTriangleFilled(centre, screens[i], screens[next], fill);
                }
        }
        for(var i = 0; i < points.Count; i++) {
            var next = (i + 1) % points.Count;
            if(ok[i] && ok[next]) draw.AddLine(screens[i], screens[next], color, thickness);
        }
    }

    private void DrawAreaMap(ImDrawListPtr draw) {
        if(areaMap is not { } map) return;
        var black = Color(0, 0, 0, 0.85f);
        foreach(var (point, floor, height) in map.Dots) {
            if(!Screen(point, out var dot)) continue;
            var warm = Math.Clamp(height / 1.5f, 0f, 1f);
            draw.AddCircleFilled(dot, 1.5f, floor ? Color(0.3f, 1, 0.3f, 0.45f) : Color(1, 1 - warm * 0.7f, 0.2f, 0.7f));
        }
        foreach(var outline in map.FloorOutlines) DrawLoop(draw, outline, Color(0.3f, 1, 0.3f, 0.9f), 2);
        foreach(var (a, b) in map.Walls)
            if(Screen(a, out var wallA) && Screen(b, out var wallB)) draw.AddLine(wallA, wallB, Color(0.2f, 0.9f, 1, 1), 3);
        var me = new Vector2(map.Stand.X, map.Stand.Z);
        foreach(var o in map.Objects) {
            foreach(var piece in o.Outlines) DrawLoop(draw, piece, Color(1, 0.6f, 0.1f, 0.9f), 1.5f, Color(1, 0.6f, 0.1f, 0.2f));
            if(o.Seat is { } seat) DrawSeat(draw, seat);
            // the edge nearest you (where you'd sit or lean) stands out
            var nearest = o.Outlines.SelectMany(piece => piece).OrderBy(point => Vector2.DistanceSquared(new Vector2(point.X, point.Z), me)).FirstOrDefault();
            foreach(var piece in o.Outlines) {
                if(o.Seat != null) break;   // a seat shows its front instead
                for(var i = 0; i < piece.Count; i++) {
                    var a = piece[i];
                    var b = piece[(i + 1) % piece.Count];
                    if(Vector3.Distance(a, nearest) > 0.4f || Vector3.Distance(b, nearest) > 0.4f) continue;
                    if(!Screen(a, out var edgeA) || !Screen(b, out var edgeB)) continue;
                    draw.AddLine(edgeA, edgeB, black, 5);
                    draw.AddLine(edgeA, edgeB, Color(1, 0.9f, 0.2f, 1), 3);
                }
            }
            if(!Screen(o.Centre + Vector3.UnitY * 0.05f, out var label)) continue;
            var top = o.Flatness > 0.97f ? "flat" : o.Flatness > 0.85f ? "semi-flat" : "sloped";
            var text = FormattableString.Invariant(
                $"{o.Kind}  {o.Height:0.00} above floor, {top}, {o.Short:0.00}x{o.Long:0.00}, {(o.OpenBelow ? "open" : "solid")} below, room above {o.Clearance:0.0}");
            if(o.Seat is { } sit) {
                var back = sit.Read.Back >= 0 ? FormattableString.Invariant($", back +{sit.Rise[sit.Read.Back]:0.00}{(sit.Rise[sit.Read.Back] >= 1.15f ? " or more" : "")}") : ", no back";
                var arms = sit.Read.Arms.Length > 0 ? FormattableString.Invariant($", arms +{sit.Read.Arms.Average(arm => sit.Rise[arm]):0.00}") : "";
                text = FormattableString.Invariant($"{sit.Read.Type}: seat {o.Height:0.00} high, {sit.Read.Depth:0.00} deep, {sit.Read.Width:0.00} wide{back}{arms}");
            }
            var textSize = ImGui.CalcTextSize(text);
            label -= new Vector2(textSize.X / 2, textSize.Y / 2);
            draw.AddText(label + new Vector2(1, 1), black, text);
            draw.AddText(label, Color(1, 0.9f, 0.2f, 1), text);
        }
        foreach(var pole in map.Poles) {
            var purple = Color(0.85f, 0.45f, 1, 1);
            var poleTop = pole.Bottom + Vector3.UnitY * pole.Height;
            if(Screen(pole.Bottom, out var poleA) && Screen(poleTop, out var poleB)) {
                draw.AddLine(poleA, poleB, black, 5);
                draw.AddLine(poleA, poleB, purple, 3);
            }
            foreach(var part in new[] { 0.15f, 0.5f, 0.85f }) {
                var ring = new List<Vector3>();
                for(var k = 0; k < 12; k++)
                    ring.Add(pole.Bottom + new Vector3(MathF.Sin(k * MathF.Tau / 12), 0, MathF.Cos(k * MathF.Tau / 12)) * MathF.Max(pole.Thickness / 2, 0.03f) + Vector3.UnitY * (pole.Height * part));
                DrawLoop(draw, ring, purple, 2);
            }
            if(!Screen(pole.Bottom + Vector3.UnitY * (pole.Height * 0.6f), out var poleLabel)) continue;
            var poleText = FormattableString.Invariant($"pole  {pole.Thickness:0.00} thick, {pole.Height:0.0}{(pole.GoesOn ? "+" : "")} tall");
            draw.AddText(poleLabel + new Vector2(9, 1), black, poleText);
            draw.AddText(poleLabel + new Vector2(8, 0), purple, poleText);
        }
        if(Screen(map.Stand, out var feet)) {
            var text = "you're on: " + map.StandingOn;
            draw.AddText(feet + new Vector2(13, 9), black, text);
            draw.AddText(feet + new Vector2(12, 8), Color(0.3f, 1, 0.3f, 1), text);
        }
    }

    /// <summary>A seat's sides: the backrest (blue) and armrests (lighter) as upright panels, the front as a yellow
    /// line with an arrow the way you'd face sitting on it.</summary>
    private static void DrawSeat(ImDrawListPtr draw, AreaSeat seat) {
        var black = Color(0, 0, 0, 0.85f);
        Vector3 At(Vector2 flat, float lift) => new(flat.X, seat.Y + lift, flat.Y);
        void Panel(int side, uint line, uint fill) {
            var mid = seat.Box.Centre + seat.Box.Outward(side) * seat.Box.Reach(side);
            var half = seat.Box.AlongSide(side) * seat.Box.HalfSide(side);
            var corners = new[] { At(mid - half, 0), At(mid + half, 0), At(mid + half, seat.Rise[side]), At(mid - half, seat.Rise[side]) };
            var screens = new Vector2[4];
            for(var i = 0; i < 4; i++) if(!Screen(corners[i], out screens[i])) return;
            draw.AddQuadFilled(screens[0], screens[1], screens[2], screens[3], fill);
            draw.AddQuad(screens[0], screens[1], screens[2], screens[3], line, 2);
        }
        if(seat.Read.Back >= 0) Panel(seat.Read.Back, Color(0.3f, 0.55f, 1, 1), Color(0.3f, 0.55f, 1, 0.25f));
        foreach(var arm in seat.Read.Arms) Panel(arm, Color(0.55f, 0.8f, 1, 1), Color(0.55f, 0.8f, 1, 0.2f));
        if(seat.Read.Front < 0) return;
        var front = seat.Read.Front;
        var out2 = seat.Box.Outward(front);
        var edge = seat.Box.Centre + out2 * seat.Box.Reach(front);
        var along = seat.Box.AlongSide(front) * seat.Box.HalfSide(front);
        var yellow = Color(1, 0.9f, 0.2f, 1);
        if(Screen(At(edge - along, 0.01f), out var left) && Screen(At(edge + along, 0.01f), out var right)) {
            draw.AddLine(left, right, black, 6);
            draw.AddLine(left, right, yellow, 4);
        }
        // the way you'd face
        var tip = edge + out2 * 0.35f;
        var wing = seat.Box.AlongSide(front) * 0.08f;
        if(Screen(At(edge, 0.01f), out var from0) && Screen(At(tip, 0.01f), out var to0) &&
            Screen(At(tip - out2 * 0.12f + wing, 0.01f), out var wingA) && Screen(At(tip - out2 * 0.12f - wing, 0.01f), out var wingB)) {
            draw.AddLine(from0, to0, black, 5);
            draw.AddLine(from0, to0, yellow, 3);
            draw.AddTriangleFilled(to0, wingA, wingB, yellow);
        }
    }

    /// <summary>The map's cost, for the tab.</summary>
    private string AreaMapCost() {
        if(areaMap is not { } map) return areaScan != null ? "Mapping the area..." : "No map yet.";
        long bytes = 0;
        var loaded = 0;
        foreach(var mesh in visualModels.Values) if(mesh != null) { bytes += mesh.Bytes; loaded++; }
        return FormattableString.Invariant(
            $"Map: {map.Points / 1000000.0:0.0} million 5 cm cubes, {map.Layers} surface points, {map.Objects.Count} objects, {map.Poles.Count} poles, {map.Models} models in range; built in {map.BuildMs:0} ms (about 1.5 ms a frame). Models loaded: {loaded}, about {bytes / 1048576.0:0} MB.");
    }
}
