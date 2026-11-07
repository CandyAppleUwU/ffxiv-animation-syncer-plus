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
	// 64-bit Havok layout: base 0x38, two hkArrays, nine int offsets + alignment,
	// float hkArray, three int offsets, skeleton dimensions, stored frame count.
	// Reference: DSMapStudio/HKX2/Autogen/hkaPredictiveCompressedAnimation.cs
	[StructLayout(LayoutKind.Explicit, Size = 0xA8)]
	private struct PredictiveTiming {
		[FieldOffset(0x9C)] public int NumBones;
		[FieldOffset(0xA0)] public int NumFloatSlots;
		[FieldOffset(0xA4)] public int NumFrames;
	}
	// hkaQuantizedAnimation starts with an hkArray<byte> after the hkaAnimation base.
	[StructLayout(LayoutKind.Explicit, Size = 0x48)]
	private struct QuantizedTiming {
		[FieldOffset(0x38)] public byte* Data;
		[FieldOffset(0x40)] public int DataLength;
	}
	public static bool IsExcluded(Character* character, Func<ushort, string> resolveKey, out string key) {
		key = TimelineKey(character, resolveKey);
		return AnimationExclusions.IsExcluded(key);
	}
	// Read the resource for the strongest live body control, including resident clips without an emote scheduler.
	public static (string Path, ushort Binding)? CurrentPap(IGameObject actor) {
		var native = (Actor*)actor.Address;
		if(native == null) return null;
		var body = native->Control;
		if(body == null) return null;
		var partial = &native->DrawObject->Skeleton->PartialSkeletons[0];
		var visited = 0;
		foreach(var entry in partial->AnimationControllers) {
			if(++visited > 256) break;
			var controller = entry.Value;
			if(controller == null || controller->HavokAnimationControl != body) continue;
			var pap = controller->PapResourceHandle;
			if(pap == null || pap->HavokAnimationContainer == null || pap->AnimationCount is 0 or > 512) return null;
			var bindings = pap->HavokAnimationContainer->Bindings;
			if(bindings.Length is < 1 or > 512) return null;
			for(ushort i = 0; i < bindings.Length; i++)
				if(bindings[i].ptr == body->hkaAnimationControl.Binding.ptr) return (pap->FileName.ToString(), i);
		}
		return null;
	}
	public static string TimelineKey(Character* character, Func<ushort, string> resolveKey) {
		var seq = &character->Timeline.TimelineSequencer;
		var scheduler = seq->GetSchedulerTimeline(0);
		// Prefer the actual running scheduler when its ID has already been cleared.
		var key = scheduler != null && scheduler->ActionTimelineKey.HasValue
			? scheduler->ActionTimelineKey.ToString() : resolveKey(seq->TimelineIds[0]);
		if(string.IsNullOrEmpty(key)) key = resolveKey(seq->TimelineIds[0]);
		return key;
	}
	public static bool TryRead(IGameObject obj,
		Func<ushort, string> resolveKey, out AnimationSample sample, out string reason, bool allowPaused = false) {
		if(IsExcluded((Character*)obj.Address, resolveKey, out var key)) {
			sample = default;
			reason = $"Excluded: {AnimationExclusions.GetExclusionReason(key)} ({key})";
			return false;
		}
		return TryReadTiming(obj, out sample, out reason, allowPaused);
	}
	// Manual timeline control inspects the loaded clip, independently of automatic emote eligibility.
	public static bool TryReadTimeline(IGameObject obj, out AnimationSample sample, out string reason) =>
		TryReadTiming(obj, out sample, out reason, true, true);
	private static bool TryReadTiming(IGameObject obj, out AnimationSample sample, out string reason, bool allowPaused, bool manualTimeline = false) {
		sample = default;
		reason = "No loaded draw object";
		var character = (Character*)obj.Address;
		var draw = character->GameObject.DrawObject;
		if(draw == null) return false;
		reason = "Not currently drawn";
		// Camera culling does not necessarily unload a character's animation controls.
		// Manual timelines can keep using the guarded live control while off-camera.
		if(!manualTimeline && !draw->IsVisible) return false;
		reason = "Mounted";
		if(!manualTimeline && !character->GameObject.IsNotMounted()) return false;
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
			case hkaAnimation.AnimationType.PredictiveCompressedAnimation:
				var predictive = (PredictiveTiming*)animation;
				frameCount = ClipFrames.PredictiveFrameCount(predictive->NumFrames, predictive->NumBones, predictive->NumFloatSlots,
					animation->NumberOfTransformTracks, animation->NumberOfFloatTracks);
				if(frameCount == 0) {
					reason = "Invalid predictive animation frame count or track metadata";
					return false;
				}
				break;
			case hkaAnimation.AnimationType.QuantizedCompressedAnimation:
				var quantized = (QuantizedTiming*)animation;
				frameCount = quantized->Data == null || quantized->DataLength < ClipFrames.QuantizedHeaderSize ? 0 :
					ClipFrames.QuantizedFrameCount(new ReadOnlySpan<byte>(quantized->Data, ClipFrames.QuantizedHeaderSize), quantized->DataLength, animation->Duration);
				break;
			default:
				reason = $"Unsupported clip format: {animation->Type}";
				return false;
		}
		reason = $"Invalid frame count ({frameCount})";
		if(frameCount < (manualTimeline ? 1 : 2) || frameCount > 1000000) return false;
		var duration = animation->Duration;
		var time = control->hkaAnimationControl.LocalTime;
		var speed = control->PlaybackSpeed;
		reason = "Paused or invalid playback timing";
		if(!float.IsFinite(time) || !float.IsFinite(duration) || duration <= 0 || !float.IsFinite(speed) ||
			(!manualTimeline && speed < 0) || (!allowPaused && speed == 0)) return false;
		var timeline = character->Timeline.TimelineSequencer.TimelineIds[0];
		// A scheduler ID may be zero while the loaded clip continues playing.
		// Detection depends on the live clip, not a new emote request.
		var frame = ClipFrames.CurrentFrame(time, duration, frameCount);
		sample = new AnimationSample(obj.GameObjectId, obj.Name.TextValue, obj.Position, timeline, frame, frameCount, duration, speed, time);
		reason = "Tracked";
		return true;
	}
}
