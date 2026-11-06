// Added: inspect visible player body clips using the spline layout documented by VFX Editor.
using System;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Animation;

namespace AnimSync;

internal static unsafe class AnimationSampling {
	// Reference: Dalamud-VFXEditor/VFXEditor/Interop/Havok/Structs/HkaSplineCompressedAnimation.cs
	[StructLayout(LayoutKind.Explicit, Size = 0x54)]
	private struct SplineTiming {
		[FieldOffset(0x38)] public int NumFrames;
		[FieldOffset(0x50)] public float FrameDuration;
	}
	// hkArray lengths from VFX Editor's HkaInterleavedUncompressedAnimation layout.
	[StructLayout(LayoutKind.Explicit, Size = 0x58)]
	private struct InterleavedTiming {
		[FieldOffset(0x40)] public int TransformCount;
		[FieldOffset(0x50)] public int FloatCount;
	}
	public static bool IsExcluded(Character* character, Func<ushort, string> resolveKey, out string key) {
		var seq = &character->Timeline.TimelineSequencer;
		var scheduler = seq->GetSchedulerTimeline(0);
		// Prefer the actual running scheduler when its ID has already been cleared.
		key = scheduler != null && scheduler->ActionTimelineKey.HasValue
			? scheduler->ActionTimelineKey.ToString() : resolveKey(seq->TimelineIds[0]);
		if(string.IsNullOrEmpty(key)) key = resolveKey(seq->TimelineIds[0]);
		return AnimationExclusions.IsExcluded(key);
	}
	public static bool TryRead(IGameObject obj,
		Func<ushort, string> resolveKey, out AnimationSample sample, out string reason) {
		if(IsExcluded((Character*)obj.Address, resolveKey, out var key)) {
			sample = default;
			reason = $"Excluded: {AnimationExclusions.GetExclusionReason(key)} ({key})";
			return false;
		}
		return TryReadTiming(obj, out sample, out reason);
	}
	private static bool TryReadTiming(IGameObject obj, out AnimationSample sample, out string reason) {
		sample = default;
		reason = "No loaded draw object";
		var character = (Character*)obj.Address;
		var draw = character->GameObject.DrawObject;
		if(draw == null) return false;
		reason = "Not currently drawn";
		if(!draw->IsVisible) return false;
		reason = "Mounted";
		if(!character->GameObject.IsNotMounted()) return false;
		reason = "No loaded body animation control";
		var control = ((Actor*)obj.Address)->Control;
		if(control == null) return false;
		var animation = control->hkaAnimationControl.Binding.ptr->Animation.ptr;
		int frameCount;
		switch(animation->Type) {
			case hkaAnimation.AnimationType.SplineCompressedAnimation:
				frameCount = ((SplineTiming*)animation)->NumFrames;
				break;
			case hkaAnimation.AnimationType.InterleavedAnimation:
				var timing = (InterleavedTiming*)animation;
				frameCount = ClipFrames.InterleavedFrameCount(timing->TransformCount, animation->NumberOfTransformTracks,
					timing->FloatCount, animation->NumberOfFloatTracks);
				break;
			default:
				reason = $"Unsupported clip format: {animation->Type}";
				return false;
		}
		reason = $"Invalid frame count ({frameCount})";
		if(frameCount < 2 || frameCount > 1000000) return false;
		var duration = animation->Duration;
		var time = control->hkaAnimationControl.LocalTime;
		var speed = control->PlaybackSpeed;
		reason = "Paused or invalid playback timing";
		if(!float.IsFinite(time) || !float.IsFinite(duration) || duration <= 0 || !float.IsFinite(speed) || speed <= 0) return false;
		var timeline = character->Timeline.TimelineSequencer.TimelineIds[0];
		// A scheduler ID may be zero while the loaded clip continues playing.
		// Detection depends on the live clip, not a new emote request.
		var frame = ClipFrames.CurrentFrame(time, duration, frameCount);
		sample = new AnimationSample(obj.GameObjectId, obj.Name.TextValue, obj.Position, timeline, frame, frameCount, duration, speed, time);
		reason = "Tracked";
		return true;
	}
}
