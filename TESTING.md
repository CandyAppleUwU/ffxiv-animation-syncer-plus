# Animation Syncer Plus — build 1.3.40

The user confirmed clientside sound recovery and Penumbra redraw recovery work
in-game. Native body/face playback and additional edge cases still need game
validation; compilation does not validate native behavior.

## Current UI

Manifest includes the user's tags: animation, sync, emote, animsync, posing,
anims, align, position, tool, to satisfy the installer's
missing-tags validation check.

The user's unchanged 96x96 icon.png is stored in images/icon.png and copied to
images/icon.png beside the built plugin DLL, the path Dalamud uses for dev
plugin icons. It must remain in that subfolder when extracting the package.
The public IconUrl is configured under CandyAppleUwU/ffxiv-animation-syncer-plus
on branch main. Confirm the raw image is available after publication.

Sync Position sits below the animation sync buttons. It shares the
`/animsync pos` and `/animsync align` action. All five sync buttons have
descriptive tooltips after one continuous second of hover, including while
disabled. Position alignment is always disabled during combat, duties,
cutscenes and loading. Manual animation buttons remain available in cutscenes and GPose.

Perfect Frame Sync is below the manual sync buttons, followed by red
"(Experimental)" and a `(?)` explaining leader-frame matching. Sync Target uses
the exact same command handler as `/animsync target`; it wraps onto a new row
if there is insufficient space beside the other buttons. Distance is labeled
"Distance". Both Distance and Check Refresh Time have concise `(?)` tooltips,
with slider widths reserving room for their labels/help markers. Check layout,
colored text, target action and tooltip hover in-game.

