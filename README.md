# SplitTrack

**A Windows game recorder that keeps every audio source on its own track.**

Game, Discord, your mic and the rest of your PC are recorded separately. When you export a clip you
choose what goes in and how loud: drop your friends' voices, turn the game up, or mute yourself.
You don't need virtual audio cables or any routing setup.

<!-- Add a screenshot or GIF here, e.g. ![SplitTrack](docs/screenshot.png) -->

## Why

Most recorders mix everything into one audio track. That makes it hard to share a clip without the
Discord call in it, or to fix a mic that was too loud. SplitTrack captures each program's audio on
its own track (Windows *process loopback*), so you decide on the mix after the fact.

## Features

- **Separate audio tracks.** Game, Discord (or any app), microphone and system sound each get their own track.
- **Replay buffer.** The last 60 seconds (or whatever length you set) are always kept in memory. Press a hotkey to save them as a clip.
- **Full recordings** start and stop with a hotkey, and they run alongside the replay buffer.
- **Library** of your clips and recordings, with thumbnails.
- **Player with a live mixer.** Untick a track or change its volume (0–400 %) and you hear the change immediately. What you hear is what gets exported.
- **Export to MP4** with trimming. You can set a **size limit** (e.g. 10 MB for Discord without Nitro), then use *Copy file* and paste the clip straight into Discord.
- **Hardware encoding** on AMD (AMF), NVIDIA (NVENC) or Intel (QSV), with x264 on the CPU as a fallback.
- **Finds your game on its own.** By default it records whatever is running full screen. Presets can switch settings automatically per game.
- **Sits in the tray.** It can start with Windows and runs without admin rights.

## Requirements

- **Windows 11, 64-bit.** Strictly, any Windows build 20348 or newer; per-app audio capture needs it. Windows 10 is not supported.
- A GPU with an H.264 encoder is recommended (pretty much any AMD, NVIDIA or Intel GPU from the last several years). Without one, SplitTrack falls back to encoding on the CPU.

## Install

