using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Plugin.Services;
using Dalamud.IoC;
using Dalamud.Game.Text.SeStringHandling;

namespace AnimSync;

public sealed partial class AnimSync {
    [PluginService] public static IPartyList Party { get; private set; } = null!;
    private sealed record TimeChat(XivChatType Channel, string Text, string Name, uint World, long Received);
    private sealed record TimeJob(long Due, long Expires, ulong Self, uint Territory, string Phrase,
        (ulong Id, nint Address)[] Actors);
    private readonly ConcurrentQueue<TimeChat> timeChat = new();
    private TimeJob? timeJob;
    private (bool Party, ulong Target, nint Address, string Phrase)? timeSend;
    private long nextTimeSync, nextTimeSend;
    private void ClearTimeSync() {
        timeJob = null;
        timeSend = null;
        while(timeChat.TryDequeue(out _)) { }
    }
    private void OnTimeSyncChat(IHandleableChatMessage message) {
        if(disposed || !config.SameTimeSync) return;
        var channel = message.LogKind;
        if(channel is not (XivChatType.TellIncoming or XivChatType.TellOutgoing or XivChatType.Party or XivChatType.CrossParty)) return;
        var text = message.OriginalMessage.ExtractText();
        if(!TimeSyncRules.Matches(config.TimeSyncPhrase, text)) return;
        var player = SeString.Parse(message.OriginalSender.Data.Span).Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if(player == null) return; // Never guess an identity from a formatted name.
        if(timeChat.Count < 16)
            timeChat.Enqueue(new(channel, text, player.PlayerName, player.World.RowId, Environment.TickCount64));
    }
    private void QueueTimeSyncSend(bool party) {
        var target = Targets.Target as IPlayerCharacter;
        timeSend = (party, target?.GameObjectId ?? 0, target?.Address ?? 0, config.TimeSyncPhrase);
    }
    private unsafe void ProcessTimeSync(DateTime now) {
        var self = Objects.LocalPlayer;
        if(!config.SameTimeSync || !TimeSyncRules.ValidPhrase(config.TimeSyncPhrase) || self == null ||
            Conditions[ConditionFlag.BetweenAreas] || Conditions[ConditionFlag.BetweenAreas51]) {
            ClearTimeSync(); return;
        }
        var clock = Environment.TickCount64;
        if(timeSend is { } send) {
            timeSend = null;
            if(clock >= nextTimeSend && send.Phrase == config.TimeSyncPhrase) {
                string? command = null;
                if(send.Party) {
                    if(Party.Length > 1) command = "/party " + send.Phrase.Trim();
                    else Chat.Print("[AnimSync] Join a party to use Time Sync Party.");
                } else {
                    var target = Objects.OfType<IPlayerCharacter>().FirstOrDefault(p => p.GameObjectId == send.Target && p.Address == send.Address && p.ObjectIndex < 200);
                    if(target != null && target.GameObjectId != self.GameObjectId && target.HomeWorld.RowId != 0)
                        command = $"/tell {target.Name.TextValue}@{target.HomeWorld.Value.Name} {send.Phrase.Trim()}";
                    else Chat.Print("[AnimSync] Target another loaded player to send a time sync tell.");
                }
                if(command != null && UIModule.Instance() != null) {
                    nextTimeSend = clock + TimeSyncRules.RepeatDelayMs;
                    using var entry = new Utf8String(command);
                    UIModule.Instance()->ProcessChatBoxEntry(&entry);
                    // The outgoing chat event schedules the restart, including manually typed messages.
                }
            }
        }
        while(timeChat.TryDequeue(out var chat)) {
            if(clock - chat.Received > 2000 || clock < nextTimeSync || !TimeSyncRules.Matches(config.TimeSyncPhrase, chat.Text)) continue;
            var party = chat.Channel is XivChatType.Party or XivChatType.CrossParty;
            var selfKey = PlayerSoundKey(self);
            var otherKey = SoundSyncPolicy.PlayerKey(chat.Name, chat.World);
            if(otherKey == null) continue;
            var partyKeys = Party.Select(p => SoundSyncPolicy.PlayerKey(p.Name.TextValue, p.World.RowId)).ToHashSet();
            if(party && (!partyKeys.Contains(otherKey) || Party.Length < 2)) continue;
            var outgoing = chat.Channel == XivChatType.TellOutgoing || (party && otherKey == selfKey);
            var actors = Objects.OfType<IPlayerCharacter>()
                .Where(p => p.ObjectIndex < 200 && (party ? partyKeys.Contains(PlayerSoundKey(p)) :
                    p.GameObjectId == self.GameObjectId || PlayerSoundKey(p) == otherKey))
                .Select(p => (p.GameObjectId, p.Address)).ToArray();
            if(actors.Length < 2) continue;
            timeJob = new(TimeSyncRules.Due(chat.Received, outgoing), chat.Received + 2000,
                self.GameObjectId, Client.TerritoryType, config.TimeSyncPhrase, actors);
            nextTimeSync = clock + TimeSyncRules.RepeatDelayMs;
        }
        if(timeJob is not { } job) return;
        if(clock > job.Expires || self.GameObjectId != job.Self || Client.TerritoryType != job.Territory || job.Phrase != config.TimeSyncPhrase) {
            timeJob = null; return;
        }
        if(clock < job.Due) return;
        timeJob = null;
        // An explicit shared-phrase restart takes priority over timeline playback.
        if(job.Actors.Any(identity => TimelineOwns(identity.Id, identity.Address))) ResetTimeline();
        foreach(var identity in job.Actors) {
            var actor = Objects.OfType<IPlayerCharacter>().FirstOrDefault(p => p.GameObjectId == identity.Id && p.Address == identity.Address && p.ObjectIndex < 200);
            if(actor == null) continue;
            var character = (Character*)actor.Address;
            var draw = character->GameObject.DrawObject;
            if(draw == null || !draw->IsVisible || ((Actor*)actor.Address)->Control == null ||
                AnimationSampling.IsExcluded(character, ResolveTimelineKey, out var key)) continue;
            RestartActor(actor, ResolveCurrentTimeline(character, key));
            cooldowns[identity.Id] = now.AddSeconds(Math.Max(1, config.CooldownSeconds));
        }
        nextScan = now.AddSeconds(0.5);
    }
}
