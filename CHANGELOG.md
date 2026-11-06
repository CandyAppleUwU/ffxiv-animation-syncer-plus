# Changelog

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
