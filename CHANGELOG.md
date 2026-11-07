# Changelog

## 1.3.100.0

- Add Animation Timeline with frame scrubbing, play and pause, stepping, and separate timelines for targets and their groups. Animation speed ranges from −500% to +500%.
- Detect supported music while the timeline is open. Music chains automatically and has shared volume, equalizer presets, and reverb controls.
- Support predictive and quantized animation clips, improve short cue timing, and preserve playback state when music relinks.
- Limit position syncing to eligible poses and resident idle or move animations.

## 1.3.81.0 (local experimental build)
- Fix compact icons appearing as dots at reduced UI scales. Draw play, pause, step, skip, link and add symbols directly at their button size instead of scaling icon-font text.
- Retain the standard button height and existing tooltips/actions.

## 1.3.80.0 (local experimental build)
- Make the primary self timeline collapsible too, with its group count and always-accessible + in the header. Added rows retain their close X; collapsing any row keeps playback/linking running.
- Use standard text-button height for transport/link icons and draw smaller, centered glyphs fitted inside the padding. This matches Export Sound's height instead of enlarging buttons to fit the original icon font.
- Remove duplicate self count/+ from the transport row now that both live in the header.

## 1.3.79.0 (local experimental build)
- Replace separate linked animation/music controls with one compact row: shared play/pause, longer-master seek bar/time, chain, volume and sound export. Keep self's Synced/+ access and added timelines' collapsible name/count/X headers.
- Remember requested linking on the character timeline across music replacement or temporary absence. Release the active link when captured music ends; link a newly detected song automatically when controls and animation are available. Explicit unlink or closing the timelines clears the preference.
- Export progress/path moves into its tooltip in compact mode; right-click Export Sound to open the output folder.

## 1.3.78.0 (local experimental build)
- Add a chain toggle beside Music. The longer animation/music timeline supplies the initial clock and seek controls; the shorter follows elapsed time modulo its duration. Equal durations use music as master.
- Linked playback uses a real-time clock, drives the matching group's animation controls, and keeps local audio aligned with bounded drift correction. Play/pause and dragging the master slider affect both; the follower seek controls are disabled.
- Links are independent per row. Collapsing does not stop links; unlink, replacement, manual restart, invalid animation, row removal or closing safely releases linked animation control. Game EQ remains outside local audio playback.

## 1.3.77.0 (local experimental build)
- Fix the reversed global background-mute check in local music output. Apply the native sound manager's global and per-bus inactive-mute flags, matching GetEffectiveVolume's predicate.
- Apply channel mute/background permissions even after manually adjusting or boosting music volume. Keep playback position advancing while muted so returning to the game does not restart the song.

## 1.3.76.0 (local experimental build)
- Move the music play/pause icon to the left of the time display, with the time vertically centered beside the button and volume remaining on the right.

## 1.3.75.0 (local experimental build)
- Size the music play/pause icon button using both horizontal and vertical font padding plus extra margin.
- Added player timelines use collapsible headers containing their character name, matching-player count and close X. Collapsing hides animation/music controls and reduces window height while playback continues; X removes the row and releases its controls.
- Keep the primary self timeline layout and its Synced count unchanged. Added rows no longer repeat the count next to playback controls. Collapsing cancels held stepping and finishes active scrubbing safely.

## 1.3.74.0 (local experimental build)
- Raise per-song manual volume to 200% and allow amplified gain through mix calculation and local sample playback. Automatic game-channel/master levels retain their normal range.
- Replace music text play/pause with an icon and remove Release control. Automatic replacement/close/unload cleanup still restores native instances.

## 1.3.73.0 (local experimental build)
- Automatically take local control of newly detected eligible timeline music without a Take Control click. Wait/retry for loading native instances at 250ms intervals for up to 10 seconds; failures leave the original sound unmodified/restored with a status and retry action.
- Support one independent local music control per timeline instead of one global player. Replacing, releasing, invalidating or failing a row does not release another row's music. Closing/disabling/unloading still cleans up all instances.
- Explicit Release control suspends takeover for that detection; Resume control or a new song enables it again. Preserve ownership, DJ, duration and allocation checks, and existing playback/export behavior.

