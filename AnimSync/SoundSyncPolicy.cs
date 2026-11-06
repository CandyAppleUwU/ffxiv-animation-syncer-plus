// Added: persistent per-character sound exclusions, separated from animation sync.
using System.Collections.Generic;

namespace AnimSync;

internal static class SoundSyncPolicy {
	public static string? PlayerKey(string name, uint homeWorld) =>
		string.IsNullOrWhiteSpace(name) || homeWorld == 0 ? null : $"{homeWorld}:{name}";
	public static bool CanSync(bool enabled, bool isPlayer, string? key, IReadOnlyDictionary<string, string> djs) =>
		enabled && (!isPlayer || (key != null && !djs.ContainsKey(key)));
}
