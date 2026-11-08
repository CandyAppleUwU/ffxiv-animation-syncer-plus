using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.Havok.Animation.Playback;

namespace AnimSync;

public sealed partial class AnimSync {
    private unsafe delegate void StepPoseDelegate(hkaAnimatedSkeleton* skeleton, float delta);
    private Hook<StepPoseDelegate>? drivenPoseHook;
    private bool drivenPoseHookFailed;
    private volatile Dictionary<DrivenPoseKey, float>? drivenPoseSamples;

    private unsafe void PublishDrivenPoseSamples() {
        Dictionary<DrivenPoseKey, float>? samples = null;
        var awaitingControl = false;
        if(!drivenPoseHookFailed) {
            foreach(var track in AllTimelines()) {
                if(track.timelineView == null || !track.timelineActors.Exists(playback => playback.HasProps)) continue;
                var clock = track.PropSpeedClock;
                var linkedTime = musicControls.TryGetValue(track.Key, out var music) ? music.Link?.AnimationTime : null;
                if(clock == null && linkedTime == null && !track.PausedAt.HasValue) continue;
                if(track.PropControlGapSince.HasValue) {
                    awaitingControl = true;
                    continue;
                }
                samples ??= new();
                foreach(var playback in track.timelineActors) {
                    var time = clock?.Time ?? linkedTime ?? playback.PausedAt ?? track.PausedAt;
                    if(time is { } phase) playback.AddDrivenPoseSamples(samples, (float)phase, loopLayers: clock.HasValue);
                }
            }
        }
        drivenPoseSamples = samples;
        if(samples is not { Count: > 0 }) {
            if(!awaitingControl && drivenPoseHook?.IsEnabled == true) drivenPoseHook.Disable();
            return;
        }
        if(drivenPoseHook?.IsEnabled == true) return;
        try {
            drivenPoseHook ??= Interop.HookFromAddress<StepPoseDelegate>((nint)hkaAnimatedSkeleton.MemberFunctionPointers.stepDeltaTime, AfterPoseStep);
            drivenPoseHook.Enable();
        } catch(Exception ex) {
            drivenPoseSamples = null;
            drivenPoseHookFailed = true;
            Logger.Warning(ex, "Could not attach the driven animation sampling hook.");
        }
    }

    private unsafe void AfterPoseStep(hkaAnimatedSkeleton* skeleton, float delta) {
        drivenPoseHook!.Original(skeleton, delta);
        var samples = drivenPoseSamples;
        if(samples == null || skeleton == null) return;
        for(var i = 0; i < skeleton->AnimationControls.Length; i++) {
            var control = skeleton->AnimationControls[i].Value;
            if(control == null) continue;
            var binding = control->hkaAnimationControl.Binding.ptr;
            // Loop restarts can replace the control after the framework update. Match the same clip on the same live skeleton.
            if(binding == null || !samples.TryGetValue(new((nint)skeleton, (nint)binding->Animation.ptr), out var time)) continue;
            control->hkaAnimationControl.LocalTime = time;
        }
    }

    private void DisposeDrivenPoseSampling() {
        drivenPoseSamples = null;
        drivenPoseHook?.Dispose();
    }
}

internal readonly record struct DrivenPoseKey(nint Skeleton, nint Animation);
