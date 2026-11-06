namespace AnimSync;

internal static class SyncScopeRules {
    public static bool ShouldScan(bool paused, bool autoEnabled, bool manualGroups) =>
        manualGroups || (autoEnabled && !paused);
    public static bool IncludesPlayer(int index, bool gpose) =>
        gpose ? index is >= 201 and <= 439 : index is >= 0 and < 200;
}
