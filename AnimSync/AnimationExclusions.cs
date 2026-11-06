// Modified: only timeline keys under an emote directory are eligible.
using System;

namespace AnimSync;

internal static class AnimationExclusions {
	public static bool IsExcluded(string key) => GetExclusionReason(key) != null;
	public static string? GetExclusionReason(string key) {
		if(string.IsNullOrWhiteSpace(key)) return "Timeline key unavailable";
		if(key.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
		var parts = key.Replace('\\', '/').Trim('/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
		if(Array.IndexOf(parts, "..") >= 0) return "Invalid timeline path";
		var category = Array.IndexOf(parts, "emote");
		if(category < 0 || category == parts.Length - 1) return "Outside emote/";
		if(parts[category + 1].StartsWith("pose", StringComparison.Ordinal)) return "Excluded emote/pose animation";
		for(var i = category + 1; i < parts.Length; i++)
			if(parts[i].Contains("start", StringComparison.Ordinal)) return "Excluded emote containing start";
		return null;
	}
}
