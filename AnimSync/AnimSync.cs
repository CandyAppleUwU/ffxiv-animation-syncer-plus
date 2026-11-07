// Modified: settings UI and selective, frame-based nearby player synchronization.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json;

namespace AnimSync;

public sealed partial class AnimSync : IDalamudPlugin {
	[PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
	[PluginService] public static IFramework Framework { get; private set; } = null!;
	[PluginService] public static ICommandManager Commands { get; private set; } = null!;
	[PluginService] public static IObjectTable Objects { get; private set; } = null!;
	[PluginService] public static IChatGui Chat { get; private set; } = null!;
	[PluginService] public static IPluginLog Logger { get; private set; } = null!;
	[PluginService] public static IDataManager Data { get; private set; } = null!;
	[PluginService] public static IGameGui GameGui { get; private set; } = null!;
	[PluginService] public static ITargetManager Targets { get; private set; } = null!;
	[PluginService] public static ICondition Conditions { get; private set; } = null!;
	[PluginService] public static IClientState Client { get; private set; } = null!;
	private readonly PositionAlignment positionAlignment;
	private bool positionRequested;
	private readonly Dictionary<ushort, string> timelineKeys = new();
	private readonly Settings config;
	private long audioSettingsSaveAt;
	private readonly WindowSystem windows = new("AnimSync");
	private readonly SettingsWindow settingsWindow;
	private readonly Dictionary<ulong, DateTime> cooldowns = new();
	private readonly List<AnimationSample> samples = new();
	private readonly Dictionary<string, string> detectedPlayers = new();
	private DateTime nextScan;
	private bool restartRequested, bodyRequested, nearbyRequested;
	private bool nearYouRequested;
	private (ulong Id, nint Address)? targetSyncRequest;
	private ICallGateSubscriber<nint, int, object?>? redrawSubscriber;
	private DateTime nextRedrawSubscribe;
	private volatile bool disposed;
	private readonly ConcurrentQueue<(nint Address, int Index)> redrawEvents = new();
	private readonly Dictionary<int, PendingRedraw> pendingRedraws = new();
	private sealed record PendingRedraw(nint Address, ulong Id, DateTime Expires);

	public AnimSync() {
		// Read old untyped JSON too, preserving the original plugin's Enabled setting.
		try {
			config = Interface.ConfigFile.Exists
				? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(Interface.ConfigFile.FullName)) ?? new Settings()
				: new Settings();
		} catch(Exception ex) when(ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) {
			Logger.Warning(ex, "Could not read Animation Syncer Plus settings; using defaults.");
			config = new Settings();
		}
		config.Normalize();
		positionAlignment = new PositionAlignment(Objects, Targets, Client, Conditions, Chat);
		settingsWindow = new SettingsWindow(this);
		windows.AddWindow(settingsWindow);
		timelineWindow = new TimelineWindow(this);
		windows.AddWindow(timelineWindow);
		Interface.UiBuilder.DisableGposeUiHide = true;
		Interface.UiBuilder.DisableCutsceneUiHide = true;
		Interface.UiBuilder.Draw += windows.Draw;
		Interface.UiBuilder.OpenConfigUi += OpenSettings;
		Interface.UiBuilder.OpenMainUi += OpenSettings;
		Framework.Update += Tick;
		Chat.ChatMessage += OnTimeSyncChat;
		Commands.AddHandler("/animsync", new CommandInfo(OnCommand) {
			HelpMessage = "'ui'/'settings': menu; 'timeline': animation player; 'groups': sync groups; 'everyone': sync everyone; 'near': sync near you; 'target': sync you and target; 'pos'/'align': match position/facing within 0.5y; 'toggle': auto sync; 'body': body reset.",
		});
	}
	public void Dispose() {
		disposed = true;
		Framework.Update -= Tick;
		void CleanupPlayback() { ResetTimeline(); DisposeTimelineMusic(); }
		if(Framework.IsInFrameworkUpdateThread) CleanupPlayback();
		else if(!Framework.IsFrameworkUnloading) {
			try { Framework.RunOnFrameworkThread(CleanupPlayback).GetAwaiter().GetResult(); }
			catch(OperationCanceledException) { DisposeTimelineMusic(false); }
		}
		else DisposeTimelineMusic(false);
		Chat.ChatMessage -= OnTimeSyncChat;
		ClearTimeSync();
		positionAlignment.Cancel();
		redrawSubscriber?.Unsubscribe(OnCharacterRedrawn);
		pendingRedraws.Clear();
		while(redrawEvents.TryDequeue(out _)) { }
		Framework.Update -= Tick;
		Interface.UiBuilder.Draw -= windows.Draw;
		Interface.UiBuilder.OpenConfigUi -= OpenSettings;
		Interface.UiBuilder.OpenMainUi -= OpenSettings;
		Commands.RemoveHandler("/animsync");
		windows.RemoveAllWindows();
		cooldowns.Clear();
		samples.Clear();
		detectedPlayers.Clear();
		if(audioSettingsSaveAt != 0) Save();
	}
	private void OpenSettings() => settingsWindow.IsOpen = true;
	private void DrawDistancePreview() {
		var player = Objects.LocalPlayer;
		if(player == null) return;
		var center = player.Position + new Vector3(0, 0.05f, 0);
		var radius = config.Distance;
		var draw = ImGui.GetBackgroundDrawList();
		var color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.9f, 1f, 1f));
		var outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.8f));
		Vector2 previous = default;
		var previousVisible = false;
		const int segments = 96;
		for(var i = 0; i <= segments; i++) {
			var angle = MathF.Tau * i / segments;
			var point = center + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
			var visible = GameGui.WorldToScreen(point, out var screen) && float.IsFinite(screen.X) && float.IsFinite(screen.Y);
			if(visible && previousVisible) {
				draw.AddLine(previous, screen, outline, 4f);
				draw.AddLine(previous, screen, color, 2f);
			}
			previous = screen;
			previousVisible = visible;
		}
	}
	private void ScheduleAudioSettingsSave() => audioSettingsSaveAt = Environment.TickCount64 + 300;
	private void Save() { config.Normalize(); Interface.SavePluginConfig(config); audioSettingsSaveAt = 0; }
	private static bool IsAutomaticSyncPaused => Conditions[ConditionFlag.WatchingCutscene] ||
		Conditions[ConditionFlag.WatchingCutscene78] || Conditions[ConditionFlag.OccupiedInCutSceneEvent] || Client.IsGPosing;
	private static bool IsManualPlayer(IGameObject obj) => obj.ObjectKind == ObjectKind.Pc &&
		SyncScopeRules.IncludesPlayer(obj.ObjectIndex, Client.IsGPosing);
	private static IGameObject? ManualSelf() {
		var self = Objects.LocalPlayer;
		return !Client.IsGPosing || self == null ? self : Objects.FirstOrDefault(p => IsManualPlayer(p) &&
			p.Name.TextValue == self.Name.TextValue);
	}
	private static string? PlayerSoundKey(IGameObject obj) => obj is IPlayerCharacter p
		? SoundSyncPolicy.PlayerKey(p.Name.TextValue, p.HomeWorld.RowId) : null;
	private bool CanSyncSound(IGameObject obj) => SoundSyncPolicy.CanSync(config.SyncSound,
		obj.ObjectKind == ObjectKind.Pc, PlayerSoundKey(obj), config.DjPlayers);
	private unsafe int RestartActor(IGameObject obj, ushort baseTimeline = 0, bool resumePaused = false) =>
		AnimationRestart.Restart((Character*)obj.Address, CanSyncSound(obj), baseTimeline, resumePaused);
	private string ResolveTimelineKey(ushort id) {
		if(timelineKeys.TryGetValue(id, out var key)) return key;
		key = Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>().TryGetRow(id, out var row) ? row.Key.ToString() : string.Empty;
		timelineKeys[id] = key;
		return key;
	}
	private void OnCommand(string command, string args) {
		switch(args.Trim().ToLowerInvariant()) {
			case "": case "everyone": restartRequested = true; break;
			case "body": bodyRequested = true; break;
			case "nearby": case "groups": nearbyRequested = true; break;
			case "near": nearYouRequested = true; break;
			case "pos": case "align": positionRequested = true; break;
			case "target":
				if((Client.IsGPosing ? Targets.GPoseTarget : Targets.Target) is { } target && IsManualPlayer(target))
					targetSyncRequest = (target.GameObjectId, target.Address);
				else Chat.Print("[AnimSync] Select another player to sync with.");
				break;
			case "ui": case "settings": settingsWindow.IsOpen = !settingsWindow.IsOpen; break;
			case "timeline": timelineWindow.IsOpen = !timelineWindow.IsOpen; break;
			case "toggle": config.Enabled = !config.Enabled; Save(); break;
			default: Chat.Print("Usage: /animsync [ui|settings|timeline|groups|everyone|near|target|pos|align|toggle|body]"); break;
		}
	}
	private unsafe void Tick(IFramework framework) {
		if(disposed) return;
		if(audioSettingsSaveAt != 0 && Environment.TickCount64 >= audioSettingsSaveAt) Save();
		ProcessTimeline();
		ProcessTimelineMusic();
		if(positionRequested) { positionRequested = false; positionAlignment.Request(); }
		positionAlignment.Update();
		var now = DateTime.UtcNow;
		var paused = IsAutomaticSyncPaused;
		ProcessTimeSync(now);
		ProcessRedrawRecovery(now, paused);
		if(targetSyncRequest is { } requested) {
			targetSyncRequest = null;
			SyncTarget(requested.Id, requested.Address, now);
			return;
		}
		if(restartRequested || bodyRequested || nearYouRequested) {
			var nearOnly = nearYouRequested && !restartRequested && !bodyRequested;
			var self = ManualSelf();
			if(nearOnly && self == null) {
				nearYouRequested = false;
				Chat.Print("[AnimSync] No local player is loaded.");
				return;
			}
			var center = self?.Position ?? default;
			foreach(var obj in Objects) {
				if(obj.ObjectKind != ObjectKind.Pc && obj.ObjectKind != ObjectKind.BattleNpc) continue;
				if(Client.IsGPosing && !SyncScopeRules.IncludesPlayer(obj.ObjectIndex, true)) continue;
				if(nearOnly && (!IsManualPlayer(obj) ||
					Vector3.DistanceSquared(center, obj.Position) > config.Distance * config.Distance)) continue;
				if(nearOnly) {
					var draw = ((Character*)obj.Address)->GameObject.DrawObject;
					if(draw == null || !draw->IsVisible) continue;
				}
				if(AnimationSampling.IsExcluded((Character*)obj.Address, ResolveTimelineKey, out _)) continue;
				var actor = (Actor*)obj.Address;
				if(actor->Control == null) continue;
				if(restartRequested || nearOnly) RestartActor(obj);
				else actor->Control->hkaAnimationControl.LocalTime = 0;
				cooldowns[obj.GameObjectId] = now.AddSeconds(config.CooldownSeconds);
			}
			restartRequested = bodyRequested = nearYouRequested = false;
			nextScan = now.AddSeconds(0.5);
			return;
		}
		if(paused && !nearbyRequested) {
			samples.Clear();
			nextScan = default;
			return;
		}
		if(now < nextScan && !nearbyRequested) return;
		nextScan = now.AddSeconds(Math.Min(0.1, config.CooldownSeconds));
		var manualGroups = nearbyRequested;
		var shouldSync = SyncScopeRules.ShouldScan(paused, config.Enabled, manualGroups);
		nearbyRequested = false;
		samples.Clear();
		detectedPlayers.Clear();
		var live = new Dictionary<ulong, IGameObject>();
		foreach(var obj in Objects) {
			if(obj.ObjectKind != ObjectKind.Pc || !(manualGroups ? IsManualPlayer(obj) : obj.ObjectIndex < 200)) continue;
			if(!manualGroups && TimelineOwns(obj)) continue;
			if(obj is IPlayerCharacter p && PlayerSoundKey(obj) is { } playerKey)
				detectedPlayers[playerKey] = $"{p.Name.TextValue}@{p.HomeWorld.Value.Name}";
			if(!AnimationSampling.TryRead(obj, ResolveTimelineKey, out var sample, out _)) continue;
			// GPose copies may reuse world IDs; the address uniquely identifies each loaded copy.
			if(Client.IsGPosing) sample = sample with { Id = (ulong)obj.Address };
			samples.Add(sample);
			live[sample.Id] = obj;
		}
		foreach(var id in new List<ulong>(cooldowns.Keys))
			if(cooldowns[id] <= now) cooldowns.Remove(id);
		if(!shouldSync) return;
		foreach(var group in NearbyGroups.Find(samples, config.Distance)) {
			var cooling = false;
			foreach(var sample in group)
				if(cooldowns.ContainsKey(sample.Id)) cooling = true;
			if(cooling) continue;
			// Snapshot group membership before changing any native playback.
			var leader = config.MatchLeadingPlayer ? NearbyGroups.Leader(group) : (AnimationSample?)null;
			foreach(var sample in group) {
				if(leader is { } ahead) {
					if(sample.Id != ahead.Id) {
						var actor = live[sample.Id];
						var cueLeader = CanSyncSound(actor) ? (Character*)live[ahead.Id].Address : null;
						AnimationRestart.MatchTime((Character*)actor.Address, ahead.LocalTime, cueLeader);
					}
				} else RestartActor(live[sample.Id]);
				cooldowns[sample.Id] = now.AddSeconds(config.CooldownSeconds);
			}
		}
	}
	private unsafe void SyncTarget(ulong targetId, nint targetAddress, DateTime now) {
		var self = ManualSelf();
		if(self == null) { Chat.Print("[AnimSync] No local player is loaded."); return; }
		var target = Objects.FirstOrDefault(o => o.GameObjectId == targetId && o.Address == targetAddress &&
			IsManualPlayer(o));
		if(target == null || target.Address == self.Address) {
			Chat.Print("[AnimSync] Select another loaded player to sync with.");
			return;
		}
		// Validate both before changing either actor, and capture only the chosen pair.
		var selfActor = (Character*)self.Address;
		var targetActor = (Character*)target.Address;
		if(selfActor->GameObject.DrawObject == null || !selfActor->GameObject.DrawObject->IsVisible ||
			targetActor->GameObject.DrawObject == null || !targetActor->GameObject.DrawObject->IsVisible ||
			((Actor*)self.Address)->Control == null || ((Actor*)target.Address)->Control == null) {
			Chat.Print("[AnimSync] Both players must finish loading before syncing.");
			return;
		}
		if(AnimationSampling.IsExcluded(selfActor, ResolveTimelineKey, out var selfKey) ||
			AnimationSampling.IsExcluded(targetActor, ResolveTimelineKey, out var targetKey)) {
			Chat.Print("[AnimSync] One of the current animations is excluded from syncing.");
			return;
		}
		var selfTimeline = ResolveCurrentTimeline(selfActor, selfKey);
		var targetTimeline = ResolveCurrentTimeline(targetActor, targetKey);
		RestartActor(self, selfTimeline);
		RestartActor(target, targetTimeline);
		cooldowns[self.GameObjectId] = cooldowns[target.GameObjectId] = now.AddSeconds(config.CooldownSeconds);
		nextScan = now.AddSeconds(0.5);
	}
	private void OnCharacterRedrawn(nint address, int index) {
		// IPC callbacks only queue identifiers; native reads stay on the framework thread.
		if(!disposed && index >= 0 && index < 200) redrawEvents.Enqueue((address, index));
	}
	private unsafe void ProcessRedrawRecovery(DateTime now, bool paused) {
		if(redrawSubscriber == null && now >= nextRedrawSubscribe) {
			nextRedrawSubscribe = now.AddSeconds(5);
			ICallGateSubscriber<nint, int, object?>? candidate = null;
			try {
				candidate = Interface.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn");
				candidate.Subscribe(OnCharacterRedrawn);
				redrawSubscriber = candidate;
			} catch(Exception ex) {
				candidate?.Unsubscribe(OnCharacterRedrawn);
				Logger.Debug(ex, "Penumbra redraw subscription unavailable; retrying later.");
			}
		}
		while(redrawEvents.TryDequeue(out var e)) {
			if(paused || !config.SyncSound) continue;
			var obj = Objects[e.Index];
			if(obj == null || obj.Address != e.Address || obj.ObjectKind != ObjectKind.Pc) continue;
			// Coalesce same-update notifications without adding an artificial delay.
			pendingRedraws[e.Index] = new(e.Address, obj.GameObjectId, now.AddSeconds(5));
		}
		if(paused || !config.SyncSound) { pendingRedraws.Clear(); return; }
		foreach(var pair in new List<KeyValuePair<int, PendingRedraw>>(pendingRedraws)) {
			var pending = pair.Value;
			var obj = Objects[pair.Key];
			if(obj != null && TimelineOwns(obj)) { pendingRedraws.Remove(pair.Key); continue; }
			if(obj == null || obj.Address != pending.Address || obj.GameObjectId != pending.Id || obj.ObjectKind != ObjectKind.Pc || now > pending.Expires) {
				pendingRedraws.Remove(pair.Key);
				continue;
			}
			// Check readiness on every framework update, bounded to five seconds.
			if(!CanSyncSound(obj)) { pendingRedraws.Remove(pair.Key); continue; }
			var character = (Character*)obj.Address;
			var draw = character->GameObject.DrawObject;
			if(draw == null || !draw->IsVisible || ((Actor*)obj.Address)->Control == null) continue;
			var excluded = AnimationSampling.IsExcluded(character, ResolveTimelineKey, out var key);
			if(string.IsNullOrEmpty(key)) continue;
			if(excluded) { pendingRedraws.Remove(pair.Key); continue; }
			var timeline = ResolveCurrentTimeline(character, key);
			if(timeline == 0) continue;
			if(RestartActor(obj, timeline) == 0) continue;
			pendingRedraws.Remove(pair.Key);
			cooldowns[pending.Id] = now.AddSeconds(config.CooldownSeconds);
		}
	}
	private unsafe ushort ResolveCurrentTimeline(Character* character, string key) {
		var timeline = character->Timeline.TimelineSequencer.TimelineIds[0];
		if(timeline != 0) return timeline;
		var normalized = key.Replace('\\', '/');
		if(normalized.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
		var emoteDirectory = normalized.IndexOf("emote/", StringComparison.OrdinalIgnoreCase);
		if(emoteDirectory >= 0) normalized = normalized[emoteDirectory..];
		foreach(var row in Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>()) {
			if(row.RowId <= ushort.MaxValue && row.Key.ToString().Equals(normalized, StringComparison.OrdinalIgnoreCase))
				return (ushort)row.RowId;
		}
		return 0;
	}
	[Serializable]
	public sealed class Settings : IPluginConfiguration {
		public int Version { get; set; } = 6;
		public bool Enabled = true;
		public bool SyncSound = true;
		public bool MatchLeadingPlayer;
		public bool SameTimeSync;
		public string TimeSyncPhrase = "Match anim with me c:";
		public Dictionary<string, string> DjPlayers = new();
		public float Distance = 0.60f;
		public float CooldownSeconds = 3f;
		public float[] MusicEqualizerGains = new float[MusicEqualizer.Frequencies.Length];
		public float MusicReverbAmount;
		public float? TimelineMusicVolume;
		public void Normalize() {
			Version = 6;
			TimeSyncPhrase ??= string.Empty;
			DjPlayers ??= new();
			MusicEqualizerGains = MusicEqualizer.Normalize(MusicEqualizerGains);
			MusicReverbAmount = MusicVolume.Clamp(MusicReverbAmount);
			if(TimelineMusicVolume is { } gain) TimelineMusicVolume = MusicVolume.ClampGain(gain);
			Distance = float.IsFinite(Distance) ? Math.Clamp(Distance, 0.1f, 2f) : 0.6f;
			CooldownSeconds = float.IsFinite(CooldownSeconds) ? Math.Clamp(CooldownSeconds, 0f, 5f) : 3f;
		}
	}
	private sealed class SettingsWindow : Window {
		private readonly AnimSync plugin;
		private double previewUntil;
		private string playerSearch = string.Empty;
		private string? hoveredButton;
		private double buttonHoverStart;
		public SettingsWindow(AnimSync plugin) : base("Animation Syncer Plus###AnimSyncSettings", ImGuiWindowFlags.AlwaysAutoResize) {
			this.plugin = plugin;
			SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(400, 0), MaximumSize = new Vector2(600, 900) };
		}
		public override void Draw() {
			const string timelineLabel = "Animation Timeline";
			var style = ImGui.GetStyle();
			var timelineWidth = ImGui.CalcTextSize(timelineLabel).X + style.FramePadding.X * 2;
			var discordWidth = ImGui.CalcTextSize("Discord").X + style.FramePadding.X * 2;
			var leftEdge = ImGui.GetCursorPosX();
			var rowWidth = ImGui.GetContentRegionAvail().X;
			var discordStart = leftEdge + rowWidth - discordWidth;
			var c = plugin.config;
			var changed = ImGui.Checkbox("Auto Sync Anims", ref c.Enabled);
			ImGui.SameLine();
			ImGui.TextDisabled("(?)");
			if(ImGui.IsItemHovered()) {
				ImGui.BeginTooltip();
				ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
				ImGui.TextUnformatted("How it works:");
				ImGui.Spacing();
				ImGui.TextWrapped("1. Checks for players near each other within your selected distance.");
				ImGui.TextWrapped("2. Checks that their animations have the same total frames, duration and playback speed.");
				ImGui.TextWrapped("3. If they are on different frames, syncs their animations together.");
				ImGui.Spacing();
				ImGui.TextWrapped("With this enabled, you should rarely need to use animsync manually.");
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}
			ImGui.SameLine();
			var timelineCenter = leftEdge + (rowWidth - timelineWidth) / 2;
			ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), Math.Min(timelineCenter, discordStart - style.ItemSpacing.X - timelineWidth)));
			ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.08f, .40f, .34f, 1));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.08f, .49f, .42f, 1));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.06f, .32f, .27f, 1));
			ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
			if(ImGui.Button(timelineLabel)) plugin.timelineWindow.IsOpen = !plugin.timelineWindow.IsOpen;
			ImGui.PopStyleColor(4);
			ImGui.SameLine();
			ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), discordStart));
			ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(88f / 255, 101f / 255, 242f / 255, 1));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(71f / 255, 82f / 255, 196f / 255, 1));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(60f / 255, 69f / 255, 165f / 255, 1));
			ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
			if(ImGui.Button("Discord")) Util.OpenLink("https://discord.com/invite/bKswgraKvh");
			ImGui.PopStyleColor(4);
			changed |= ImGui.Checkbox("Sync sound", ref c.SyncSound);
			var sliderWidth = Math.Max(80, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Check Refresh Time").X
				- ImGui.CalcTextSize("(?)").X - ImGui.GetStyle().ItemInnerSpacing.X - ImGui.GetStyle().ItemSpacing.X * 2);
			ImGui.SetNextItemWidth(sliderWidth);
			var distanceChanged = ImGui.SliderFloat("Distance", ref c.Distance, 0.1f, 2f, "%.2f");
			if(distanceChanged) c.Normalize();
			changed |= distanceChanged;
			if(ImGui.IsItemActive() || distanceChanged) previewUntil = ImGui.GetTime() + 1.5;
			if(ImGui.GetTime() < previewUntil) plugin.DrawDistancePreview();
			Help("Maximum distance between players for group sync, measured in yalms. Sync Near You uses this radius around you. Adjust it to preview the range circle.");
			ImGui.SetNextItemWidth(sliderWidth);
			changed |= ImGui.SliderFloat("Check Refresh Time", ref c.CooldownSeconds, 0f, 5f, "%.2f s");
			Help("Minimum wait before automatically syncing the same players again. At 0, checks run every game update with no added cooldown.");
			if(ImGui.Button("Sync Groups")) plugin.nearbyRequested = true;
			ButtonHelp("Sync Groups", "Syncs nearby groups whose animations have matching frame counts and timing but are on different frames.");
			ImGui.SameLine();
			if(ImGui.Button("Sync Everyone")) plugin.restartRequested = true;
			ButtonHelp("Sync Everyone", "Restarts all loaded, allowed animations regardless of distance or matching frames. DJ sound exclusions still apply.");
			ImGui.SameLine();
			if(ImGui.Button("Sync Near You")) plugin.nearYouRequested = true;
			ButtonHelp("Sync Near You", "Syncs your animation and players inside your Distance radius, without requiring matching frames.");
			ImGui.SameLine();
			if(ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize("Sync Target").X + ImGui.GetStyle().FramePadding.X * 2)
				ImGui.NewLine();
			if(ImGui.Button("Sync Target")) plugin.OnCommand("/animsync", "target");
			ButtonHelp("Sync Target", "Syncs only your animation and your selected player's animation, regardless of distance or matching frames.");
			var positionBlock = plugin.positionAlignment.BlockReason;
			ImGui.BeginDisabled(positionBlock != null);
			if(ImGui.Button("Sync Position")) plugin.positionRequested = true;
			ButtonHelp("Sync Position", positionBlock ?? "Matches your position and facing to your target within 0.5 yalms. With no target, uses the closest player within 0.5 yalms.");
			ImGui.EndDisabled();
			changed |= ImGui.Checkbox("Perfect Frame Sync", ref c.MatchLeadingPlayer);
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.25f, 0.25f, 1f), "(Experimental)");
			Help("Matches the animation frame to the person furthest ahead. Applies to Auto Sync and Sync Groups. With Sync Sound enabled, matching native cue timelines can align short effects without restarting music.");
			changed |= ImGui.Checkbox("Same time sync", ref c.SameTimeSync);
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.25f, 0.25f, 1f), "(Experimental)");
			Help("Enable on each participant's client with exactly the same phrase (case-sensitive). Sending the phrase manually or with the buttons restarts animations on each client. Party syncs loaded party members; tells sync you and the other player. The sender waits 300 ms to approximate delivery time; latency varies. Sound and DJ settings apply. Perfect Frame Sync does not apply to this restart.");
			if(c.SameTimeSync) {
				ImGui.SetNextItemWidth(-1);
				changed |= ImGui.InputTextWithHint("##TimeSyncPhrase", "Shared phrase...", ref c.TimeSyncPhrase, 181);
				if(!TimeSyncRules.ValidPhrase(c.TimeSyncPhrase)) ImGui.TextWrapped("Enter up to 180 UTF-8 bytes without control characters or chat placeholders (<...>).");
				ImGui.BeginDisabled(!TimeSyncRules.ValidPhrase(c.TimeSyncPhrase));
				if(ImGui.Button("Time Sync Party")) plugin.QueueTimeSyncSend(true);
				ButtonHelp("Time Sync Party", "Sends your shared phrase to party chat. Participants need Same time sync enabled with the same phrase.");
				ImGui.SameLine();
				if(ImGui.Button("Time Sync Target")) plugin.QueueTimeSyncSend(false);
				ButtonHelp("Time Sync Target", "Sends your shared phrase in a tell to your targeted player. Both clients need Same time sync enabled with the same phrase.");
				ImGui.EndDisabled();
			}
			if(changed) { plugin.ClearTimeSync(); plugin.Save(); }
			ImGui.Separator();
			if(ImGui.CollapsingHeader("DJs (Sound Sync Exclusions)")) {
				ImGui.TextWrapped("Keeps DJs music uninterrupted when animations sync. This is only really needed when anim syncing everyone.");
				ImGui.SetNextItemWidth(-1);
				ImGui.InputTextWithHint("##DjSearch", "Search players...", ref playerSearch, 128);
				var target = Targets.Target;
				var targetKey = target == null ? null : PlayerSoundKey(target);
				var targetIsDj = targetKey != null && c.DjPlayers.ContainsKey(targetKey);
				ImGui.BeginDisabled(targetKey == null || targetIsDj);
				if(ImGui.Button("Add target") && target is IPlayerCharacter targetPlayer && targetKey != null) {
					c.DjPlayers[targetKey] = $"{targetPlayer.Name.TextValue}@{targetPlayer.HomeWorld.Value.Name}";
					plugin.Save();
					playerSearch = string.Empty;
				}
				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.TextDisabled(targetIsDj ? "Target is already a DJ" : targetKey == null ? "Target a player to add them" : target!.Name.TextValue);
				var players = new Dictionary<string, string>(c.DjPlayers);
				foreach(var entry in plugin.detectedPlayers) players[entry.Key] = entry.Value;
				var query = playerSearch.Trim();
				var matching = players.Where(e => e.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
					.OrderByDescending(e => c.DjPlayers.ContainsKey(e.Key))
					.ThenBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ToList();
				ImGui.BeginChild("DjPlayers", new Vector2(0, 200));
				if(ImGui.BeginTable("DjTable", 2, ImGuiTableFlags.SizingStretchProp)) {
					ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
					ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90);
					foreach(var entry in matching) {
						ImGui.PushID(entry.Key);
						var isDj = c.DjPlayers.ContainsKey(entry.Key);
						ImGui.TableNextRow();
						ImGui.TableSetColumnIndex(0);
						if(isDj) ImGui.TextColored(new Vector4(0.25f, 1f, 0.35f, 1f), entry.Value);
						else ImGui.TextUnformatted(entry.Value);
						ImGui.TableSetColumnIndex(1);
						if(isDj) {
							ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.65f, 0.1f, 0.1f, 1f));
							ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.15f, 0.15f, 1f));
							ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.05f, 0.05f, 1f));
						}
						if(ImGui.Button(isDj ? "(Remove)" : "(ADD DJ)", new Vector2(-1, 0))) {
							if(isDj) c.DjPlayers.Remove(entry.Key);
							else c.DjPlayers[entry.Key] = entry.Value;
							plugin.Save();
						}
						if(isDj) ImGui.PopStyleColor(3);
						ImGui.PopID();
					}
					ImGui.EndTable();
				}
				if(matching.Count == 0) ImGui.TextUnformatted(players.Count == 0 ? "No player identities loaded yet." : "No matching players.");
				ImGui.EndChild();
			}
		}
		private void ButtonHelp(string id, string text) {
			if(!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
				if(hoveredButton == id) hoveredButton = null;
				return;
			}
			if(hoveredButton != id) { hoveredButton = id; buttonHoverStart = ImGui.GetTime(); }
			if(ImGui.GetTime() - buttonHoverStart < 1) return;
			ImGui.BeginTooltip();
			ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
			ImGui.TextWrapped(text);
			ImGui.PopTextWrapPos();
			ImGui.EndTooltip();
		}
		public static void Help(string text) {
			ImGui.SameLine();
			ImGui.TextDisabled("(?)");
			if(!ImGui.IsItemHovered()) return;
			ImGui.BeginTooltip();
			ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
			ImGui.TextWrapped(text);
			ImGui.PopTextWrapPos();
			ImGui.EndTooltip();
		}
	}
}
