using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;

namespace AnimSync;

// All operations run on the framework thread, including guarded release on close/disposal.
internal sealed unsafe class TimelinePlayback {
    private sealed class Saved(nint address, nint binding, nint animation, float original, bool isProp = false) {
        public readonly nint Address = address, Binding = binding, Animation = animation;
        public readonly float Original = original;
        public readonly bool IsProp = isProp;
        public float Applied = original;
    }
    public readonly ulong Id;
    public readonly nint Address, Body, Animation, Draw;
    public readonly int FrameCount;
    public readonly float Duration;
    private readonly List<Saved> controls = new();
    private readonly IReadOnlySet<ushort>? propModels;
    public bool HasProps => propModels is { Count: > 0 };
    private (float Original, float Applied)? cueSpeed;
    public float? PausedAt { get; private set; }
    public float PlayRate => TimelineRules.PlaySpeed(controls.FirstOrDefault(c => c.Address == Body)?.Original ?? 1);
    public TimelinePlayback(IGameObject actor, AnimationSample sample, IReadOnlySet<ushort>? propModels) {
        this.propModels = propModels;
        Id = actor.GameObjectId;
        Address = actor.Address;
        Draw = (nint)((Character*)Address)->GameObject.DrawObject;
        var body = ((Actor*)Address)->Control;
        Body = (nint)body;
        Animation = body == null ? 0 : (nint)body->hkaAnimationControl.Binding.ptr->Animation.ptr;
        FrameCount = sample.FrameCount;
        Duration = sample.Duration;
        CaptureBodyControls(AnimationRestart.ActiveControls((Character*)Address, includeFadingOut: true).ToHashSet());
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
    private void CaptureBodyControls(HashSet<nint> live) {
        // During a handoff the sampled body can still be fading out, or its binding can be replaced in place.
        controls.RemoveAll(saved => !saved.IsProp && !SameControl(saved, live));
        foreach(var pointer in live) {
            var control = (hkaDefaultAnimationControl*)pointer;
            if(!float.IsFinite(control->PlaybackSpeed) || controls.Any(saved => saved.Address == pointer)) continue;
            if(pointer != Body && control->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasingOut) continue;
            controls.Add(new(pointer, (nint)control->hkaAnimationControl.Binding.ptr,
                (nint)control->hkaAnimationControl.Binding.ptr->Animation.ptr, control->PlaybackSpeed));
        }
    }
    public void Apply(float? time, float currentTime, bool play, float bodyRate, float multiplier, bool syncCues) {
        PausedAt = play ? null : time ?? currentTime;
        SetCueRate(syncCues ? (play ? multiplier : 0) : 1);
        ApplyAtTime(PausedAt ?? time, play, bodyRate, multiplier, currentTime);
    }
    public void HoldPause(bool syncCues) {
        if(PausedAt is not { } time) return;
        SetCueRate(syncCues ? 0 : 1);
        ApplyAtTime(time, false, 0);
    }
    public void HoldSpeed(float bodyRate, float multiplier, bool syncCues, float currentTime) {
        SetCueRate(syncCues ? multiplier : 1);
        ApplyAtTime(null, true, bodyRate, multiplier, currentTime, propsOnly: multiplier == 1);
    }
    public void ApplyDriven(float time, float cueRate, bool syncCues, bool loopLayers = false) {
        PausedAt = null;
        SetCueRate(syncCues ? cueRate : 1);
        ApplyAtTime(time, false, 0, loopLayers: loopLayers);
    }
    public void AddDrivenPoseSamples(Dictionary<DrivenPoseKey, float> samples, float time, bool loopLayers) {
        var owners = new Dictionary<nint, nint>();
        AnimationRestart.ActiveControls((Character*)Address, includeFadingOut: true, owners: owners);
        if(propModels is { Count: > 0 }) AnimationRestart.PropControls((Character*)Address, propModels, owners);
        foreach(var saved in controls) {
            if(!owners.TryGetValue(saved.Address, out var skeleton)) continue;
            var control = (hkaDefaultAnimationControl*)saved.Address;
            var binding = control->hkaAnimationControl.Binding.ptr;
            if((nint)binding != saved.Binding || (nint)binding->Animation.ptr != saved.Animation) continue;
            if(!saved.IsProp && saved.Address != Body && control->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasingOut) continue;
            var duration = control->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration;
            if(float.IsFinite(duration) && duration > 0)
                samples[new(skeleton, saved.Animation)] = loopLayers || saved.IsProp
                    ? TimelineRules.DrivenLayerTime(time, duration) : TimelineRules.HoldTime(time, duration);
        }
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
    private void ApplyAtTime(float? time, bool play, float bodyRate, float multiplier = 1, float currentTime = 0, bool propsOnly = false, bool loopLayers = false) {
        var live = AnimationRestart.ActiveControls((Character*)Address, includeFadingOut: true).ToHashSet();
        if(!propsOnly) CaptureBodyControls(live);
        if(propModels is { Count: > 0 }) {
            var props = AnimationRestart.PropControls((Character*)Address, propModels).ToHashSet();
            controls.RemoveAll(saved => saved.IsProp && !SameControl(saved, props));
            foreach(var pointer in props) {
                if(!live.Add(pointer) || controls.Any(saved => saved.Address == pointer)) continue;
                var prop = (hkaDefaultAnimationControl*)pointer;
                if(float.IsFinite(prop->PlaybackSpeed))
                    controls.Add(new(pointer, (nint)prop->hkaAnimationControl.Binding.ptr,
                        (nint)prop->hkaAnimationControl.Binding.ptr->Animation.ptr, prop->PlaybackSpeed, isProp: true));
            }
        }
        foreach(var saved in controls) {
            if((propsOnly && !saved.IsProp) || !SameControl(saved, live)) continue;
            var c = (hkaDefaultAnimationControl*)saved.Address;
            if(!saved.IsProp && saved.Address != Body && c->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasingOut) continue;
            var duration = c->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration;
            if(!float.IsFinite(duration) || duration <= 0) continue;
            if(saved.IsProp) {
                var phase = Math.Max(0, time ?? currentTime);
                c->hkaAnimationControl.LocalTime = TimelineRules.DrivenLayerTime(phase, duration);
                c->PlaybackSpeed = saved.Applied = 0;
                continue;
            }
            if(time is { } position)
                c->hkaAnimationControl.LocalTime = loopLayers ? TimelineRules.DrivenLayerTime(position, duration) :
                    play ? Math.Clamp(position, 0, duration) : TimelineRules.HoldTime(position, duration);
            c->PlaybackSpeed = saved.Applied = play ?
                (saved.Address == Body ? bodyRate : TimelineRules.PlaySpeed(saved.Original)) * multiplier : 0;
        }
    }
    public void Release(IGameObject actor) {
        if(!IsSameActor(actor)) return;
        ReleaseCueSpeed();
        if(!IsActor(actor)) return;
        var live = AnimationRestart.ActiveControls((Character*)actor.Address, includeFadingOut: true).ToHashSet();
        if(propModels is { Count: > 0 }) live.UnionWith(AnimationRestart.PropControls((Character*)actor.Address, propModels));
        foreach(var saved in controls) {
            if(!SameControl(saved, live)) continue;
            var c = (hkaDefaultAnimationControl*)saved.Address;
            // Do not overwrite a newer playback-speed change made by the game or another tool.
            if(TimelineRules.CanRestore(c->PlaybackSpeed, saved.Applied)) c->PlaybackSpeed = saved.Original;
        }
    }
}
