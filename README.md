# Animation Syncer Plus

Perfect and quick automatic matching animation syncing.

By **CandyApple**, based on [Animation Syncer by Sevii](https://github.com/Sevii77/animation_syncer).

## Install

In XIVLauncher/Dalamud, open `/xlsettings`, go to Experimental, and add this
Custom Plugin Repository:

```text
https://raw.githubusercontent.com/CandyAppleUwU/ffxiv-animation-syncer-plus/main/repo.json
```

Save, then open `/xlplugins` and install **Animation Syncer Plus**.

## Use

Open or close settings with `/animsync ui` or `/animsync settings`.

- **Auto Sync Anims** checks nearby players for matching clip lengths and
  playback speeds, then synchronizes groups on different frames.
- **Sync Groups**, **Sync Everyone**, **Sync Near You**, and **Sync Target**
  provide manual control.
- **Sync sound** controls sound timeline restarts. Saved **DJ exclusions**
  protect selected players' audio from restart requests.
- **Perfect Frame Sync (Experimental)** makes automatic groups join their
  leading player's animation time. It does not seek audio.
- **Sync Position** matches your position and facing to your target within
  0.5 yalms. With no target, it picks the closest player in that range.

Automatic syncing and redraw recovery pause during cutscenes and GPose. Manual animation commands and buttons remain available; in GPose they use the visible GPose copies. Position alignment is additionally
blocked during combat, duties, cutscenes, GPose, and loading.

| Command | Action |
| --- | --- |
| `/animsync` or `/animsync everyone` | Sync all eligible loaded characters |
| `/animsync groups` | Sync qualifying nearby groups |
| `/animsync near` | Sync yourself and players inside your selected radius |
| `/animsync target` | Sync yourself and the selected player |
| `/animsync pos` or `/animsync align` | Match nearby position and facing |
| `/animsync toggle` | Toggle automatic synchronization |
| `/animsync ui` or `/animsync settings` | Toggle the menu |

## Credits and license

Original Animation Syncer: Copyright 2024 Sevii. Modifications: CandyApple.
Distributed under the Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

## Same time sync (experimental)
Enable **Same time sync (Experimental)** below Perfect Frame Sync on each client and configure the same case-sensitive phrase. Type the phrase manually in party chat or a tell, or use **Time Sync Party** / **Time Sync Target**. The target button sends a real tell to the selected player; the party button sends a real party message.

A matching party message restarts loaded party members locally. A matching tell restarts you and the other player locally. The sender waits 300 ms after its outgoing chat event; recipients restart on the next game update. This is approximate chat-based coordination, not a guarantee of simultaneous playback across clients. There is no handshake confirming that another client has enabled the feature.

This mode restarts from the beginning, independently of Perfect Frame Sync. Normal animation exclusions, the sound toggle, and DJ exclusions apply. Matching chat triggers are explicit requests and are not blocked by the automatic cutscene pause. Repeated triggers and sends are limited to once per two seconds. Unloaded players cannot be synchronized. Phrases allow at most 180 UTF-8 bytes and cannot contain control characters or `<...>` chat placeholders. The feature defaults to off.

## Animation Timeline (experimental)
Open `/animsync timeline`, or click **Animation Timeline** at the top of the settings window. The separate window shows the current frame, total frames and elapsed time, with a frame slider, Play/Pause, first-frame and single-frame step controls. Frame numbers in this window start at 1. Stepping pauses playback. Dragging the slider temporarily pauses the selected players and holds the selected frame. Releasing it resumes playback only if it was playing before the drag.

Timeline actions always control you and visible players within your configured Distance radius with the same frame count and duration. Different animation IDs and already-synced players are included. Each control action selects the current matching players; players leaving range or changing clips are released. Group play uses your body's playback rate. GPose uses the visible GPose copies. Normal animation exclusions apply.

Timeline controls change local skeletal body/face playback only. Sound continues independently; it is not sought, paused or restarted, including for DJs. Auto Sync and redraw recovery skip actors under timeline control. Explicit manual sync requests release timeline control before restarting. Closing the timeline, changing your animation, changing territory/GPose mode or unloading the plugin restores the original playback speeds when the same native controls still exist, preserving newer speed changes from other tools.

### Multiple timelines and held stepping
The **Previous frame** and **Next frame** arrows step immediately when pressed. Holding either for 0.5 seconds starts repeating in that direction at approximately five frames per second; releasing stops it. Stepping stops at the first or final frame. These steps pause playback, just like a single step.

**+ Add Target's Timeline** at the bottom adds a row for your current target, labeled with that player's name. It is enabled only for a visible player with a supported animation outside all existing timeline groups. Each added row controls the target and matching players around that target using the Distance setting. Rows have independent sliders, play/pause, scrub state and held stepping. A character belongs to at most one row, with your own row taking priority. Changing your target does not retarget existing rows.

Adding a row starts its group playing from the beginning. Use the **X** beside an added player's name to remove that row and release its playback controls. Added rows are removed when their anchor disappears, changes territory/GPose context, or joins an earlier row's group. Closing the timeline clears added rows and restores still-valid playback speeds.

### Timeline music
Open **Animation Timeline** before the emote starts. Music detection runs automatically while the window is open. Closing it stops capture and returns still-playing sounds to the game. A sound that started before the window opened needs a fresh cue. Adding a target replays the group's cues using the sound sync and DJ settings.

## Historical sound tests
The following sections describe earlier experimental builds. The Sound Test window and command were removed in 1.3.88.

Open `/animsync soundtest` or **Sound Test (Experimental)** in settings. It has two separate parts:

1. **Preview — separate local playback:** scans a mod folder (initially the supplied Luna Vibes path), lets you load a single-entry unencrypted Vorbis SCD, and provides a seconds slider, play/pause, stop and preview volume. Loading does not automatically play. Dragging pauses preview audio and resumes on release only if it was playing before. It plays an extra local audio copy through the Windows output device, independently of game audio and the animation timeline. It does not replace or seek the in-game music, reproduce its spatial mixing/ducking/loops, or synchronize other clients. Closing the window stops and disposes the preview.
2. **Read-only native trigger monitor:** off by default. Enable listening before starting Step Dance normally. The test hooks SoundManager.PlaySound and copies matching virtual sound paths or resolved paths under the configured folder. It shows sound index, volume category and time since observation. That time is not a verified playback position, and volume category does not establish player ownership. Calls to other sound entry points may not be captured. No native sounds are stopped, paused, muted or replaced; existing DJ protection is unchanged.

The test supports the inspected Luna files. Marker/mono-split, encrypted, non-Vorbis and multi-entry SCD layouts are rejected with a message. No mod audio assets are included in this repository or release ZIP.

### Player-owned music detection (1.3.54 test — withdrawn)
The scheduler-interception ownership experiment below was removed in 1.3.55 after a reported animation-exit regression. Current builds retain sound observation and preview only; unassigned sounds do not create music rows. The strict over-10-second rule remains. The following describes the withdrawn experiment for reference only.

Open the animation timeline and enable listening in `/animsync soundtest` BEFORE starting the emote. The test now attempts to capture SoundManager.PlaySound calls made synchronously inside a known SchedulerTimeline.ProcessAll callback. Only scheduler ownership indices corresponding to exact timeline anchors are registered. It does not assign sounds merely because another player is nearby or has the same filename, category or animation.

Captured player identity is revalidated against row key, object ID, address, index, territory and GPose context. Penumbra.ResolveGameObjectPath resolves virtual resources for that player's collection. A supported SCD must have a decoded duration strictly greater than 10 seconds, and DJ-excluded players are omitted. Qualifying observations appear beneath the corresponding animation row as Music detected, filename and duration. These are detection metadata, not a verified live playhead or native audio controls; separately triggered long sounds may continue or stop independently.

Ownership through indirect VFX, asynchronous audio work or unobserved scheduler implementations may not be captured. The test skips unknown ownership/duration and reports why in Sound Test. The scheduler interception requires runtime validation. The separate file-preview player remains explicitly labeled and independent.

### PAP-reference inference (1.3.56)
This replaces the withdrawn scheduler-hook attribution test. Enable listening before starting an emote. The test polls the current body AnimationController's PAP handle/binding, reads its embedded TMB C063 entries, and resolves sound resources for that character through Penumbra. It never hooks animation scheduler calls.

Only one matching visible/recent player may infer ownership. Players without timeline rows still count toward ambiguity. Equal basenames alone do not match. A virtual sound path shared by two players stays ambiguous even if their collection resolutions differ, unless the observed trigger supplies an exact resolved path. Recent references are retained for 10 seconds, with a two-second pre-trigger window and a half-second post-trigger polling allowance for inference. Unavailable emote-player PAP scans suppress assignment conservatively.

The matched player must own an eligible animation timeline row, and the supported audio must be longer than 10 seconds. Inferred music rows display beneath that player's animation; native live playback/seek controls remain disabled. Detection cannot prove ownership in offscreen/asynchronous cases, and existing playing music is not recovered without a fresh observed trigger.

### Local music takeover test (1.3.60)
After an inferred music row appears, click **Take Control (Experimental)**. A ready, still-current sound allocation is required. The test prepares a local decoder, mutes only that captured native sound instance using per-instance volume, starts the local player near the native elapsed time (modulo track duration), then provides a seconds slider and Play/Pause. Ownership remains inferred; no animation scheduler interception or native sound seeking is used.

**Release control** stops the local copy and restores the original volume only if the same resource/sound ID/index/allocation still exists and its volume remains zero. The game sound continues internally while muted, so releasing returns to the game's current position; it does not seek the game to the local playhead. If the original sound expired, there is no native instance to restore. Game mixing/ducking, spatialization, loop points and cross-client synchronization are not reproduced. Only one local music takeover is active at a time. A fresh trigger/reload can require retaking control.

Controlled music loops the full track automatically, independently of the animation's duration. A Volume slider on the music controls row starts from the detected SCD channel's effective game volume. The level follows the game until you adjust it manually; right-click the slider to resume automatic following. Hover it to see the channel. Each new detection starts automatic again. Local output also applies game master volume/background muting and the entry/instance gain; it never changes game channel or Windows session/device settings. The game's equalizer, embedded custom loop regions and positional/ducking effects are not reproduced by this separate local audio player. Native arbitrary-time seeking is not implemented.

Closing Sound Test or the timeline, disabling listening, changing owner eligibility, removing/replacing a music observation, adding a DJ exclusion, or losing unambiguous inference releases local control. Pending/loading native handles are retained via generation tickets; slot reuse invalidates a ticket even if its address is unchanged. Missing allocations, loading state and failed mute verification produce an error instead of muting another sound. Audio device and native behavior remain experimental and need runtime validation.

### General PAP sound detection (1.3.62)
Enable **Listen for PAP in-game sound triggers** before starting an emote. Detection scans each visible emote player's active body PAP and its direct C063 sound references, resolving files through that player's Penumbra collection. The preview folder only selects files for independent preview; it does not limit detection. Arbitrary SCD paths are captured even before the next PAP scan. A unique matching player and an eligible timeline are still required; shared paths can remain ambiguous.

This is not universal sound support: indirect VFX sounds and routes other than SoundManager.PlaySound are not traced, and music duration/local playback still require a resolved local, single-entry, unencrypted Vorbis SCD at cue index 0. The >10-second rule and DJ exclusions remain active.

### Export Sound (1.3.63)
Click **Export Sound** beside a detected song or the selected sound preview. The full supported sound is converted once to a standard 16-bit PCM WAV with its original sample rate and channel count. It saves in Documents/Animation Syncer Plus/Exported Sounds; **Open folder** shows the result. Each export has a unique filename. Playback position, looping, slider volume and game equalizer effects are not included. Conversion runs in the background and uses the same single-entry unencrypted Vorbis SCD support as preview.

### Manual animation timeline (1.3.64)
Manual timeline controls inspect the character's current loaded body animation independently of the automatic emote filter. This includes poses, start transitions, non-emote animations, paused clips and mounted rider clips when their timing is readable. The same rules apply to self, added targets and matching nearby groups. Automatic syncing still excludes idle/movement, pose and start animations. A missing draw object/control, invalid timing or an unsupported native clip format can still prevent control; the window reports the actual reason.

### PAP → VFX → sound detection (1.3.65)
Detection now follows C012/C173 VFX paths from the active PAP body binding, resolves those AVFX files through the same player's Penumbra collection and reads sound paths/cue numbers from individual emitters. Both normal PlaySound and VFX PlayClipSound calls are observed. Exact sound path/cue and a unique matching player remain necessary; this is ownership inference, not direct native attribution. Missing or malformed linked files withhold assignment. VFX files are cached and refreshed when their length/write time changes.

This covers one PAP-to-AVFX-to-SCD chain. It does not recursively trace other effect systems, sounds from unrelated playback routes, or effects persisting long after their PAP reference history expires. The existing >10-second music rule, DJ exclusions and single-entry unencrypted Vorbis/cue-0 local decoder restrictions remain. Detecting a dependency alone never creates a music row without an observed native sound trigger.

### Predictive-compressed clips (1.3.67)
The shared animation sampler now reads the stored frame count of PredictiveCompressedAnimation clips. This enables timeline selection, target addition and matching groups when their native timing/track metadata is valid. Automatic sync continues to enforce its existing emote eligibility rules. No fixed-FPS estimate or predictive decompression is performed; seeking still changes the existing Havok playback control's time.
