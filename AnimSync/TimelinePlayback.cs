using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;

namespace AnimSync;

// All operations run on the framework thread, including guarded release on close/disposal.
internal sealed unsafe class TimelinePlayback {
    private sealed class Saved(nint address, nint binding, nint animation, float original) {
        public readonly nint Address = address, Binding = binding, Animation = animation;
        public readonly float Original = original;
        public float Applied = original;
    }
    public readonly ulong Id;
    public readonly nint Address, Body, Animation, Draw;
    public readonly int FrameCount;
    public readonly float Duration;
    private readonly List<Saved> controls = new();
    private (float Original, float Applied)? cueSpeed;
    public float? PausedAt { get; private set; }
    public float PlayRate => TimelineRules.PlaySpeed(controls.FirstOrDefault(c => c.Address == Body)?.Original ?? 1);
    public TimelinePlayback(IGameObject actor, AnimationSample sample) {
        Id = actor.GameObjectId;
        Address = actor.Address;
        Draw = (nint)((Character*)Address)->GameObject.DrawObject;
        var body = ((Actor*)Address)->Control;
        Body = (nint)body;
        Animation = body == null ? 0 : (nint)body->hkaAnimationControl.Binding.ptr->Animation.ptr;
        FrameCount = sample.FrameCount;
        Duration = sample.Duration;
        foreach(var pointer in AnimationRestart.ActiveControls((Character*)Address)) {
            var c = (hkaDefaultAnimationControl*)pointer;
            if(!float.IsFinite(c->PlaybackSpeed)) continue;
            controls.Add(new(pointer, (nint)c->hkaAnimationControl.Binding.ptr,
                (nint)c->hkaAnimationControl.Binding.ptr->Animation.ptr, c->PlaybackSpeed));
        }
    }
    public bool IsSameActor(IGameObject actor) => actor.Address == Address && actor.GameObjectId == Id;
    public bool IsActor(IGameObject actor) => IsSameActor(actor) &&
        (nint)((Character*)actor.Address)->GameObject.DrawObject == Draw;
    public bool Valid(IGameObject actor, AnimationSample sample) {
        if(!IsActor(actor) || sample.FrameCount != FrameCount || Math.Abs(sample.Duration - Duration) >= .001f) return false;
        var c = ((Actor*)actor.Address)->Control;
        return c != null && (nint)c == Body && (nint)c->hkaAnimationControl.Binding.ptr->Animation.ptr == Animation;
    }
    private static bool SameControl(Saved saved, HashSet<nint> live) {
        if(!live.Contains(saved.Address)) return false;
        var c = (hkaDefaultAnimationControl*)saved.Address;
        return (nint)c->hkaAnimationControl.Binding.ptr == saved.Binding &&
            (nint)c->hkaAnimationControl.Binding.ptr->Animation.ptr == saved.Animation;
    }
    public void Apply(float time, bool play, float bodyRate, float multiplier, bool syncCues) {
        PausedAt = play ? null : time;
        SetCueRate(syncCues ? (play ? multiplier : 0) : 1);
        ApplyAtTime(time, play, bodyRate, multiplier);
    }
    public void HoldPause(bool syncCues) {
        if(PausedAt is not { } time) return;
        SetCueRate(syncCues ? 0 : 1);
        ApplyAtTime(time, false, 0);
    }
    public void HoldSpeed(float bodyRate, float multiplier, bool syncCues) {
        SetCueRate(syncCues ? multiplier : 1);
        ApplyAtTime(null, true, bodyRate, multiplier);
    }
    public void ApplyLinked(float time, bool playing, bool syncCues) {
        PausedAt = null;
        SetCueRate(syncCues && !playing ? 0 : 1);
        ApplyAtTime(time, false, 0);
    }
    private void SetCueRate(float multiplier) {
        if(multiplier == 1) {
            ReleaseCueSpeed();
            return;
        }
        var sequencer = &((Character*)Address)->Timeline.TimelineSequencer;
        if(sequencer->GetSchedulerTimeline(0) == null) return;
        var current = sequencer->TimelineSpeeds[0];
        if(cueSpeed is { } saved && current != saved.Applied && current != saved.Original) return;
        var original = cueSpeed?.Original ?? current;
        if(!float.IsFinite(original) || original <= 0) return;
        var speed = original * multiplier;
        if(current != speed) sequencer->SetSlotSpeed(0, speed);
        cueSpeed = (original, speed);
    }
    private void ReleaseCueSpeed() {
        if(cueSpeed is not { } speed) return;
        cueSpeed = null;
        var sequencer = &((Character*)Address)->Timeline.TimelineSequencer;
        if(TimelineRules.CanRestore(sequencer->TimelineSpeeds[0], speed.Applied))
            sequencer->SetSlotSpeed(0, speed.Original);
    }
    private void ApplyAtTime(float? time, bool play, float bodyRate, float multiplier = 1) {
        var live = AnimationRestart.ActiveControls((Character*)Address).ToHashSet();
        foreach(var saved in controls) {
            if(!SameControl(saved, live)) continue;
            var c = (hkaDefaultAnimationControl*)saved.Address;
            var duration = c->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration;
            if(!float.IsFinite(duration) || duration <= 0) continue;
            if(time is { } position) c->hkaAnimationControl.LocalTime = Math.Clamp(position, 0, duration);
            c->PlaybackSpeed = saved.Applied = play ?
                (saved.Address == Body ? bodyRate : TimelineRules.PlaySpeed(saved.Original)) * multiplier : 0;
        }
    }
    public void Release(IGameObject actor) {
        if(!IsSameActor(actor)) return;
        ReleaseCueSpeed();
        if(!IsActor(actor)) return;
        var live = AnimationRestart.ActiveControls((Character*)actor.Address).ToHashSet();
        foreach(var saved in controls) {
            if(!SameControl(saved, live)) continue;
            var c = (hkaDefaultAnimationControl*)saved.Address;
            // Do not overwrite a newer playback-speed change made by the game or another tool.
            if(TimelineRules.CanRestore(c->PlaybackSpeed, saved.Applied)) c->PlaybackSpeed = saved.Original;
        }
    }
}
