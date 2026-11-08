// Added for the body/face/sound restart modification. Native playback needs in-game validation.
using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AnimSync;

internal static unsafe class AnimationRestart {
	// Return fresh controls; callers must reacquire membership before using stored addresses.
	public static List<nint> ActiveControls(Character* character, bool includeFadingOut = false, Dictionary<nint, nint>? owners = null) {
		var result = new List<nint>();
		CollectControls(GetSkeleton(character), result, includeFadingOut, owners);
		return result;
	}
	public static List<nint> PropControls(Character* character, IReadOnlySet<ushort> models, Dictionary<nint, nint>? owners = null) {
		var result = new List<nint>();
		if(models.Count == 0 || character->GameObject.DrawObject == null) return result;
		var owner = &character->GameObject.DrawObject->Object;
		var visited = 0;
		foreach(var child in owner->ChildObjects) {
			if(++visited > 64) break;
			if(child->ParentObject == owner && child->GetObjectType() == ObjectType.CharacterBase)
				CollectProp((CharacterBase*)child, models, result, owners);
		}
		foreach(var slot in character->DrawData.WeaponData)
			if(slot.Weapon != null) CollectProp(&slot.Weapon->CharacterBase, models, result, owners);
		return result;
	}
	private static void CollectProp(CharacterBase* model, IReadOnlySet<ushort> models, List<nint> result, Dictionary<nint, nint>? owners) {
		if(model->GetModelType() == CharacterBase.ModelType.Weapon && models.Contains(((Weapon*)model)->ModelSetId))
			CollectControls(model->Skeleton, result, owners: owners);
	}
	private static void CollectControls(Skeleton* skeleton, List<nint> result, bool includeFadingOut = false, Dictionary<nint, nint>? owners = null) {
		if(skeleton == null || skeleton->PartialSkeletons == null) return;
		for(var partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
			for(var bank = 0; bank < 2; bank++) {
				var animated = skeleton->PartialSkeletons[partial].GetHavokAnimatedSkeleton(bank);
				if(animated == null) continue;
				for(var i = 0; i < animated->AnimationControls.Length; i++) {
					var c = animated->AnimationControls[i].Value;
					if(c == null || c->hkaAnimationControl.Binding.ptr == null || c->hkaAnimationControl.Binding.ptr->Animation.ptr == null ||
						c->EaseStatus == FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl.EaseStatusEnum.EasedOut ||
						(!includeFadingOut && c->EaseStatus == FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl.EaseStatusEnum.EasingOut)) continue;
					if(!result.Contains((nint)c)) result.Add((nint)c);
					if(owners != null) owners[(nint)c] = (nint)animated;
				}
			}
	}
	public static void MatchTime(Character* character, float time, Character* cueLeader) {
		if(!float.IsFinite(time) || time < 0) return;
		if(cueLeader != null) {
			var source = cueLeader->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
			var target = character->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
			// Only copy a clock within the same native cue timeline. Visual matching is independent of this check.
			if(source != null && target != null && source->SchedulerResource != null && source->SchedulerResource == target->SchedulerResource &&
				float.IsFinite(source->CurrentTimestamp) && source->CurrentTimestamp > target->CurrentTimestamp)
				target->CurrentTimestamp = source->CurrentTimestamp;
		}
		ResetAnimationControls(character, time, true);
	}
	public static int Restart(Character* character, bool syncSound, ushort baseTimeline = 0, bool resumePaused = false) {
		var skeleton = GetSkeleton(character);
		if(skeleton == null) return 0;
		var restarted = syncSound ? RestartTimelines(character, baseTimeline, resumePaused) : 0;
		ResetAnimationControls(character, resumePaused: resumePaused);
		return restarted;
	}

	private static int RestartTimelines(Character* character, ushort baseTimeline, bool resumePaused) {
		var sequencer = &character->Timeline.TimelineSequencer;
		// Snapshot before stopping anything: changing a slot can affect other slots.
		Span<ushort> timelines = stackalloc ushort[sequencer->TimelineIds.Length];
		Span<float> speeds = stackalloc float[sequencer->TimelineSpeeds.Length];
		sequencer->TimelineIds.CopyTo(timelines);
		sequencer->TimelineSpeeds.CopyTo(speeds);
		if(timelines[0] == 0) timelines[0] = baseTimeline;
		for(var slot = 0; slot < timelines.Length; slot++) {
			// Ignore pending requests and inactive slots; only restart actual playback.
			if(timelines[slot] == 0 || (sequencer->GetSchedulerTimeline((uint)slot) == null && !(slot == 0 && baseTimeline != 0)))
				timelines[slot] = 0;
		}

		// LocalTime alone does not restart scheduler events (including sound cues).
		// Clear old slot state, then issue actual LOCAL timeline playback requests.
		// Merely restoring slot IDs did not restore sound after a Penumbra redraw.
		for(var slot = 0; slot < timelines.Length; slot++)
			if(timelines[slot] != 0)
				sequencer->SetSlotTimeline((uint)slot, 0);
		var restarted = 0;
		for(var slot = 0; slot < timelines.Length; slot++) {
			if(timelines[slot] == 0) continue;
			sequencer->PlayTimeline(timelines[slot]);
			sequencer->SetSlotSpeed((uint)slot, resumePaused && speeds[slot] == 0 ? 1 : speeds[slot]);
			restarted++;
		}
		return restarted;
	}

	private static void ResetAnimationControls(Character* character, float time = 0, bool seeking = false, bool resumePaused = false) {
		// Facial/lip controls need not be on the body's first partial skeleton.
		// Reacquire after native playback, which may replace animation controls.
		// With sound sync off this is the only operation: no scheduler replay.
		var skeleton = GetSkeleton(character);
		if(skeleton == null) return;
		for(var partial = 0; partial < skeleton->PartialSkeletonCount; partial++) {
			for(var bank = 0; bank < 2; bank++) {
				var animated = skeleton->PartialSkeletons[partial].GetHavokAnimatedSkeleton(bank);
				if(animated == null) continue;
				for(var index = 0; index < animated->AnimationControls.Length; index++) {
					var control = animated->AnimationControls[index].Value;
					if(control == null || control->hkaAnimationControl.Binding.ptr == null ||
						control->hkaAnimationControl.Binding.ptr->Animation.ptr == null) continue;
					if((seeking || resumePaused) && control->EaseStatus is FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl.EaseStatusEnum.EasedOut
						or FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl.EaseStatusEnum.EasingOut) continue;
					if(seeking) {
						if(control->PlaybackSpeed <= 0) continue;
						var duration = control->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration;
						if(!float.IsFinite(duration) || duration <= 0) continue;
						control->hkaAnimationControl.LocalTime = Math.Clamp(time, 0, duration);
					} else {
						control->hkaAnimationControl.LocalTime = 0;
						if(resumePaused && control->PlaybackSpeed == 0) control->PlaybackSpeed = 1;
					}
				}
			}
		}
	}

	private static Skeleton* GetSkeleton(Character* character) {
		if(character == null || character->GameObject.DrawObject == null) return null;
		var draw = character->GameObject.DrawObject;
		if(draw->Object.GetObjectType() != ObjectType.CharacterBase) return null;
		var skeleton = ((CharacterBase*)draw)->Skeleton;
		return skeleton == null || skeleton->PartialSkeletons == null ? null : skeleton;
	}
}
