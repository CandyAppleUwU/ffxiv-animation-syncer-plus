// Modified: validate the complete body control chain before accessing animation data.
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;

namespace AnimSync;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct Actor {
	[FieldOffset(0x100)] public DrawObject* DrawObject;
	
	public hkaDefaultAnimationControl* Control {
		get {
			if(DrawObject == null) return null;
			
			var skeleton = DrawObject->Skeleton;
			if(skeleton == null) return null;
			if(skeleton->PartialSkeletonCount == 0) return null;
			
			var partials = skeleton->PartialSkeletons;
			if(partials == null) return null;
			
			// The first entry can be an old fading-out clip, or the live clip can
			// be in the other bank. Follow the strongest loaded body control.
			hkaDefaultAnimationControl* best = null;
			for(var bank = 0; bank < 2; bank++) {
				var hkaSkeleton = partials->GetHavokAnimatedSkeleton(bank);
				if(hkaSkeleton == null) continue;
				for(var i = 0; i < hkaSkeleton->AnimationControls.Length; i++) {
					var control = hkaSkeleton->AnimationControls[i].Value;
					if(control == null || control->hkaAnimationControl.Binding.ptr == null ||
						control->hkaAnimationControl.Binding.ptr->Animation.ptr == null) continue;
					if(control->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasedOut) continue;
					var fadingOut = control->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasingOut;
					var bestFadingOut = best != null && best->EaseStatus == hkaDefaultAnimationControl.EaseStatusEnum.EasingOut;
					if(best == null || (!fadingOut && bestFadingOut) ||
						(fadingOut == bestFadingOut && control->hkaAnimationControl.Weight > best->hkaAnimationControl.Weight))
						best = control;
				}
			}
			return best;
		}
	}
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct DrawObject {
	[FieldOffset(0xA0)] public Skeleton* Skeleton;
}