"Perfect Frame Sync (Experimental)" is a saved toggle, default off to preserve
existing restart behavior. When on, automatic sync and Sync Groups choose the
highest current frame in each compatible group, then use elapsed clip time to
break ties. Followers' loaded active body/face controls seek to the leader's
elapsed time (clamped to each clip's duration); the leader is left untouched.
Sound/scheduler events are neither replayed nor seeked in this mode, regardless
of Sync sound. Redraw recovery and other manual modes retain existing behavior.
Test a new starter joining a nearby 200-frame dance at frame 150, matching at
loop boundaries and varying facial clip lengths. Skeletal control seeking does
not reconstruct timeline-driven facial transitions or make audio catch up.

Automatic group sync and redraw recovery always pause in cutscenes and GPose.
Manual commands and buttons remain usable, including manual groups, near, target,
everyone and body. GPose manual selection uses slots 201-439, GPoseTarget, and
the local player's GPose copy. The settings window remains visible in cutscenes
and GPose. Position remains blocked. Combat does not pause animation syncing.

The restart cooldown slider is labeled "Check Refresh Time" and accepts 0–5
seconds, displayed to two decimal places. Zero (0.00 s) removes the
restart cooldown and checks once per framework update. Positive settings below
0.1 seconds also lower the scan interval; higher settings retain the 100ms scan
interval with their configured per-player restart cooldown. Existing saved
values and the three-second default are retained. Verify zero and small values
in-game, including repeated drift; zero permits a restart on every update if
actors remain on different frames.

A Discord button is on the same first row as Auto Sync Anims and its help marker,
aligned to the right in Discord blue (#5865F2), with
white text and hover/pressed colors. Clicking it opens
https://discord.com/invite/bKswgraKvh through Dalamud's link opener. Check
alignment, colors and link opening in-game.

Display name and assembly product/title are Animation Syncer Plus; the plugin
author is CandyApple. Manifest description and punchline use the requested
opening line, with the tooltip's three steps below in the description. Internal
name/assembly remain AnimSync to preserve saved settings and installed identity.
Original upstream copyright/license notices remain in LICENSE and NOTICE, both
included in the ZIP. prepare-release.ps1 generates the repository index from
the compiled manifest, with matching versioned release links and icon URL.

Automatic nearby sync is labeled "Auto Sync Anims" with a `(?)` hover tooltip
to its right. The tooltip explains proximity, matching frame counts/timing and
different-current-frame checks, followed by a note about rarely needing manual
sync. Check wrapping and hover behavior in-game.

The DJ section is titled "DJs (Sound Sync Exclusions)" with the description
"Keeps DJs music uninterrupted when animations sync. This is only really needed
when anim syncing everyone." directly beneath the expanded heading.

Settings contain automatic nearby sync, Sync sound, distance, restart cooldown,
Sync Groups, Sync Everyone, Sync Near You, and the searchable DJ list with Add target and add/remove
buttons. Saved DJs sort first, with alphabetical ordering within DJs and other
players; search still applies to both. Debug commands/windows, animation detection details, player/group
counters, build/status readouts and explanatory paragraphs have been removed.

## Commands

- `/animsync`: restart eligible loaded player/battle NPC animations locally.
- `/animsync ui` or `/animsync settings`: toggle settings open/closed. Both
  aliases operate on the same window. Plugin installer buttons still open it.
- `/animsync groups` (alias `nearby`): sync only eligible, out-of-sync nearby
  player groups meeting distance, timing compatibility and cooldown requirements.
- `/animsync everyone` (also bare `/animsync`): sync all loaded eligible
  player/battle NPC animations regardless of distance, frame count, relative
  current frame or cooldown. Global sound and DJ policies still apply.
- `/animsync near`: sync yourself and visible world players inside your
  configured radius, regardless of pairwise group distance, timing compatibility,
  current frame or cooldown. Animation exclusions and sound/DJ policies still
  apply. It does not include NPCs, GPose/UI actors, or distant group members.
- `/animsync toggle`: toggle automatic nearby synchronization.
- `/animsync body`: rewind active body controls only.
- `/animsync pos` or `/animsync align`: move only the local character to the
  chosen player's exact position and facing. Uses a fixed 0.5-yalm 3D limit,
  independent of the animation Distance setting. A hard target must be another
  visible world player within range. If there is no target, choose the nearest
  visible other world player within range. An invalid or far selected target
  is rejected rather than replaced by a different player. With nobody in range,
  print "Too far" without moving. Based on the user's CoordMatch source; the
  original project has not been modified.

Alignment checks login, territory, both BetweenAreas flags, combat, all three
BoundByDuty flags, and the three cutscene flags before starting and before each
write. It revalidates both actor identities, current territory, render state and
0.5-yalm range, and retries at most 30 updates like CoordMatch. It cancels on
any invalid state and uses native SetPosition/SetRotation only for the local
player. It does not execute an emote or move the target.
- `/animsync target`: restart only yourself and the selected world player.
  Captures target identity when issued and revalidates it on the framework
  update. Both must be loaded and have eligible animations; otherwise neither
  is reset. Does not require distance, matching frame counts or differing current
  frames. Sync sound and DJ protection apply. Cutscenes do not block this manual request. NPCs, self-targets
  and departed/replaced targets are rejected. Test with automatic sync off to
  isolate the manual pair from independent automatic/redraw activity.

## Behavior to preserve

- Default distance 0.60 yalms, configurable from 0.1 to 2 yalms in 3D.
- Every pair of group members must be within range. Overlapping chains are
  partitioned deterministically by object ID rather than joined transitively.
- Matching frame counts, durations and positive playback speeds are required;
  different animation IDs are allowed. Any frame difference triggers sync.
- Cooldown defaults to 3 seconds, configurable from 0 to 5 seconds.
- Only emote-directory timelines are eligible. Names beginning with pose or
  containing start are excluded; s_pose01_loop remains eligible.
- Sync sound defaults on. Turning it off rewinds skeletal controls without
  issuing timeline playback requests.
- Penumbra redraw recovery restarts only the redrawn actor, locally, as soon
  as loading is ready. It retries for at most 5 seconds, respects sound/DJ
  exclusions and operates independently of the nearby-sync switch.
- DJ exclusions persist by character name and home world. DJs retain body/face
  synchronization but never receive sound restart requests. Their redraw
  recovery is skipped. Unknown player identities also skip sound restarts.
- Distance-slider editing previews a cyan ground-plane radius circle, lasting
  1.5 seconds after editing. It does not conform to terrain.
- Player sampling supports spline-compressed and interleaved Havok animations.
- No real emote-execution API is used. Playback changes affect this client.

## Validation

Run `dotnet run --project Tests -c Release` for group selection, frame mapping,
exclusion classification and DJ sound policy checks. Build the plugin against
installed Dalamud API 15 libraries. Inspect the release package for only plugin
files, not Dalamud dependencies.

In-game, verify settings and DJ persistence, search and target addition,
add/remove button colors, range preview, exact-distance boundaries, separate
nearby groups, one-frame mismatches and already-synchronized frames. Confirm
DJ music remains uninterrupted during manual sync, automatic sync and redraw.
Verify sound recovery after a Penumbra mod change/redraw with no real emote
execution on another client. Check logout, loading, actors leaving render range
and plugin reload. Ensure removed debug UI and counters do not appear.
Check combat does not pause animation syncing. Cutscenes and GPose block automatic
grouping and redraw recovery, while manual requests still work. Verify checks resume afterward, old redraw events
do not burst on resume, and old settings cannot disable the cutscene pause.
Check position alignment at 0.49, 0.5 and 0.51 yalms, target/self/NPC/no-target
cases, closest-player selection and facing wraparound. Verify no movement or
pending retry occurs in combat, duties or cutscenes, including when entered
during a retry. Verify tooltip delay and reset when switching between buttons.
Check the three manual buttons with automatic sync disabled: Groups should
skip singletons, equal frames and incompatible clips; Everyone should include
eligible distant actors; Near You should include the local player plus players
at or inside the configured radius, including opposite sides of that circle,
while excluding actors beyond it. Verify mixed frame counts do not prevent
Everyone/Near You manual resets, and DJ music remains protected in each mode.

## Sources

- Original plugin: https://github.com/Sevii77/animation_syncer
- Native interfaces: https://github.com/aers/FFXIVClientStructs
- Animation layouts and local playback: https://github.com/0ceal0t/Dalamud-VFXEditor
- Redraw event: https://github.com/Ottermandias/Penumbra.Api

No deployment or publishing has been performed by the agent.

### Same time sync — requires two clients
1. On both clients enable Same time sync and use the same phrase. Disable Auto Sync temporarily to isolate this feature.
2. With both players performing allowed emotes, send the exact phrase manually in a tell. Verify both clients restart the pair; check which client leads with the fixed 300 ms compensation.
3. Repeat with Time Sync Target, then manually in party and with Time Sync Party using three clients. Confirm unrelated nearby players do not restart and no automatic chat replies are sent.
4. Check a different phrase/case, say/shout, disabled feature, loading/zone change, and changing the phrase during a pending sender delay. None should run a queued restart.
5. Check rapid repeated messages/buttons, no party, self/non-player target, different home worlds, and an unloaded tell sender. Confirm there is no burst of restarts or unwanted messages.
6. Verify Sync sound off and DJ exclusions preserve audio; excluded animations remain untouched. Test Perfect Frame Sync both ways: this chat feature always restarts at the beginning.
7. Re-enable Auto Sync and test together. Confirm no immediate second auto restart after a chat sync. Check logout and plugin reload for stale callbacks.

Automated checks cover phrase matching, input validation, UTF-8 limits, and timing offsets. Actual chat payload identity, delivery timing, rendered UI, audio, and cross-client playback require the above in-game tests.

### 1.3.42 cutscene / GPose correction
- During a cutscene verify manual everyone, body, near, target and groups requests are processed and their buttons remain enabled. Automatic group sync and redraw recovery must stay paused.
- In GPose repeat manual tests with two visible player copies. Near You should use your GPose copy as its center; Target should use the GPose target; Groups should sample copies rather than hidden world actors. Everyone/body should affect GPose actors.
- Verify position commands and button remain blocked in GPose, cutscenes, duties and combat. Check the settings window stays visible in GPose/cutscenes.
- Check shared-phrase requests are not discarded by the automatic cutscene pause, subject to the game's own chat availability.
- Verify exiting GPose restores normal world selection. Native playback, GPose frozen actors, and UI visibility still require in-game validation.

### 1.3.43 timeline player — in-game validation required
- `/animsync timeline` and the Animation Timeline button should toggle the same separate window. Observe a playing emote without interacting: opening the window must not seek or pause it.
- With nobody matching nearby, seek first/middle/last frames, Pause, Play, and step backward/forward. Confirm only your character changes; stepping pauses, slider seeking preserves the current play/pause state, and Play resumes at the selected frame.
- Test a frozen GPose character, then exit/enter GPose. Confirm the visible local copy and GPose target selection remain correct; frame selection should still work for clips with playback speed zero.
- Matching players are always included: select visible players at/inside Distance with equal frame counts/duration, including different animation IDs and already-synced players. Exclude out-of-range, vertical out-of-range, unequal clip lengths, excluded emotes, hidden/UI actors and unrelated groups elsewhere. Check different playback rates follow the local body rate during group Play.
- Close the window while paused; move a participant away; change either animation; redraw; change territory; logout; unload/reload the plugin. Verify still-loaded original controls regain their prior speeds and stale identities never receive writes. Previously frozen GPose clips should return to their original frozen state on release.
- Enable Auto Sync and redraw recovery while using the timeline. Owned actors must not restart behind the slider; explicit manual and shared-phrase resets should take priority and release the timeline session. Check control after another posing tool changes speed, including while paused.
- Audio must keep playing independently when seeking/pausing, with no sound timeline restart or DJ interruption. Body/face clip seeking cannot reconstruct timeline-scheduled expressions or seek audio.
- Repeat in cutscenes and GPose: manual playback remains available; position and automatic group restrictions remain intact.
- Automated checks cover frame/time round trips, clamping, paused-player group eligibility, distance and compatibility boundaries, and guarded speed-restoration decisions. Native controls, actual pause/resume, facial behavior, disposal and rendered UI require the in-game checks above.

### 1.3.44 compact timeline
- Confirm the bottom description and group toggle are absent. The timeline window is about 145 pixels tall; verify slider, status, time and all playback buttons fit at the game UI scale.
- Timeline actions always include matching nearby players. Check a matching player inside Distance is controlled without enabling anything; outsiders and incompatible clips remain untouched.

### 1.3.45 timeline layout
- Verify time is left-aligned with the four icon buttons centered on the same row below the slider. Resize horizontally and test the game UI scale; buttons must remain centered and not overlap time.
- Slider should show numbers only, without the Frame prefix. Confirm first-frame, previous, play/pause and next icons render from Dalamud's icon font and their tooltips identify the action.
- Confirm the play/pause icon switches with playback state and all existing controls still work. Verify the smaller window fits status, slider and controls without a vertical scrollbar.

### 1.3.46 timeline button sizing
- Check all five icon glyphs, including Play and Pause, at default and custom game/Dalamud font scales. Icons must have padding on all sides and should not clip.
- Confirm identical square button sizes across playback states, a centered control group, time vertically centered on the same row, and enough window height for the controls.

### 1.3.47 timeline button alignment
- Confirm all five button tops align at default/custom UI scales, including after hovering and changing play/pause state.
- Confirm the rightmost last-frame icon seeks to the final frame and pauses; first/previous/play-pause/next keep their existing behavior. Check the five-button group remains centered with the time at the left.

### 1.3.48 timeline status
- Verify time stays on the left, the five buttons stay centered, and Synced | 1 Player sits on the right of the same row. With multiple controlled actors, verify Synced | X Players.
- Confirm the old Playing/Paused heading is absent and resizing/font-scale changes do not overlap the two labels with the controls.

### 1.3.49 content-fitted timeline height
- Load with a previously saved tall window and confirm it shrinks to just the slider, controls and normal bottom padding after layout.
- Check default and non-default Dalamud UI scales: no doubled scaling, clipped controls or large empty space underneath. Horizontal resizing should still work.
- Switch between a supported animation and the no-animation status: each view should fit its current content without retaining a previous taller height.

### 1.3.50 slider scrubbing
- During playback grab the slider, move to a frame, and hold still for several seconds. The selected characters must stay paused on that frame without flickering toward the next one.
- Release the slider and verify playback resumes from the selected frame. Repeat starting paused: release must leave playback paused.
- Click/hold/release without moving, drag repeatedly, and test keyboard/temporary numeric input. Verify the original play/pause state is preserved across each interaction.
- Close the window, change animations, enter GPose/change territory or run a manual sync while dragging. Releasing an old drag must not start an unrelated animation or undo a manual reset.
- Repeat with matching nearby players and GPose copies. Audio remains independent.

### 1.3.51 held stepping and target timelines
- Tap Next: exactly one step. Hold for less than 0.5s: no repeat. Hold longer: about five steps/second, paused, with no burst after a slow frame. Release: stop. At the last frame: clamp. Animation change/close during hold must cancel old input.
- Target a supported visible player outside your group: + becomes available to the right of Synced. Self, NPCs, excluded/unloaded players, and members of existing groups should leave it disabled. Retarget before the queued click is processed: do not add an unintended player.
- Add a target with nearby matching players. Verify a row named for the target appears below yours; only that row's group responds to its slider/playback controls. Adding the row alone must not alter playback. A lone target still gets its own row.
- Add a second separate target group. Check duplicate groups cannot be added and overlapping radii never let two rows own the same actor. Moving a member between groups must restore old speeds before the new row captures them; an anchor joining an earlier group removes its redundant row.
- Scrub and hold Next on each row. Input IDs, drag resumption, repeat timers, play state and frame counts must remain independent.
- Remove a paused target row with X, close the entire window, change territory/GPose, redraw or despawn an anchor. Verify guarded restoration of speeds and no stale writes. Changing targets after adding must not redirect an existing row.
- Check multiple-row height fits content, plus stays to the right of Synced, names display correctly, and controls remain centered with large UI scales. Repeat world/GPose tests with Auto Sync enabled and DJ audio playing.
- Automated checks cover repeat delay/rate/bounds/cancellation and pure group partitioning; actual target payloads, native independent playback, cleanup and UI need in-game validation.

### 1.3.52 backward held stepping
- Hold Previous for over 0.5 seconds: it must step backward at about five frames per second and stop at the first frame. Tapping still steps exactly once; releasing stops immediately.
- Alternate Previous/Next and test independent target rows. Inactive buttons must not cancel the active direction's timer; changing directions restarts the delay.

### 1.3.53 sound test (do not confuse preview with live sound control)
1. Extract the ENTIRE ZIP, including the four audio DLLs. Open `/animsync soundtest`. Confirm preview and native observations are labeled separately.
2. Keep native listening off. Scan the supplied Luna folder, load song/special/AlepthGessaffelstein.scd, verify roughly 259.32 seconds. Nothing should play until Play preview is pressed. Check play/pause, stop, volume, forward/backward seek and dragging while playing/paused. The preview uses its own Windows output; game sounds keep playing independently.
3. Load all other SCDs. File errors or unsupported formats should produce a message. Test stopping/closing/unloading while preview is playing or dragging, changing the selected file, output-device failure and repeated open/close. No stale audio callback should touch a disposed stream.
4. Stop preview. Enable native listening, THEN start Step Dance normally. Expect virtual sound/chills.scd (or dance2/3/4), starter and poppy entries depending on mood and trigger route. Check whether resolved Penumbra paths appear. Only SoundManager.PlaySound is monitored; no observation is evidence to investigate the other entry points, not proof no sound is playing.
5. Observe with two players using the same song: events must remain unassigned, with no guess of player ownership. The time display is age since observation, not sound elapsed time. Ensure existing in-game music/DJ sound continues without interruption, including when disabling listening or closing the test.
6. Return to animation timelines and verify existing controls, sound toggle, redraw recovery and DJ exclusions behave as before. Do not publish this as working native sound seeking.

Run `dotnet run --project Tests -c Release -- "C:\Penumbra\[NX] Luna Vibes"` for actual local-media decode/seek checks; the folder is an optional test input and no assets are copied. Pure checks cover malformed SCD input and native-trigger path filtering. Audio-device playback, native hook behavior, UI and shutdown need in-game testing.

### 1.3.54 player-specific music threshold
1. Open `/animsync timeline` and `/animsync soundtest`. Enable listening, THEN start Luna Step Dance. Note owner-capture hook count, observed sound path, captured player index/row and classification message.
2. With the 24.581s Chill or 24.32s Dance 4 song selected, expect a detected Music metadata row beneath the correct player only if synchronous owner context was captured. Intro effects (0.68s/1.48s), Dance 2 (18.2s) and Dance 3 (12.16s) must not produce Music rows. Special longer songs should qualify. Native seeking remains disabled.
3. With two players using the same song, confirm only sounds from each registered timeline anchor attach to that anchor's row. Group neighbors without their own timeline must not be relabeled as the anchor. Other nearby emotes, UI audio and BGM must not attach to a row based on proximity/category/path alone.
4. Add a target timeline and test separate Penumbra collections selecting different songs. Confirm exact player-specific resolution, durations, and ownership. Test GPose, territory changes, player/row removal, object-slot reuse and DJ exclusions; stale or excluded observations must disappear.
5. Disable listening/close Sound Test and verify scheduler hooks are disabled, Music observations clear, and existing game/DJ audio stays uninterrupted. Re-enable before a fresh emote trigger. Test plugin unload/reload while listening.
6. If an expected long song is skipped, report the classification message and whether Owner not captured appears. Missing asynchronous/indirect ownership is an unsupported capture case, not permission to guess ownership or create a row.

Automated checks cover strict duration boundaries, nonfinite values, missing/mismatched identities, row/slot/territory/GPose changes and DJ policy, plus existing local SCD decode/seek checks. Native ProcessAll capture and correct player attribution require the two-player runtime tests above.

### 1.3.55 animation-exit regression
- Reload/replace the old plugin so the withdrawn 1.3.54 scheduler hooks are uninstalled. Source/package must contain no SoundOwnerCapture or ProcessAll detour.
- While performing an emote, enable the Luna sound listener; cancel/leave the emote using the usual game actions. Repeat after starting the emote with listening already enabled, toggling listening repeatedly, GPose transitions and plugin reload.
- Verify observations and local preview still work. Owner attribution must remain unavailable, unknown owners must not create Music rows, and no removed scheduler interception can be enabled from the UI.
- Existing automated policy/media checks do not validate animation cancellation in-game; runtime confirmation of this regression fix is required.

### 1.3.56 PAP inference and 10-second music threshold
1. Reload the test build, open `/animsync timeline` and `/animsync soundtest`, then enable listening before starting Luna Step Dance. Check PAP scan counts: active players should be indexed, with sound references listed in the count. If unavailable is nonzero, assignment is intentionally withheld.
2. With just one matching eligible player, expect an explicitly inferred Music row for that timeline after a fresh sound trigger. Test actual Luna tracks: Dance 2 (~18.2s) and Dance 3 (~12.16s) now qualify; starter/poppy (<2s) must stay excluded. Exactly 10 seconds remains excluded.
3. Add a second visible player whose current/recent PAP references the same virtual sound path. The new trigger must be ambiguous, even when only one of those players has a timeline row. With distinct resolved trigger files, confirm collection-specific matching. Equal basenames from unrelated folders must never match.
4. Check current body binding selects only its own PAP animation references rather than every animation packed in the file. Trigger intro/loop transitions, change animations, remove a row, leave render range, change territory/GPose or change Penumbra settings and retest after refreshing listening.
5. Hold/pause/scrub animation timelines and verify normal animation exit while listening. No ProcessAll or other animation scheduler hooks may be installed. Observe-only sound hook remains unchanged.
6. Check owner IDs/addresses/indices and DJ exclusions still gate rows. Native sound calls have no direct player identity; the inference classification above the log is the result to report. This is metadata inference, not verified native sound control.

### 1.3.58 local controls / loading handles
1. Reload to install allocation tracking, enable listening, then start the dance freshly. After a unique >10s match, click Take Control. Loading handles should be retained rather than discarded; if not ready, wait and retry. Report the full active/loading/resource/sound ID/index error if it still fails.
2. Verify only the captured native instance is muted and a local copy plays near its elapsed time. Test Pause/Play, forwards/backwards seeks, drag-pause/resume and endpoint behavior. No native seek is performed. Verify other native music/DJs and Windows device volume are untouched.
3. Release: local playback stops, native volume returns at its own current playback time. Repeat after native expiry, slot reuse, changing mods or new sound triggers. A recycled slot must never receive restoration for an old ticket.
4. Close either window, disable listening, remove target/row, introduce ownership ambiguity, exclude the player as DJ, logout/change territory, fail an output device, and unload/reload while controlling. Confirm cleanup restores only the matching instance and native animation exit remains normal.
5. With two music rows, taking the second releases the first. Do not assume identical loop/ducking/spatial behavior or cross-client synchronization. A missing allocation or unsupported native state should fail closed with a specific message.

Automated tests cover ticket generations, reuse/clear/re-enable, native pool slot bounds/alignment and volume-restoration policy. Real allocation hooks, elapsed-time alignment, per-instance mute/restore and audio output require in-game testing.

### 1.3.59 signed native sound ID
- With the reported native state active=True, loading=False, resource=True and a negative sound ID, Take Control must no longer report not-ready solely due to the ID sign.
- Verify actual game sound is playing, only that instance is muted, local pause/seek work, and Release restores its volume. Inactive, loading, changed-allocation/resource/index or no-longer-playing instances must still fail closed.
- Pure regression checks use the reported -2147437464 handle and high-bit values; real per-instance mute/restore remains an in-game test.

### 1.3.60 looping / sound-channel volume
1. Enable listening before a fresh dance. Check the Music row's channel and Volume slider before takeover: Luna dance2 should identify Music (Orchestrion), bus 16, independently of the observed Player/Party/Other category. Compare its automatic level to that game's effective channel setting. Test music, effects, voice, ambient and system cues where available.
2. Take Control and let the entire song end twice. It must wrap to the beginning and keep playing independently of the animation loop. Pause across the end, seek near the end and release a drag: paused playback must stay paused, playing playback must loop. This test loops the whole decoded track, not custom embedded loop regions.
3. Change the game channel level: the automatic slider and local volume should follow. Adjust the row slider manually, change the game channel again and verify the manual value holds. Use game volume must resume following. A fresh sound observation starts automatic again; separate rows retain separate values.
4. Verify master mute/level and game background muting still apply; manual per-sound volume does not change native channel settings, other sounds, or Windows device/session volume. Compare loudness before/after takeover; spatial attenuation and bus-ducking effects may differ.
5. Retest release/close/unload, DJ exclusions, ambiguity, native slot reuse and all prior controls. Unknown routing must display Unknown channel and start at zero rather than guessing a category.

Automated checks cover cue-index routing and bounds, real Luna bus metadata, automatic/manual volume state, gap-free buffer filling and real Vorbis EOF-to-start reads without an audio device. Actual device playback and native game mix still require in-game confirmation.

### 1.3.61 compact music controls
- Verify time, Play/Pause music, Release control and right-aligned Volume share a single row. Resize and vary font scale; the minimum width must fit the controls and the height must shrink without an extra status line.
- Hover Volume for its channel and right-click to resume automatic volume. Manual volume, pause/seek/loop/release remain unchanged. Native equalizer processing is still not reproduced by the local player.

### 1.3.62 general PAP listener
- Use a different animation mod with a direct C063 sound reference outside the preview folder. Enable PAP listening before starting it; check a fresh trigger can be inferred after the following scan. Repeat with an already indexed PAP and virtual/resolved/wrapped paths.
- With two players sharing a cue, assignment must stay ambiguous; different player-specific resolved paths may disambiguate. Preserve the >10-second, DJ and active timeline requirements.
- Test dense unrelated game audio, closing/disabling/reloading, and slot reuse. Known PAP paths have a separately bounded event queue. No scheduler hooks or emote restarts are added.
- Unsupported formats/cues and indirect VFX-only sounds remain unsupported; verify clear classification rather than claiming universal detection. Real non-Luna detection requires in-game confirmation.

### 1.3.63 WAV export
- Export from a detected music row while playing, paused and after seeking; also export a selected preview without loading it. Confirm the output folder opens and a standard audio player reads the complete WAV once at the original sample rate/channels, independent of slider gain.
- Export repeatedly and verify earlier files remain intact; only one conversion may run at a time. Check missing/unsupported files and denied output-folder access show an error without disrupting game/local playback or leaving partial WAV files.
- Close/reopen windows during conversion and check playback controls/layout at different font scales. The worker uses file data only and must not access native game state.
- Automated checks export a real Luna song twice, read the WAV header/duration, and compare decoded samples within PCM16 precision. Outputs are removed after the test.

### 1.3.64 unrestricted manual animation selection
- Open the timeline while standing idle, posing (emote/pose), using a start transition, walking/running, using an emote, or mounted. Any loaded spline/interleaved body clip with valid timing should show controls, including paused and single-frame poses. Game-driven animation transitions may replace a clip; verify the timeline follows the new clip and cancels stale drag/repeat requests.
- Add a target performing an animation excluded from automatic sync. Verify their independent row and matching nearby group use the same unrestricted manual sampling. Check pause, scrub, play and closing restores speeds only on the same live bindings.
- Automatic syncing must retain all existing emote-only, pose/start and mounted exclusions. Cutscene/GPose manual behavior stays enabled; auto and position restrictions stay intact.
- Missing/invisible characters, absent body controls or invalid/native-unsupported timing must show the real reason, not a generic unsupported emote label. Test player replacement, redraw, zone changes and unload; do not use stale native controls.
- Existing numerical/group tests and a single-frame regression run offline. Actual animation availability, engine transitions, mounted poses and UI still require in-game validation.

### 1.3.65 PAP/VFX sound chain
- Enable listening, then trigger an animation whose active PAP C012/C173 entry starts a VFX with an emitter sound. Check PAP/VFX indexing, VFX/Clip observations, exact cue number and unique inferred ownership. Use a supported >10s cue-0 song to verify the Music row, takeover and export.
- Test two players sharing the VFX/SCD and two collections redirecting the same VFX path to different files. Shared paths must remain ambiguous; resolve VFX and sound per player, never by filename alone. Missing/malformed dependencies should make the scan unavailable rather than asserting ownership.
- Repeat with direct C063 sound, multiple emitters, disabled/empty sounds and effects with no audio. One native allocation seen by both hooks should not create duplicate observations. Change AVFX files and verify cache refresh.
- Test normal animation exit, redraw, GPose, territory change, listener toggles and plugin unload. PlaySound and PlayClipSound originals must run exactly once with unchanged arguments; no scheduler hooks are installed.
- Automated tests cover real Luna PAP links and 12 AVFX files (which contain no emitter sound references), synthetic positive sound emitters, exact per-emitter cue matching, disabled/empty sounds, malformed offsets/chunk bounds and C012/C173 bindings. Actual indirect audio capture/attribution needs in-game validation with a sound-bearing VFX.

### 1.3.66 timeline target selection
- In normal gameplay, hard-target and soft-target a player outside all timeline groups. The + button must enable when their animation is readable. Click it, including while the UI clears the soft selection; the captured player's row should still be added.
- Hover a disabled +: it must distinguish no player, already included (name the row/group), and the exact sampling failure. With idle/pose timelines now available, nearby matching idle players can already belong to your row.
- When your own clip is unavailable, + must remain visible so a valid independent target can be added. Test target changes, despawn, territory/GPose changes and closing/reopening between request and processing; no stale player may be added.
- Preserve disjoint matching groups and the existing rule against adding a player already in a row. Native target selection and rendered tooltip behavior require in-game testing.

### 1.3.67 predictive animation support
- Reproduce the reported PredictiveCompressedAnimation with self and a targeted player. The timeline should display actual count/duration, and + should enable for a readable target outside existing groups. Verify first/last frame, seek, pause/play, held stepping and drag pause/resume.
- Compare stored frame count with the original animation where possible. Test a predictive animation beside spline/interleaved clips with matching count/duration, as well as mismatching counts/durations. Only matching nearby players should share control.
- Retest redraw, animation changes, closing/unload, paused clips and single-frame poses. Native control identities must guard speed restoration. The frame-count reader validates dimensions/ranges, while actual predictive layout and playback need runtime confirmation on the affected game clip.
- Pure tests cover stored-count preservation, subset track mappings, float-only and single-frame clips, invalid counts/dimensions and existing media/group policies. No affected predictive PAP was available as an offline fixture.
- Layout reference: https://github.com/soulsmods/DSMapStudio/blob/360245a095eb5db9dc821a213bc41b2b3ff3db0d/HKX2/Autogen/hkaPredictiveCompressedAnimation.cs (64-bit field sequence, applied after the installed 0x38-byte hkaAnimation base).

### 1.3.68 BPM dependency regression
- Run Tests with Luna as first argument and C:\Penumbra\BPM Breakdown as second. Verify face-marked real BPM PAPs retain VFX links and all BPM AVFX variants expose bpmloop/bpmstart at cue 0. Assets are read only and not packaged.
- In-game, enable listening, add the BPM player's timeline, and trigger the dance again. Verify bpmloop matches the active binding and exact cue. Shared users remain ambiguous and DJ exclusions still apply.

### 1.3.69 off-camera manual timelines
- Add a target timeline and turn the camera away without moving characters. The row and its controls should remain available while the native animation is loaded. Test pause, seek, play and returning the camera, including matching group members and GPose.
- Actually leave loaded range, change animation, redraw, zone or unload. Missing/replaced native controls must still be released safely. Automatic sync retains its drawn-character check. This native visibility regression requires in-game confirmation.

### 1.3.70 Penumbra/Mare sound-owner event
- Reload, open timeline and Sound Test, add the other player via +, enable listening, then have them restart the emote. Look for Penumbra owner in the event and Penumbra-owned music match in classification. No mod-variant selection or download is required; the resolved cache file is used.
- Test direct PAP and VFX-origin sounds with two users sharing the same virtual sound path. Each same-call Penumbra event must attach only to its actor/allocation. Unknown/conflicting owners and invalid or reused player/native slots must not get control. Nonzero cues/unsupported codecs and >10-second/DJ restrictions remain unchanged.
- Keep another visible emote unindexable: direct Penumbra-owned controls should not depend on PAP scan completeness. If no same-call owner event arrives (cached/asynchronous or other routing), existing PAP/VFX fallback and ambiguity rules still apply. A standalone resource-load indicator does not create a music row.
- Toggle listening, close either window, unload/reload, change territory/GPose and redraw while controlling. Verify subscriber cleanup, managed snapshot invalidation and per-instance restoration; game calls must run exactly once. No scheduler hooks are introduced.
- Unit checks cover matching and unrelated virtual/resolved/wrapped paths, nested-call restoration, conflicts, cross-thread isolation and cleanup after exceptions. Actual Penumbra callback timing and Mare-origin audio require in-game confirmation.

### 1.3.71 newest music and character-named exports
- Detect music A and then different music B on the same timeline: only B should remain. If A has local control, it must be released safely before replacement. Other timelines keep their own current music. Delayed older events cannot replace a newer row; short intro effects still do not qualify as music.
- Check the title reads Music | duration with no SCD basename. Export from self and an added player's row: the WAV name must contain the owning character name plus a distinct random ID, and existing files must remain untouched. Preview exports use Preview because they have no verified character owner.
- WAV conversion/duration/sample checks and the character/GUID naming check run offline. Actual multi-player detection/replacement and rendered labels need in-game confirmation.

### 1.3.72 restart when adding a timeline
- Without opening Sound Test, target a player outside existing timeline groups and press +. Confirm the target and matching nearby unclaimed players restart from frame zero with their sound cues replayed, even when Perfect Frame Sync is on. No manual animsync, redraw or emote request should be needed. Capture/subscription and new owner snapshot must be prepared before the first restart.
- Check only the added target's matching group restarts: self/earlier rows, distant players, incompatible duration/counts and already-claimed players must remain unchanged. Keep sound-sync off or mark a group member DJ: their sound must not be replayed. Animation start still resets locally.
- The new music row should appear when the sound cue fires. Direct Penumbra ownership has no 750ms polling wait, while PAP-only fallback still waits for its snapshot. Delayed sound cues naturally cannot be detected before their scheduled time; loading/asynchronous capture may still need time.
- Close Sound Test with the timeline open: timeline monitoring continues. Close both windows or disable listening: hooks/subscriptions stop and owned music releases. Test unload, target despawn/territory changes and repeated clicks; only the validated captured player may restart.
- Pure group/permission checks run offline. Actual scheduler replay, VFX sound timing, per-character capture and listener lifetime need in-game validation.

### 1.3.73 automatic per-timeline music control
- Add a player timeline and let the new sound trigger: the music slider/play/pause/release controls must appear automatically without Take Control. Test native instances still loading, then ready within 10 seconds. Retry attempts should not spam logs or mute native audio before the decoder/instance is ready.
- With two independent timelines and different songs, both local controls should stay active. Pause/seek/volume/release/replace one row without affecting the other. Export filenames and newest-only row behavior must remain correct.
- Release a song: native volume returns and the same observation stays released. Resume control or a new detection enables automatic takeover again. Loading/device/seek failures must dispose only that row's local player and restore its lease; stale/reused native instances and ambiguous or DJ-excluded owners must never be captured.
- Disable listening or close both windows, remove a row, logout/zone/GPose-change and unload. All local outputs and only their own native mute leases must clean up. Game EQ is still bypassed by local playback.
- Pure tests cover immediate eligibility, retry rate/window, independent state, explicit release, resume and new-detection reset. WAV/media/ownership/group regressions also run offline. Concurrent device output and native multi-player control need in-game verification.

### 1.3.74 amplified compact music controls
- Drag music Volume through 0/100/150/200 percent: local audio must get louder above 100, with no remaining unity clamp in mix/sample playback. Right-click restores automatic game-channel volume; master mute/attenuation remains effective. Export remains original unamplified audio.
- Confirm the control row has an adequately sized play/pause icon with a tooltip and no Release control button. Pause, seek, looping, multi-row independence and automatic replacement/close/unload cleanup remain functional. Test font scaling and window sizing in-game.
- Pure checks cover manual boost persistence, output gain above unity, master mute/attenuation, finite gain limits and automatic-volume reset. Actual device loudness and rendered layout require in-game confirmation.

### 1.3.75 collapsible added timelines
- Add two independent player timelines. Each header should show the name, Synced | N Player(s), a collapse arrow and an X. Collapse one: hide only its animation/music controls, keep its playback running and shrink window height. Reopen and verify the current state is retained.
- Change nearby group membership while collapsed: the header count should update and expanding should not reset the header's open state. Press X both expanded and collapsed: the added row must disappear and release only its animation/music controls. Closing the whole window still releases all rows.
- The primary self row must have no dropdown and keep its original count/+ arrangement. Added expanded rows must not repeat Synced text beside the transport controls.
- Verify the music play/pause icon fits at default, 125%, 150% and larger font scales. Check new larger button, row alignment and height fitting. Exercise collapse after scrubbing/held stepping: no stuck drag, unexpected resume or continued stepping.
- This layout change is compile-checked. Rendered appearance, header interaction and native release behavior require in-game confirmation.

### 1.3.77 game background sound permissions
- With the screenshot's global background sound setting and BGM checkbox enabled, take automatic control of a music-bus song and tab out: it must keep playing. Test the same at a manually adjusted/boosted volume.
- Disable the global checkbox: all local music output must mute while inactive. Re-enable globally and disable only BGM: music buses must mute while permitted sound-effect/voice/system buses remain audible. Also check Ambient and Performance categories using appropriate sound assets; the actual native bus flags are used rather than filename-based category guesses.
- Return focus and verify inactive-only mutes clear without resetting playback position, volume override, pause state or looping. Explicit channel/master mutes must stay effective regardless of focus and manual volume. Change settings while the local player is running.
- Pure regression checks cover allow/deny combinations, category independence, focused behavior, explicit channel mute and the independent boost/permission rules. Actual native flag mapping and window focus transitions need in-game confirmation.

### 1.3.78 chain animation/music timelines
- Once a timeline's music controls are active, click the chain beside Music. It should highlight green and describe which longer timeline is master. With a 300s song and 30s animation, seek music to 15s/45s/75s: animation should show 15s in each case (frame 450 on a 901-frame/30s clip). The follower animation seek/transport is disabled; music play/pause controls both.
- Test animation longer than music: animation slider/transport controls the clock and music follows modulo its duration. The music seek slider is disabled; its play/pause button still controls the shared state. Equal durations use music as master. Linked playback uses normal real-time speed.
- Drag the master while playing and paused, hold animation stepping when animation is master, seek first/last frames, and cross shorter/master loop boundaries. Both views should show matching shared-clock positions. Audio plays normally with drift correction above 150ms; native/output buffering can limit audible alignment precision.
- Link separate added timelines with different songs, collapse/expand rows and move group neighbors in/out. Links and seek positions must stay independent and only claim each row's current matching group. Preserve sound volume, background settings and DJ exclusions.
- Unlink while playing/paused; current positions stay and normal animation speeds restore. Replace a song, change animation/binding, request manual restart, remove a row, leave loaded range, change territory/GPose, close windows or unload: links must stop and guarded native controls release. No stale animation pointer may be reused. Link state does not persist to a replacement song.
- Unit checks cover master choice, modulo phases, frame mapping, time advance/pause, end/loop handling, clamped/nonfinite seeks, per-row independence, drift at loop edges and invalid durations. Native multi-character playback and audio buffer behavior require in-game testing.

### 1.3.79 compact linked controls and relinking
- Link self and an added timeline: replace both separate timeline sections with a single horizontal control row, containing play/pause, shared master seek, time, chain, 0-200 percent volume and Export Sound. Self retains Synced/+; added headers retain name/count/X. No grey follower transport should remain.
- While linked, detect a different supported song on the same player: after takeover it must link automatically, selecting the longer master again. Repeat with songs shorter and longer than the animation.
- Stop the native music or animation: once its captured instance is no longer playing/valid, local output and active linking should release and normal animation controls return. The character's link preference stays set. Trigger new music on that timeline and verify automatic takeover/relink without another chain click.
- Explicitly unlink, then change song: it must remain unlinked. Close/reopen timelines or replace the player/territory: stale links must not carry to a different character. Test collapse, removal, pause/seek/volume, delayed resource loading, native slot reuse and all multi-row isolation guards.
- In compact mode export status/path appears on hover; right-click opens its folder. Resize and font-scale the one-row UI and ensure all controls fit and height shrinks. Pure clock/media/ownership/group regressions run offline; native music-end detection, replacement and rendered layout require in-game testing.

### 1.3.80 collapsible self and scaled icons
- Collapse/expand your own timeline with music unlinked and linked. Hide the whole control section while playback continues; keep Synced count and + in the self header and verify + can add a player while self is folded. Added timelines retain close X and independent open state.
- At multiple UI font scales verify animation/music/merged play-pause and chain icons are smaller, centered and fully inside standard-height buttons matching Export Sound. Check hit targets, tooltips, held stepping and scrub resume still function.
- Check duplicate self count/+ has disappeared from expanded controls and window height shrinks correctly when all headers are folded. This UI-only change is compile-checked and needs in-game visual confirmation.

### 1.3.81 compact icon rendering regression
- At the reduced UI scale in the reported screenshot, verify play/pause, frame arrows, first/last, chain and + appear as recognizable symbols, not dots. Repeat at default and larger scales; buttons should still match Export Sound's height and tooltips/actions should work.
- Check linked/unlinked, collapsed headers, disabled + and light/dark UI colors. Geometry uses button bounds and text color, so it should stay centered and within each button. Build verification passes; actual visual behavior needs an in-game check.