## 1.3.72.0 (local experimental build)
- Adding a timeline arms music detection, registers the new anchor and restarts only that target and their matching unclaimed nearby group from the beginning using local timeline replay. Sound toggle and DJ exclusions remain enforced; Perfect Frame Sync does not override this explicit restart.
- Sound listening can run with the timeline alone, without opening Sound Test. It stops when both windows close or listening is disabled. Direct Penumbra-owned detections no longer wait for the PAP inference polling delay.

## 1.3.71.0 (local experimental build)
- Keep only the newest qualifying music detection per timeline and release that row's previous local control. Delayed older triggers cannot replace newer music.
- Show Music followed by duration without exposing the SCD filename.
- Name detected-sound WAV exports after the source character plus a full random GUID. Independent file-preview exports use Preview instead of asserting a player owner; existing exports are never overwritten.

## 1.3.70.0 (local experimental build)
- Subscribe to Penumbra.GameObjectResourcePathResolved (the event used by Mare's sound indicator) and correlate matching SCD paths within the same thread-local native sound call.
- Attach the player-specific resolved sound path and managed owner snapshot to the captured allocation. Use that evidence for self/added timelines without requiring a PAP dependency match; retain guarded native allocation and DJ checks.
- Reject conflicting owner/path evidence; restore nested-call context in finally blocks. Unsubscribe/clear owner snapshots on listener stop, close and disposal. PAP/VFX inference remains a fallback when no matching owner event occurs inside the sound call.

## 1.3.69.0 (local experimental build)
- Keep manual timeline controls available for off-camera characters while their draw object, animation control and timing remain loaded. Automatic sync retains its drawn-character requirement.
- Existing control identity and unload checks still release stale controls if a character leaves loaded range or its animation data is replaced.

## 1.3.68.0 (local experimental build)
- Preserve sound/VFX references in PAP entries marked Face. BPM Breakdown uses such entries for its animation-to-VFX sound chain; skipping them prevented bpmloop.scd from matching.
- Keep active body-binding, exact path/cue, ownership ambiguity and DJ checks unchanged.

## 1.3.67.0 (local experimental build)
- Read stored frame counts from 64-bit Havok PredictiveCompressedAnimation clips, with frame-count and track/skeleton-dimension validation.
- Shared sampling now recognizes predictive clips for manual timeline selection, target addition, matching groups and eligible automatic sync. Existing emote exclusions and native-control lifetime guards remain unchanged.

## 1.3.66.0 (local experimental build)
- Recognize soft-selected targets in the timeline + button. Validate an add request against the captured player even if clicking the UI clears the selection.
- Explain disabled + buttons on hover, including an already-included group or the actual animation sampling failure. Keep + accessible when your own animation is unavailable.

## 1.3.65.0 (local experimental build)
- Follow the active PAP binding's C012/C173 VFX references, resolve each AVFX through the player's Penumbra collection, and parse each emitter's SdNm/SdNo sound path/cue pair.
- Add the VFX/clip sound playback route to trigger monitoring with allocation tickets and duplicate-instance filtering. Preserve original native calls and leave animation schedulers untouched.
- Cache VFX dependencies with file-change checks and bound scan sizes. Missing/malformed dependencies withhold ownership inference. Shared player matches remain ambiguous; music duration/codec/cue and DJ requirements remain unchanged.

## 1.3.64.0 (local experimental build)
- Manual animation timelines read the character's loaded body clip without automatic emote exclusions. Non-emotes, pose/start clips, paused clips, mounted riders and single-frame clips can be selected when their loaded timing is valid.
- Use the same manual sampling for self, target addition, matching groups and ongoing control validation. Display actual sampling failures rather than calling every unavailable clip an unsupported emote. Automatic sync retains its exclusions.

## 1.3.63.0 (local experimental build)
- Add Export Sound to detected music rows and selected sound previews. Decode the full supported SCD once to standard PCM16 WAV at its original sample rate/channel count, independently of current playback.
- Export in the background to Documents/Animation Syncer Plus/Exported Sounds with unique filenames, a saved-path message and Open folder button. Never overwrite earlier exports; remove incomplete files on conversion failure.

## 1.3.62.0 (local experimental build)
- Generalize sound capture to arbitrary SCD paths, including triggers arriving before a new PAP is indexed. Reserve a queue for known PAP paths independently of unrelated sound events.
- Rename the listener to PAP sound triggers. The preview folder no longer filters detection. Exact player-specific PAP path/cue matching and ambiguity/DJ/10-second rules remain required.
- Direct C063 references from active emote body bindings are supported; indirect VFX sound references, other trigger routes, nonzero cues and unsupported SCD codecs remain limitations.

## 1.3.61.0 (local experimental build)
- Place music volume on the right of the time/play-pause/release row. Remove the channel/automatic-volume status line and extra local-playback line; right-click Volume to resume game volume following.
- Fit the minimum timeline width to all music controls. Local audio still bypasses the game's equalizer; native arbitrary-time seeking is not implemented.

## 1.3.60.0 (local experimental build)
- Local music takeover loops the full track continuously, including while the animation has a different duration.
- Detects the exact SCD cue's audio bus and initializes a per-sound volume slider from the game's effective channel volume. Automatic volume follows that channel until manually adjusted; Use game volume restores automatic behavior.
- Local output applies game master volume/background muting and the sound's entry/instance gain. No game channel settings are changed. Native positional mixing and bus-ducking effects are not recreated.

## 1.3.40.0

First public release of Animation Syncer Plus by CandyApple, based on
Animation Syncer by Sevii.

- Automatic nearby animation grouping with configurable distance and refresh time.
- Body/facial control synchronization, optional sound timeline restarts, and
  automatic local sound recovery after Penumbra redraws.
- Saved DJ sound exclusions, searchable player list, and target-based addition.
- Optional experimental matching to the player furthest ahead in the animation.
- Manual Sync Groups, Sync Everyone, Sync Near You, and Sync Target actions.
- Guarded position/facing alignment within 0.5 yalms via pos/align commands.
- Settings window, range preview, button tooltips, Discord link, and plugin icon.
- Cutscene pause, animation exclusions, and 94 standalone validation checks.

Experimental frame matching does not seek audio. Position alignment is blocked
during combat, duties, cutscenes, and loading.

## 1.3.41.0
- Added Same time sync below Perfect Frame Sync, with a saved shared phrase and party/target chat buttons.
- Manual matching party/tell messages also trigger; outgoing events wait 300 ms and incoming events run next update.
- Restricts restarts to the tell pair or loaded party, preserves sound/DJ and cutscene rules, and limits repeated triggers.

## 1.3.42.0
- Restrict the cutscene/GPose pause to automatic group syncing, redraw recovery, and position alignment. Manual animation commands and buttons remain available.
- Manual GPose commands select the visible GPose copies and GPose target; keep the settings window visible in cutscenes and GPose.

## 1.3.43.0 (local experimental build)
- Added `/animsync timeline` and an Animation Timeline button below Perfect Frame Sync.
- Added a live frame slider, Play/Pause, first-frame and previous/next-frame controls, with self-only selection by default.
- Optional matching groups include nearby visible players with equal frame counts and duration, including GPose copies. Restores playback speeds on release and isolates timeline actors from automatic sync/redraw restarts.
- Timeline scrubbing and playback controls affect visual animation only; sound remains independent.

## 1.3.44.0 (local experimental build)
- Removed the timeline's bottom description and matching-group toggle.
- Matching nearby players are always included in timeline actions.
- Reduced the timeline window height for a compact layout.

## 1.3.45.0 (local experimental build)
- Centered icon-only playback controls on the same row as the time display, with time on the left.
- Replaced Play/Pause and frame-step text buttons with icons and hover tooltips; removed the slider's Frame prefix.
- Reduced timeline height to fit the compact three-row layout.

## 1.3.46.0 (local experimental build)
- Size timeline buttons using actual icon-font dimensions with padding, including both Play and Pause to prevent resizing between states.
- Keep time vertically centered beside the controls and adapt minimum window height to the control row.

## 1.3.47.0 (local experimental build)
- Explicitly align every timeline button to the same row to avoid the time label shifting later controls downward.
- Added a rightmost last-frame button that seeks to the final frame and pauses; keep the five-button group centered.

## 1.3.48.0 (local experimental build)
- Moved timeline participant status to the right of the time/control row and renamed it Synced, with Player for one and Players for more than one.
- Keep controls centered and reserve enough width for the left time and right status labels.

## 1.3.49.0 (local experimental build)
- Fit the timeline window height exactly to the slider and playback row, overriding previously saved tall heights.
- Correct measured constraints for Dalamud UI scaling and shrink the no-animation status view too. Horizontal resizing remains available.

## 1.3.50.0 (local experimental build)
- Pause selected players while the timeline slider is being adjusted, holding the chosen frame instead of competing with live playback.
- Resume on release only if playback was running before the drag. Cancel stale resume requests on animation changes, manual resets and window closure.

## 1.3.51.0 (local experimental build)
- Hold Next for 0.5 seconds to step forward at approximately five frames per second, stopping on release or the final frame.
- Added a target-only + button to the right of your synced count. Creates a named, independently controlled timeline for a supported target outside existing groups, including matching players near that target.
- Added row removal and guarded cleanup, separate per-row playback/scrub/repeat state, and deterministic group ownership to prevent duplicate control of the same actor.

## 1.3.52.0 (local experimental build)
- Added hold-to-repeat to Previous with the same 0.5-second delay and five-frame-per-second rate as Next, stopping at the first frame.

## 1.3.53.0 (local sound test)
- Added `/animsync soundtest` and an experimental test window with an independent local SCD audio preview: load, seconds slider, pause/resume, stop and volume.
- Added an opt-in, read-only monitor for Luna sound trigger calls. Native player ownership, live pause and native seeking remain unverified and are not exposed.
- Added bounded single-entry unencrypted Vorbis SCD parsing, decoder seek tests against all supplied Luna SCDs, packaged audio dependencies and license notices. No mod assets are shipped.

## 1.3.54.0 (local sound ownership test)
- Added experimental synchronous sound-owner capture around animation scheduler ProcessAll calls, scoped only to the exact player anchoring each open timeline row.
- Music metadata rows require a matching current player identity, a resolved supported SCD duration strictly over 20 seconds, and no DJ exclusion. Unknown owners/durations are skipped rather than inferred from sound category, filename or proximity.
- Resolve virtual sound paths through the captured player's Penumbra collection. Missing, async or indirect ownership remains unsupported; native sound playback is unchanged and native seeking is still not enabled.

## 1.3.55.0 (local regression fix)
- Removed the experimental scheduler ProcessAll interception introduced in 1.3.54 after a report that enabling sound listening prevented animation exit.
- Restored the sound listener to path observation only. Owner assignment and automatic music rows remain unavailable rather than guessing; strict duration/DJ/ownership rules remain for a future verified capture path.
- Sound preview controls remain independent of game animation playback. Native animation exit behavior needs in-game regression confirmation.

## 1.3.56.0 (local PAP inference test)
- Infer sound ownership from the PAP attached to each visible player's active body binding, using parsed C063 sound paths/indices and player-specific Penumbra resource resolution.
- Require exactly one matching player across indexed visible/recent sources, not just timeline anchors. Ambiguous matches, incomplete scans, unrelated basenames and stale identities remain unassigned.
- Lower the music threshold to strictly over 10 seconds. Qualifying observations are explicitly labeled as inferred; live audio seeking remains unavailable.
- Keep animation scheduler interception removed. Sound observation and PAP polling do not mutate animation playback.

## 1.3.57–1.3.58 (local music takeover test)
- Added Take Control beneath inferred music rows, with local seconds seeking and play/pause plus Release control. The local player substitutes for one specifically captured game sound instance, which is muted without changing bus/category volume.
- Capture pending allocation tickets at SoundManager.PlaySound return and track SoundManager.AcquireSoundData generations so loading can finish before takeover. Reused pool slots invalidate old tickets; unknown allocations remain unavailable.
- Guard native access by pool bounds/alignment, active/loading state, resource pointer, sound ID/index and allocation generation. Restore original volume only for the same still-current instance if its volume remains zero.
- Keep playback/seek separate from native seeking. Releasing restores volume at the native instance's current position, not the local seek position. One local music control is supported at a time.
- Use sample-level volume for the local decoder instead of changing the Windows audio device volume.

## 1.3.59.0 (local sound handle correction)
- Treat SoundController.SoundId as an opaque signed 32-bit token: its high bit can be set for a valid playing sound. Removed the erroneous negative-ID rejection that blocked takeover despite active=True, loading=False and resource=True.
- Retain exact token comparison, pool/allocation/resource/index guards and require IsPlaying before muting. No animation scheduler hooks are introduced.
