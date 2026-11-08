using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnimSync;

public sealed partial class AnimSync {
    private readonly Dictionary<string, (long Length, DateTime Modified, HashSet<ushort> Models)> timelinePropModels = new(StringComparer.OrdinalIgnoreCase);

    private unsafe IReadOnlySet<ushort>? FindTimelinePropModels(IGameObject actor) {
        var key = AnimationSampling.TimelineKey((Character*)actor.Address, ResolveTimelineKey);
        if(string.IsNullOrEmpty(key)) return null;
        var path = key.Replace('\\', '/');
        if(!path.StartsWith("chara/action/", StringComparison.OrdinalIgnoreCase)) path = "chara/action/" + path;
        if(!path.StartsWith("chara/action/emote/", StringComparison.OrdinalIgnoreCase)) return null;
        if(!path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) path += ".tmb";

        try {
            var resolved = ResolveMusicPath(path, actor.ObjectIndex);
            if(!Path.IsPathRooted(resolved)) return null; // Only look for explicitly authored prop models in the resolved mod.
            var file = new FileInfo(resolved);
            var length = file.Length;
            var modified = file.LastWriteTimeUtc;
            if(timelinePropModels.TryGetValue(resolved, out var cached) && cached.Length == length && cached.Modified == modified)
                return cached.Models;
            if(length > 4 * 1024 * 1024) return null;
            var models = TmbPropModels.Read(File.ReadAllBytes(resolved));
            if(timelinePropModels.Count >= 128) timelinePropModels.Clear();
            timelinePropModels[resolved] = (length, modified, models);
            return models;
        } catch(Exception) { return null; } // A missing mod resource leaves ordinary playback available.
    }
}