1. Download `SplitTrack-Setup-<version>.exe` from the [Releases](https://github.com/AldoCariaga/SplitTrack/releases) page.
2. Run it. The setup asks for admin rights once, to install into `C:\Program Files\SplitTrack`
   (you can choose another folder). SplitTrack itself runs without admin rights.
3. Start SplitTrack from the last page of the setup or from the Start menu.

To **update**, run a newer setup. It upgrades in place and keeps your settings and recordings.
To **uninstall**, go to *Settings → Apps*. Uninstalling keeps your settings, logs and recordings.

> Windows SmartScreen may warn about the installer because it is not code-signed. Click
> *More info → Run anyway* if you trust the download.

## Quick start

1. Open SplitTrack. It appears in the notification area (tray). Closing the window keeps it running
   there; **Exit** is in the tray icon's menu.
2. Go to **Settings** and check the audio tracks:
   - **Game** is set to `auto`, which picks the program running full screen. If your game runs in a
     window, add it with **Add…**, which lists running programs.
   - Add an **App** track for `Discord.exe` if you want voice chat on its own track.
   - Under **Mic**, pick your headset if it isn't the Windows default.
   - Click **Save and apply**.
3. Play. The level meters at the bottom of the window show each track picking up its own sound.
4. Press **Alt+F10** after a good moment to save a clip. Press **Alt+F9** to start or stop a full recording.
5. Open the clip from the **Library**. Untick or turn down the tracks you don't want, set a start and
   end, and click **Export**.

## Using the app

### Hotkeys

| Action | Default |
|---|---|
| Save a clip from the replay buffer | `Alt+F10` |
| Start / stop a recording | `Alt+F9` |

You can change both in **Settings**. The hotkeys work while a game has focus. If another program
already uses one (NVIDIA's Instant Replay uses Alt+F10, for example), SplitTrack shows a
notification so you know to pick a different key.

### The window

- **Recorder bar** (along the bottom): the current state, with a green line while the replay buffer
  runs, red while recording and amber if capture failed. It also shows what is being captured, one
  level meter per track, and the **Replay buffer**, **Record** and **Save clip** buttons. The same
  actions are in the tray menu.
- **Library**: clips and recordings, newest first. Filter by *All / Clips / Recordings*. Double-click
  (or press Enter) to open one. Right-click to show it in its folder or delete it (it goes to the
  Recycle Bin).
- **Settings**: capture target (monitor or window), quality, encoder, audio tracks, clip length,
  hotkeys, presets and Start with Windows.

A recording starts within a second of the key press, on the next keyframe. With the replay buffer
off, capture runs only while you record.

### Player and export

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `←` / `→` | Skip 5 s (with `Shift`: 1 s) |
| `I` / `O` | Set the start / end of the part to export |

- **Tracks**: untick a track to leave it out, or drag its volume (double-click to reset to 100 %).
- **Export to MP4** writes the file next to the recording. The options are:
  - **Size limit**: keeps the file under that many MB, re-encoding only if it has to.
  - **Resolution**: scales the video down.
  - **Start exactly at the start point**: by default the cut starts on the nearest keyframe (up
    to 1 s early), which is almost instant. Tick this to cut on the exact frame, at the cost of a
    slower re-encode.
- **Copy file** puts the export on the clipboard, ready to paste into Discord or a folder.

Most exports finish in a second or two because the video is copied as it was recorded. It is only
re-encoded for a size limit, a lower resolution or an exact cut.

### Presets

A preset is a capture setup: video settings, audio tracks and the replay buffer. The library folder,
hotkeys and Start with Windows are shared by all presets.

- In **Settings → Presets** you can create presets (each starts as a copy of the one shown), delete
  them, and choose which one the page edits.
- **Auto-switch for** lists the games that select a preset. When one of them starts, SplitTrack
  switches to that preset, and when it closes, back to the default preset. A recording is never cut
  off; the switch waits until it ends.
- With two or more presets, the tray menu has a **Preset** submenu to switch by hand.
- Switching presets restarts capture, so the replay buffer starts empty.

## What each track records

| Track | What it records |
|---|---|
| **Game** | The game's audio only. `auto` picks the program whose window is in front and covers the whole monitor (exclusive or borderless full screen; a maximized window doesn't count). It keeps that program after Alt+Tab until another one goes full screen or it exits. Browsers and programs on an App track are never picked. The game may start after the recording does, and restart during it. |
| **App** (e.g. Discord) | Only that program, including its child processes. |
| **Mic** | Your default microphone, or the one you picked. It follows device changes and replugs. |
| **System** | Everything else: your PC's audio *minus* the game and the app tracks. |

All tracks start and end at the same moment and stay in sync, even in long recordings. A source
that is silent or not running is filled with silence.

**Limits of the System track.** Windows can only leave one program out of a single capture. So while
an App track records, SplitTrack captures every other program separately and mixes them together.
This works well, but:

- Windows' own sounds (error and notification dings) usually aren't tied to a program, so they are
  missing from System.
- Programs that launched the game, an app on an App track, or SplitTrack itself (Windows Explorer,
  for example) can't be captured on their own, so their sounds are left out.
- When a program plays its first sound, up to about a quarter of a second of it can be lost.

If those sounds matter more to you, untick **Leave app tracks out** on the System track. System then
records everything except the game in one capture. The catch is that Discord is on both the Discord
and System tracks, so you would hear it twice if you export both.

## Where things are stored

| What | Where |
|---|---|
| Clips and recordings | `Videos\SplitTrack`, one subfolder per recording (you can change this in Settings) |
| Settings | `%LOCALAPPDATA%\SplitTrack\settings.json` |
| Logs | `%LOCALAPPDATA%\SplitTrack\logs` |

Each recording is an `.mkv` file with one video track and one AAC audio track per source. You can
open it in VLC and switch tracks under *Audio → Audio Track*. Next to it are a `.json` sidecar
describing the tracks and a `.jpg` thumbnail. Exports are saved in the same folder.

A 60-second replay buffer at the default 20 Mbps uses about 150 MB of RAM.

## Troubleshooting

- **A hotkey doesn't work.** Another program probably owns it (NVIDIA Instant Replay uses Alt+F10).
  Choose a different hotkey in Settings.
- **The Game track is silent.** Your game may run in a window, so `auto` doesn't pick it. Add its exe
  to the Game track's list in Settings.
- **I hear Discord twice in an export.** You exported both Discord and a System track that contains
  Discord (recorded with *Leave app tracks out* off). Untick one of them.
- **Which encoder is used?** `auto` picks your GPU's encoder (AMF, NVENC or QSV) and falls back to
  x264. You can force one in Settings.
- **Something else.** Check the logs in `%LOCALAPPDATA%\SplitTrack\logs` and
  [open an issue](https://github.com/AldoCariaga/SplitTrack/issues) with them attached.

## Building from source

Requires the .NET 10 SDK on Windows 11 x64:

```powershell
powershell -File tools\get-ffmpeg.ps1   # downloads the FFmpeg DLLs (not in git)
dotnet build -c Release
dotnet run --project src\SplitTrack.App -c Release
```

## Contributing

Issues and pull requests are welcome. If you're reporting a bug, please attach the logs from
`%LOCALAPPDATA%\SplitTrack\logs`.

## Credits

- [FFmpeg](https://ffmpeg.org/) (GPL build by [BtbN](https://github.com/BtbN/FFmpeg-Builds)), with x264
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)
- [CsWin32](https://github.com/microsoft/CsWin32)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)
- [Inno Setup](https://jrsoftware.org/isinfo.php)

## License

Copyright (C) 2026 Aldo

SplitTrack is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License v3.0](LICENSE) or (at your option) any later version. It is
distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; see the license for
details.

The installer also ships FFmpeg, a GPL build that includes x264. Its source code is available from
[FFmpeg](https://ffmpeg.org/download.html) and [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds).
