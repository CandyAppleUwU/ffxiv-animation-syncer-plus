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
