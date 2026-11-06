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

Animation syncing pauses during cutscenes. Position alignment is additionally
blocked during combat, duties, and loading.

| Command | Action |
| --- | --- |
| `/animsync` or `/animsync everyone` | Sync all eligible loaded characters |
| `/animsync groups` | Sync qualifying nearby groups |
| `/animsync near` | Sync yourself and players inside your selected radius |
| `/animsync target` | Sync yourself and the selected player |
| `/animsync pos` or `/animsync align` | Match nearby position and facing |
| `/animsync toggle` | Toggle automatic synchronization |
| `/animsync ui` or `/animsync settings` | Toggle the menu |

## Build and release

Requires Windows, .NET 10 SDK, and Dalamud API 15 development libraries under
`%APPDATA%/XIVLauncher/addon/Hooks/dev`, or set `DALAMUD_HOME` to that directory.

```powershell
./prepare-release.ps1
```

This runs checks, builds the ZIP, and generates `repo.json` from the actual
compiled manifest. It does not publish anything. The current release tag is
`v1.3.40.0` and its asset must be named **AnimationSyncerPlus.zip**.

For the first publication:

1. Create the public GitHub repository `CandyAppleUwU/ffxiv-animation-syncer-plus`
   with branch `main` and push this source, including `repo.json` and `images/icon.png`.
2. Create release **v1.3.40.0** and upload `dist/AnimationSyncerPlus.zip`.
3. Verify the release download, icon URL, and raw repository index before
   sharing the installation link.

For later updates, bump the project version, run the script, publish the
matching versioned release asset, and push the updated `repo.json`. Keep the
internal plugin name `AnimSync` unchanged so existing installations/settings
continue to work.

## Credits and license

Original Animation Syncer: Copyright 2024 Sevii. Modifications: CandyApple.
Distributed under the Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
