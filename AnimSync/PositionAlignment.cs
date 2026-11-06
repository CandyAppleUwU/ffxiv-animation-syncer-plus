// Adapted from the user's CoordMatch/Plugin.cs: fixed 0.5y, guarded execution,
// nearest-player fallback and local-player-only position/facing setters.
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AnimSync;

internal sealed class PositionAlignment(IObjectTable objects, ITargetManager targets,
	IClientState client, ICondition conditions, IChatGui chat) {
	private sealed record Snap(ulong SelfId, nint SelfAddress, ulong TargetId, nint TargetAddress,
		uint Territory, Vector3 Position, float Rotation, string Name);
	private Snap? snap;
	private int ticks;
	public string? BlockReason =>
		conditions[ConditionFlag.InCombat] ? "Position sync is unavailable during combat."
		: conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] || conditions[ConditionFlag.BoundByDuty95]
			? "Position sync is unavailable in duties."
		: conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.WatchingCutscene78] || conditions[ConditionFlag.OccupiedInCutSceneEvent]
			? "Position sync is unavailable during cutscenes."
		: !client.IsLoggedIn || client.TerritoryType == 0 || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
			? "Wait until you finish loading before syncing position." : null;
	public void Cancel() => snap = null;
	private void Reply(string message) => chat.Print($"[AnimSync] {message}");
	private static unsafe bool Available(IGameObject obj) {
		if(obj is not IPlayerCharacter || obj.ObjectIndex >= 200) return false;
		var draw = ((NativeObject*)obj.Address)->DrawObject;
		return draw != null && draw->IsVisible;
	}
	public void Request() {
		Cancel();
		if(BlockReason is { } blocked) { Reply(blocked); return; }
		var self = objects.LocalPlayer;
		if(self == null) { Reply("No local player is loaded."); return; }
		var target = targets.Target;
		if(target == null) {
			var closest = PositionRules.Closest(self.Position, self.GameObjectId, objects.Where(Available)
				.Select(o => new PositionCandidate(o.GameObjectId, o.Position)));
			target = closest.HasValue ? objects.FirstOrDefault(o => o.GameObjectId == closest.Value && Available(o)) : null;
			if(target == null) { Reply("Too far — no player is within 0.5 yalms."); return; }
		}
		if(target.GameObjectId == self.GameObjectId) { Reply("Select another player, or clear your target to find the closest player."); return; }
		if(!Available(target)) { Reply("Target a visible player, or clear your target to find the closest player."); return; }
		if(!PositionRules.InRange(self.Position, target.Position)) { Reply("Too far — move within 0.5 yalms of your target."); return; }
		if(!float.IsFinite(target.Rotation)) return;
		snap = new(self.GameObjectId, self.Address, target.GameObjectId, target.Address,
			client.TerritoryType, target.Position, target.Rotation, target.Name.TextValue);
		ticks = 30;
	}
	public unsafe void Update() {
		if(snap is not { } current) return;
		var self = objects.LocalPlayer;
		if(BlockReason != null || self == null || self.GameObjectId != current.SelfId || self.Address != current.SelfAddress ||
			client.TerritoryType != current.Territory || !PositionRules.InRange(self.Position, current.Position)) { Cancel(); return; }
		var target = objects.FirstOrDefault(o => o.GameObjectId == current.TargetId && o.Address == current.TargetAddress);
		if(target == null || !Available(self) || !Available(target) || !PositionRules.InRange(self.Position, target.Position)) { Cancel(); return; }
		// Like CoordMatch, use the game's setters so rendering updates with the move.
		var native = (NativeObject*)self.Address;
		native->SetPosition(current.Position.X, current.Position.Y, current.Position.Z);
		native->SetRotation(current.Rotation);
		var matched = Vector3.DistanceSquared(self.Position, current.Position) < 1e-8f &&
			Math.Abs(PositionRules.AngleDelta(self.Rotation, current.Rotation)) < 1e-3f;
		if(matched || --ticks <= 0) {
			Cancel();
			Reply(matched ? $"Position synced with {current.Name}." : "Position sync did not finish. Try again while standing still.");
		}
	}
}
