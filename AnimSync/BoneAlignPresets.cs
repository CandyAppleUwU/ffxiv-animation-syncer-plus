using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimSync;

/// <summary>A built-in Bone Align setup: a name and the line Blender's "Copy Align Offset" makes.</summary>
internal sealed record BoneAlignPreset(string Name, string Line);

internal static class BoneAlignPresets {
	public static readonly IReadOnlyList<BoneAlignPreset> BuiltIn = new[] {
		new BoneAlignPreset("Blow Job",
			"AnimSyncAlign j_f_ulip_01_l+j_f_ulip_01_r+j_f_dlip_01_l+j_f_dlip_01_r iv_ochinko_f -0.00447 -0.01078 -0.00143"),
		// Your iv_koumon exactly on the partner's iv_ochinko_d (no offset).
		new BoneAlignPreset("Ass", "AnimSyncAlign iv_koumon iv_ochinko_d 0 0 0"),
		// Your iv_ochinko_d exactly on the partner's iv_omanko (no offset).
		new BoneAlignPreset("Pussy", "AnimSyncAlign iv_ochinko_d iv_omanko 0 0 0"),
	};

	/// <summary>SimpleHeels command that SETS the whole temp offset (Y height, Z forward, X left, rotation in degrees).</summary>
	public static string SimpleHeelsSetCommand(Vector3 offset, float rotationDegrees) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
		$"/heels temp set height {offset.Y:0.#####} forward {offset.Z:0.#####} left {offset.X:0.#####} rotate {rotationDegrees:0.###} silent");

	public const string SimpleHeelsResetCommand = "/heels temp reset";

	/// <summary>Race, gender, body type, height and clan packed into one comparable number.</summary>
	public static long LooksKey(byte race, byte sex, byte bodyType, byte height, byte tribe) =>
		(long)race << 32 | (long)sex << 24 | (long)bodyType << 16 | (long)height << 8 | tribe;

	/// <summary>Customize key combined with the loaded model's identity and scale.</summary>
	public static long ModelKey(long looks, long model, long skeleton, long scale) =>
		looks * 1_000_003 ^ HashCode.Combine(model, skeleton, scale);

	/// <summary>Degrees folded into -180..180.</summary>
	public static float WrapDegrees(float degrees) {
		var wrapped = (degrees + 180f) % 360f;
		if(wrapped < 0) wrapped += 360f;
		return wrapped - 180f;
	}

	/// <summary>Two readings in a row within half a millimetre = the pose has stopped changing.</summary>
	public static bool Settled(Vector3? previous, Vector3 current, float tolerance = 0.0005f) =>
		previous is { } last && Vector3.Distance(last, current) <= tolerance;

	/// <summary>True when enough time has passed since the last send to push a pending change again.</summary>
	public static bool ShouldSend(bool pending, double now, double lastSent, double interval = 0.1) =>
		pending && (now - lastSent >= interval || now < lastSent);
}
