using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace AnimSync;

// The walls you SEE: rays against the scenery models' own triangles (BgObjects in the scene: map pieces,
// housing furniture), instead of the invisible collision that can sit in front of them.
public sealed partial class AnimSync {
    private readonly ConcurrentDictionary<string, MeshBvh?> visualModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> visualLoading = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Path, Vector3 Position, Quaternion Rotation, Vector3 Scale)> visualPlaced = new();
    private long visualSceneAt;
    private Vector3 visualSceneCentre;
    private const float VisualRange = 60f;         // scenery placed this close (by its origin) is considered
    private const int VisualMaxTriangles = 400_000;

    /// <summary>Re-lists the scenery around a point (twice a second at most) and starts loading models not seen before.</summary>
    private unsafe void RefreshVisualScene(Vector3 centre) {
        var now = Environment.TickCount64;
        if(now < visualSceneAt && Vector3.DistanceSquared(centre, visualSceneCentre) < 4) return;
        visualSceneAt = now + 500;
        visualSceneCentre = centre;
        var placed = new List<(string, Vector3, Quaternion, Vector3)>();
        try {
            var world = World.Instance();
            if(world == null) return;
            var first = ((SceneObject*)world)->ChildObject;
            if(first == null) return;
            var seen = 0;
            for(var o = first; o != null && seen < 200_000; seen++) {
                if(o->GetObjectType() == ObjectType.BgObject) {
                    var bg = (BgObject*)o;
                    var position = (Vector3)o->Position;
                    if(bg->ModelResourceHandle != null && Vector3.DistanceSquared(position, centre) < VisualRange * VisualRange) {
                        var path = bg->ModelResourceHandle->ResourceHandle.FileName.ToString();
                        if(!string.IsNullOrEmpty(path)) {
                            placed.Add((path, position, (Quaternion)o->Rotation, (Vector3)o->Scale));
                            if(!visualModels.ContainsKey(path) && visualLoading.TryAdd(path, 0)) LoadVisualModel(path);
                        }
                    }
                }
                o = o->NextSiblingObject;
                if(o == first) break;
            }
        } catch(Exception ex) { Logger.Debug(ex, "Couldn't read the scenery around you."); }
        // most of the world is terrain: square "plates", each its own model, laid out by the map's terrain.tera
        try {
            var layout = FFXIVClientStructs.FFXIV.Client.LayoutEngine.LayoutWorld.Instance();
            if(layout != null && layout->ActiveLayout != null)
                foreach(var (_, terrain) in layout->ActiveLayout->Terrains) {
                    if(terrain.Value == null) continue;
                    var folder = terrain.Value->PathString.TrimEnd('/');
                    if(folder.Length == 0 || TerrainPlates(folder) is not { } plates) continue;
                    var reach = plates.Size * 0.75f + 4;
                    for(var i = 0; i < plates.Positions.Length; i++) {
                        var at = plates.Positions[i];
                        if(MathF.Abs(at.X - centre.X) > reach || MathF.Abs(at.Y - centre.Z) > reach) continue;
                        var path = $"{folder}/bgplate/{i:D4}.mdl";
                        placed.Add((path, new Vector3(at.X, 0, at.Y), Quaternion.Identity, Vector3.One));
                        if(!visualModels.ContainsKey(path) && visualLoading.TryAdd(path, 0)) LoadVisualModel(path);
                    }
                }
        } catch(Exception ex) { Logger.Debug(ex, "Couldn't read the terrain around you."); }
        visualPlaced = placed;
        DropUnusedModels(placed, now);
    }

    private readonly Dictionary<string, long> visualLastNear = new(StringComparer.OrdinalIgnoreCase);
    private long visualCleanAt;
    private uint visualTerritory;

    /// <summary>Keeps memory in check: models from a zone you've left go at once, models you haven't been near for two minutes go too.</summary>
    private void DropUnusedModels(List<(string Path, Vector3, Quaternion, Vector3)> placed, long now) {
        var territory = (uint)Client.TerritoryType;
        if(territory != visualTerritory) {
            visualTerritory = territory;
            visualLastNear.Clear();
            foreach(var path in visualModels.Keys.ToList()) if(!visualLoading.ContainsKey(path)) visualModels.TryRemove(path, out _);
            terrainPlates.Clear();
            areaMap = null;
        }
        foreach(var (path, _, _, _) in placed) visualLastNear[path] = now;
        if(now < visualCleanAt) return;
        visualCleanAt = now + 10_000;
        foreach(var path in visualModels.Keys.ToList()) {
            if(visualLoading.ContainsKey(path)) continue;
            if(visualLastNear.TryGetValue(path, out var last) && now - last < 120_000) continue;
            visualModels.TryRemove(path, out _);
            visualLastNear.Remove(path);
        }
    }

    private readonly Dictionary<string, (Vector2[] Positions, float Size)?> terrainPlates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each terrain plate of a map sits (centre, x/z) and how big plates are, from its terrain.tera.</summary>
    private (Vector2[] Positions, float Size)? TerrainPlates(string folder) {
        if(terrainPlates.TryGetValue(folder, out var cached)) return cached;
        (Vector2[], float)? result = null;
        try {
            var tera = Data.GetFile<Lumina.Data.Files.TeraFile>($"{folder}/bgplate/terrain.tera");
            if(tera != null && tera.PlateCount is > 0 and < 100_000) {
                var positions = new Vector2[tera.PlateCount];
                for(var i = 0; i < positions.Length; i++) positions[i] = tera.GetPlatePosition(i);
                result = (positions, tera.PlateSize);
            }
        } catch(Exception ex) { Logger.Debug(ex, $"Couldn't read {folder}/bgplate/terrain.tera."); }
        if(terrainPlates.Count > 32) terrainPlates.Clear();
        terrainPlates[folder] = result;
        return result;
    }

    /// <summary>Reads a model's triangles in the background (game file, or the mod file Penumbra swapped in).</summary>
    private void LoadVisualModel(string path) {
        Task.Run(() => {
            MeshBvh? mesh = null;
            try {
                var file = path;
                var bar = file.LastIndexOf('|');
                if(bar >= 0) file = file[(bar + 1)..];
                var mdl = System.IO.Path.IsPathRooted(file)
                    ? Data.GameData.GetFileFromDisk<Lumina.Data.Files.MdlFile>(file)
                    : Data.GetFile<Lumina.Data.Files.MdlFile>(file);
                if(mdl != null) {
                    var model = new Lumina.Models.Models.Model(mdl, Lumina.Models.Models.Model.ModelLod.High);
                    var vertices = new List<Vector3>();
                    var indices = new List<int>();
                    foreach(var part in model.Meshes) {
                        // what you see: main and glass surfaces (no shadow/water/fog/light-shaft helpers)
                        if(!part.Types.Contains(Lumina.Models.Models.Mesh.MeshType.Main) && !part.Types.Contains(Lumina.Models.Models.Mesh.MeshType.Glass)) continue;
                        if(part.Types.Contains(Lumina.Models.Models.Mesh.MeshType.Shadow) || part.Types.Contains(Lumina.Models.Models.Mesh.MeshType.LightShaft)) continue;
                        var start = vertices.Count;
                        foreach(var v in part.Vertices) {
                            var p = v.Position ?? Vector4.Zero;
                            vertices.Add(new Vector3(p.X, p.Y, p.Z));
                        }
                        foreach(var i in part.Indices) indices.Add(start + i);
                        if(indices.Count / 3 > VisualMaxTriangles) break;
                    }
                    if(indices.Count >= 3) mesh = new MeshBvh(vertices.ToArray(), indices.Take(indices.Count / 3 * 3).ToArray());
                }
            } catch(Exception ex) { Logger.Debug(ex, $"Couldn't read the model {path}."); }
            visualModels[path] = mesh;
            visualLoading.TryRemove(path, out _);
        });
    }

    /// <summary>The scenery whose box comes within reach of a point, ready to raycast (models still loading are skipped).</summary>
    private List<MeshInstance> VisualNear(Vector3 point, float reach) {
        var near = new List<MeshInstance>();
        foreach(var (path, position, rotation, scale) in visualPlaced) {
            if(!visualModels.TryGetValue(path, out var mesh) || mesh == null) continue;
            var instance = new MeshInstance(mesh, position, rotation, scale, path);
            if(instance.Near(point, reach)) near.Add(instance);
        }
        return near;
    }

    private List<MeshInstance> visualActive = new();

    /// <summary>Scenery models within reach of the body for this reading.</summary>
    private List<MeshInstance> VisualPick(Vector3 centre) {
        RefreshVisualScene(centre);
        return VisualNear(centre, 3f);
    }
    private int VisualLoadingCount => visualLoading.Count;
    private string VisualPlacedSummary {
        get {
            var list = visualPlaced;
            var plates = list.Count(p => p.Path.Contains("/bgplate/", StringComparison.OrdinalIgnoreCase));
            return $"{list.Count - plates} pieces + {plates} terrain plates around";
        }
    }

    /// <summary>Nearest hit on the visible scenery picked for this reading (see visualActive).</summary>
    private bool VisualRay(Vector3 origin, Vector3 direction, float max, out Vector3 point, out Vector3 normal, out float distance) =>
        VisualRayIn(visualActive, origin, direction, max, out point, out normal, out distance);

    /// <summary>Like VisualRayIn, but the normal is the face's own front side (not turned to face the ray), so a
    /// ray from above can tell the top of something from its underside.</summary>
    private static bool VisualRayFront(List<MeshInstance> instances, Vector3 origin, Vector3 direction, float max, out Vector3 point, out Vector3 front) {
        point = default; front = default;
        var distance = float.PositiveInfinity;
        var found = false;
        foreach(var instance in instances) {
            if(!instance.Raycast(origin, direction, MathF.Min(max, distance), out var p, out var n, out var d) || d >= distance) continue;
            point = p; distance = d;
            // a mirrored copy (negative scale) has its faces wound the other way
            front = instance.Scale.X * instance.Scale.Y * instance.Scale.Z < 0 ? -n : n;
            found = true;
        }
        if(found) front = Vector3.Normalize(front);
        return found;
    }

    private static bool VisualRayIn(List<MeshInstance> instances, Vector3 origin, Vector3 direction, float max, out Vector3 point, out Vector3 normal, out float distance) {
        point = default; normal = default; distance = float.PositiveInfinity;
        var found = false;
        foreach(var instance in instances) {
            if(!instance.Raycast(origin, direction, MathF.Min(max, distance), out var p, out var n, out var d) || d >= distance) continue;
            point = p; distance = d;
            normal = Vector3.Dot(n, direction) > 0 ? -n : n;   // face back along the ray
            found = true;
        }
        if(found) normal = Vector3.Normalize(normal);
        return found;
    }
}
