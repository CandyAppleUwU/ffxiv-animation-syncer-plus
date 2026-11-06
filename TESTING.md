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
