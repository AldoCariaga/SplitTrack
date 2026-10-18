# SplitTrack

A Windows game recorder that keeps **one audio track per source** (game, Discord, mic, system), so
you can choose later which ones end up in the exported clip. Per-app audio comes from WASAPI
process loopback, so no virtual audio cables are needed.

Status: **Milestone 7, the app** (`src/SplitTrack.App`, `SplitTrack.exe`): a tray app that keeps a
replay buffer running, saves clips and starts/stops recordings with hotkeys, shows the library as
a thumbnail grid, plays a recording with every track mixed live at its own volume, and exports
the part you choose to MP4 (optionally under a size limit, e.g. Discord's 10 MB). Its settings
page edits the same `settings.json` the command-line tools use. The command-line tools are still
there: `tools/SplitTrack.RecordCli` (record / replay), `tools/SplitTrack.ExportCli` (export) and
the milestone 1 and 2 proofs of concept (`tools/SplitTrack.AudioPoc`, `tools/SplitTrack.VideoPoc`).

## Requirements

- Windows 11, or Windows build 20348 or newer (process loopback needs it), on x64.
- Building: .NET 10 SDK. Running the published exe needs nothing installed.
- No admin rights to run (the installer asks for them once).

## Build, run, publish

The video tools need FFmpeg's DLLs, which are not in git. Download them once (BtbN's 9.0 shared
build, checked against its SHA-256; FFmpeg.AutoGen 9.0 is bound to exactly that major version):

```
powershell -File tools\get-ffmpeg.ps1
dotnet build -c Release
```

```
dotnet run --project src\SplitTrack.App -c Release
dotnet publish src\SplitTrack.App -c Release -o out\app

dotnet run --project tools\SplitTrack.AudioPoc -c Release -- list
dotnet run --project tools\SplitTrack.AudioPoc -c Release -- record --process msedge.exe --mic --seconds 15 --out C:\Temp\poc
dotnet publish tools\SplitTrack.AudioPoc -c Release -o out\audio-poc

dotnet run --project tools\SplitTrack.VideoPoc -c Release -- list
dotnet run --project tools\SplitTrack.VideoPoc -c Release -- record --monitor 0 --seconds 15 --out C:\Temp\poc
dotnet publish tools\SplitTrack.VideoPoc -c Release -o out\video-poc

dotnet run --project tools\SplitTrack.RecordCli -c Release -- list
dotnet run --project tools\SplitTrack.RecordCli -c Release -- record --game msedge.exe --mic --system --seconds 15 --out C:\Temp\poc
dotnet run --project tools\SplitTrack.RecordCli -c Release -- replay --game msedge.exe --mic --system --buffer 30 --out C:\Temp\poc
dotnet run --project tools\SplitTrack.RecordCli -c Release -- settings
dotnet publish tools\SplitTrack.RecordCli -c Release -o out\record-cli

dotnet run --project tools\SplitTrack.ExportCli -c Release -- info C:\Temp\poc\<name>_rec.mkv
dotnet run --project tools\SplitTrack.ExportCli -c Release -- export C:\Temp\poc\<name>_rec.mkv --tracks Game,Mic
dotnet publish tools\SplitTrack.ExportCli -c Release -o out\export-cli
```

`out\audio-poc\SplitTrack.AudioPoc.exe` (about 45 MB, self-contained) is the only file to copy to
another PC. For video, copy `out\video-poc\SplitTrack.VideoPoc.exe` **and** the `ffmpeg` folder
next to it (about 185 MB together); the same goes for `out\record-cli\SplitTrack.RecordCli.exe`.
`out\export-cli\SplitTrack.ExportCli.exe` needs its `ffmpeg` folder too, which there also holds
`ffmpeg.exe` and the DLLs it loads (about 225 MB together). The app is the same:
`out\app\SplitTrack.exe` (about 85 MB) plus its `ffmpeg` folder, about 260 MB together.

### Installer

For the app there is an installer, so only one file needs copying. It needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup --scope user`):

```
powershell -File tools\build-installer.ps1
```

It publishes the app and writes `out\installer\SplitTrack-Setup-<version>.exe` (about 120 MB; the
version is `<Version>` in `src\SplitTrack.App\SplitTrack.App.csproj`). The setup asks for admin
rights once and installs into `C:\Program Files\SplitTrack` by default; its Destination page lets
you pick any other folder. FFmpeg goes in the `ffmpeg` subfolder, next to `SplitTrack.exe` and an
`Uninstall SplitTrack` shortcut, and there is a Start menu shortcut. SplitTrack itself runs without
admin rights. Running a newer setup upgrades in place (same folder, no Destination page);
both setup and uninstall (Settings → Apps) first close a running SplitTrack, letting a recording
finish. Uninstalling removes the program and the Start with Windows entry, and keeps
`settings.json`, the logs and the recordings.

## The app

Start `SplitTrack.exe`. It sits in the notification area (the tray icon is the plain logo when
idle, with a green dot while the replay buffer runs, red while recording, orange when capture failed); closing the
window keeps it running there, and Exit is in the tray icon's menu.

- **Top of the window**: what is being captured, one level meter per audio track with what it is
  recording right now (exe or device), and the buttons Save clip, Start/Stop recording and Replay
  buffer on/off. The same actions are in the tray menu and on the hotkeys (default Alt+F10 saves
  a clip, Alt+F9 starts and stops a recording). A balloon reports each saved clip or recording,
  and problems such as a hotkey another program owns.
- **Recording** starts on the next keyframe, at most a second after the press, and runs while the
  replay buffer keeps going; with the replay buffer off, the capture starts for the recording only.
- **Library**: clips and recordings, newest first, with thumbnails. Double-click (or Enter) opens
  one; right-click to show it in its folder or delete it (to the Recycle Bin).
- **Player**: plays the file with every audio track mixed live; untick a track or move its
  volume (0–400 %, double-click for 100 %) and you hear the change at once. What you hear is what
  gets exported. Space plays and pauses, arrows skip 5 s (Shift: 1 s), I and O set the part to
  export. Export writes an MP4 next to the recording; Copy file puts it on the clipboard to paste
  into Discord.
- **Settings**: everything in `settings.json` (capture monitor or window, quality, encoder, the
  audio tracks with pickers for running programs and microphones, clip length, hotkeys) and Start
  with Windows. Save and apply restarts the capture with the new settings (after a running
  recording ends).

Options: `--settings <file>` uses another settings file, `--minimized` starts in the tray (what
Start with Windows uses), and `--exit` closes a running SplitTrack cleanly (a running recording is
finished first). Starting it a second time brings the open one to the front.

## RecordCli commands

```
SplitTrack.RecordCli list
SplitTrack.RecordCli record [--monitor <n> | --window <hwnd|exe|title text> | --no-video]
                            [--fps 60] [--bitrate <Mbps, 20>] [--height <px>] [--encoder auto|amf|nvenc|qsv|x264]
                            [--cursor | --no-cursor] [--adapter <n>]
                            [--game <exe[,exe...]>] [--app <Title>=<exe[,exe...]>]... [--mic [name[,name...]]]
                            [--system] [--no-audio] [--audio-bitrate <kbps, 160>]
                            [--seconds <n>] [--out <dir>] [--settings <file>]
SplitTrack.RecordCli replay [same options as record] [--buffer <s, 60>] [--hotkey <keys, Alt+F10>] [--record | --no-record]
SplitTrack.RecordCli settings [--settings <file>]
SplitTrack.RecordCli settings save [options of record and replay, except --seconds]
```

### Settings

`record` and `replay` start from `%LOCALAPPDATA%\SplitTrack\settings.json` (or `--settings <file>`);
options on the command line override it for that run only. Without a file the defaults apply:
primary monitor at 60 fps and 20 Mbps, encoder auto, tracks Game (`auto`), Mic and System, a 60 s buffer, Alt+F10.
`settings` prints what is in effect; `settings save <options>` writes options into the file, e.g.

```
SplitTrack.RecordCli settings save --game VALORANT-Win64-Shipping.exe,cs2.exe --app Discord=Discord.exe --mic --system --hotkey Ctrl+Shift+F8
SplitTrack.RecordCli replay
```

The file can also be edited by hand (comments and trailing commas are fine; unknown keys and
out-of-range values are reported with their line and name):

```jsonc
{
  "version": 1,
  "libraryFolder": null,                 // null: Videos\SplitTrack
  "video": {
    "enabled": true,                     // false: audio only
    "monitor": 0,
    "window": null,                      // window handle, exe or title text instead of the monitor
    "fps": 60,
    "bitrateMbps": 20,
    "maxHeight": null,                   // e.g. 1080 to scale a 1440p monitor down
    "encoder": "auto",                   // auto, amf, nvenc, qsv, x264
    "cursor": true,
    "adapter": 0
  },
  "audio": {
    "bitrateKbps": 160,
    "tracks": [                          // in this order in the file
      { "title": "Game", "kind": "game", "processes": ["VALORANT-Win64-Shipping.exe", "auto"] },
      { "title": "Discord", "kind": "app", "processes": ["Discord.exe"] },
      { "title": "Mic", "kind": "microphone", "devices": ["Tamago"] },   // no devices: default mic
      { "title": "System", "kind": "system" }
    ]
  },
  "replay": {
    "enabled": true,                     // the app keeps the replay buffer running (RecordCli ignores this)
    "bufferSeconds": 60,
    "alsoRecord": false                  // RecordCli replay only
  },
  "hotkeys": {
    "saveClip": "Alt+F10",
    "toggleRecording": "Alt+F9"          // the app only; null or "" for none
  }
}
```

On the command line, any of `--game`, `--app`, `--mic`, `--system` and `--no-audio` replaces the
whole track list (in the order Game, apps, Mic, System); the other options change one value each.

### Tracks

Files go to the library folder, `Videos\SplitTrack` unless the settings or `--out` say otherwise.
`record` writes `<ts>_rec.mkv`: the video as in VideoPoc, then one AAC track (48 kHz stereo) per
audio track:

| Kind (option) | Track title | What it records |
|---|---|---|
| `game` (`--game X,Y`) | `Game` | The first of the process trees X, Y that is running. It may start after the recording and restart during it. `auto` stands for the program that is full screen in front (see below). At most one |
| `app` (`--app Title=X`, repeatable) | `Title` | Same rules, e.g. `--app Discord=Discord.exe` |
| `microphone` (`--mic [name,...]`) | `Mic` | Default microphone, or the first present whose name contains one of the names; follows device changes and replugs |
| `system` (`--system`) | `System` | Everything except the game (everything while no game runs). At most one |

A process list is tried in order only while nothing is captured: a running game is kept even if
one earlier in the list starts later.

`auto` (the default Game track) picks the program whose window is in front and covers its whole
monitor: exclusive and borderless full screen count, a maximized window does not. It stays the game
while it runs, also after Alt+Tab, until another program goes full screen in front or it exits. The
desktop, browsers (full-screen video) and programs named by an App track are never picked; name a
game that runs in a window, or that one of those programs runs, in the list instead.

All tracks start and end on the same instant; a track whose source is quiet, absent or restarting
is filled with silence. Each source's clock is gently resampled to the recording clock, so long
recordings do not drift (the status line's `corr` column shows the correction in ppm). In VLC,
choose the track under Audio → Audio Track. Stop with `--seconds`, Enter, or Ctrl+C; recording
also stops when a captured window closes.

`replay` keeps the last `--buffer` seconds in memory, already encoded (about 150 MB for 60 s at
20 Mbps), and writes nothing until you press the hotkey. The hotkey works while a game has focus;
S in the console window does the same. Each press saves `<ts>_clip.mkv` with every track, ending
at the moment of the press and starting on a keyframe (at most 1 s before the buffer length, so
a 60 s buffer gives clips of 60–61 s). Presses in quick succession give overlapping clips.
`--record` also records the whole session, as `record` does. If another program already uses the
hotkey (NVIDIA's Instant Replay uses Alt+F10), `replay` says so and exits; pick another with
`--hotkey`, e.g. `--hotkey Ctrl+Shift+F8`.

Next to each video there is `<name>.json`, a sidecar with what the file holds (length, the game
exe that was captured, video settings, the audio tracks with their titles, stream numbers, kinds,
configured targets and the exe or device last captured), and `<name>.jpg`, a 320 px
thumbnail of the middle of the video. The sidecar is written last, so a video without one is an
interrupted save.

## ExportCli commands

```
SplitTrack.ExportCli info <file>
SplitTrack.ExportCli export <file> [--tracks <title|n>[=<volume %>][,...] | --no-audio] [--audio-bitrate <kbps, 160>]
                                   [--from <time>] [--to <time>] [--precise]
                                   [--size <MB>] [--height <px>] [--bitrate <Mbps>] [--reencode]
                                   [--encoder auto|amf|nvenc|qsv|x264] [--out <file|dir>] [--overwrite]
SplitTrack.ExportCli encoders
```

`<file>` is a recording's `.mkv` or its `.json` sidecar. `info` lists the audio tracks with their
number, title, kind and what they captured. `export` writes `<name>.mp4` next to the source (or
`--out`): H.264 video and **one** AAC track (48 kHz stereo) that mixes the tracks named in
`--tracks` (all of them by default), each at its volume, e.g. `--tracks Game,Discord=80,Mic=150`.
The mix is a plain sum, so a track at 100 % sounds as loud as in the recording; a limiter stops
the sum from clipping. The moov box is at the front, so the file starts playing while it downloads
(Discord, browsers, phones). An audio-only recording becomes `<name>.m4a`.

The video is **copied** as recorded unless something needs a re-encode, so most exports take a
second or two and lose nothing:

| Option | Effect |
|---|---|
| `--from` / `--to` | The part to export: seconds (`75.5`) or `m:ss` / `h:mm:ss` (`1:15.5`). A copy starts on the keyframe at or before `--from`, at most 1 s earlier |
| `--precise` | Start exactly at `--from`; re-encodes the video when `--from` is not on a keyframe |
| `--size <MB>` | Keep the file at or under this many MB (1,000,000 bytes), e.g. `--size 10` for Discord without Nitro. Copies if that fits, otherwise re-encodes at the bitrate that fits (and tries again lower if it came out too big) |
| `--height <px>` | Scale down, e.g. `--height 720`; re-encodes |
| `--bitrate <Mbps>` | Video bitrate of a re-encode (default: the recording's, per pixel) |
| `--reencode` | Re-encode even if nothing else asks for it |
| `--encoder` | Encoder for a re-encode. `auto` takes the first GPU encoder that works (AMF, NVENC, QSV), else x264; with `--size` it takes x264, whose rate control lands on target (GPU encoders stop at a quality floor at low bitrates) |

Audio stays in sync to the sample however the file is cut. `System` records everything except the
game, so it also contains Discord: when both are chosen and Discord was captured, `export` says
so. Ctrl+C cancels and removes the unfinished file. `encoders` shows which encoders work for
re-encoding on this PC.

## AudioPoc commands

```
SplitTrack.AudioPoc list
SplitTrack.AudioPoc record [--process <pid|exe>]... [--exclude <pid|exe>] [--system-all]
                           [--mic [name]] [--seconds <n>] [--out <dir>] [--format float48|pcm44]
```

| Option | Track file | What it records |
|---|---|---|
| `--process X` (repeatable) | `<ts>_proc_<exe>.wav` | Only that process tree, whichever output device it uses |
| `--exclude X` | `<ts>_system.wav` | Everything **except** that process tree |
| `--system-all` | `<ts>_sysall.wav` | Everything on the default output device |
| `--mic [name]` | `<ts>_mic.wav` | The default mic, or the first one whose name contains `name` |

An exe name resolves to its **root** process, so `--process Discord.exe` also covers Discord's
child audio process. All files start at the same instant and have exactly the same length; gaps
where a source is quiet are filled with silence. Stop with `--seconds`, Enter, or Ctrl+C.

Windows can exclude only one process tree per stream, so the "system" track still contains Discord
when you exclude the game. Uncheck one of the two at export.

## VideoPoc commands

```
SplitTrack.VideoPoc list
SplitTrack.VideoPoc record [--monitor <n> | --window <hwnd|exe|title text>] [--fps 60] [--bitrate <Mbps, 20>]
                           [--height <px>] [--encoder auto|amf|nvenc|qsv|x264] [--no-cursor]
                           [--adapter <n>] [--seconds <n>] [--out <dir>]
```

`list` shows GPUs, monitors, capturable windows, and which H.264 encoders actually work on this PC.
`record` writes `<ts>_video.mkv`: H.264 (BT.709, limited range), constant frame rate, a keyframe
every second, average bitrate `--bitrate` with peaks up to 1.5×. Capture is Windows Graphics Capture;
colour conversion and scaling run on the GPU. `auto` uses the GPU vendor's encoder (AMD → AMF,
NVIDIA → NVENC, Intel → QSV) and falls back to x264 on the CPU. A window that is resized keeps the
first size (cropped or padded with black); a window that closes ends the recording.

Logs: `%LOCALAPPDATA%\SplitTrack\logs`.

## Testing on a gaming PC

### The app (milestone 7)

1. Copy `out\installer\SplitTrack-Setup-<version>.exe` over, run it, and start SplitTrack from the
   last page of the setup or the Start menu.
2. Settings: the Game track detects the full-screen game (`auto`); add games that run in a window
   (Add… lists what is running), add an App track for
   `Discord.exe`, pick your headset under Mic if it is not the default, Save and apply.
3. In a Discord call, play a match. The meters at the top move for Game, Discord, Mic and System
   (each one only for its own sound). Press Alt+F10 after a good moment, and Alt+F9 twice to
   record a minute or so.
4. Open the clip from the Library: picture and sound in sync (gunshots, voices); untick Discord
   and System, and only the game and you remain.
5. Export with Size limit 10 MB (Discord), Copy file, paste into Discord: it uploads and plays
   inline, also on a phone.
6. Unplug or switch the headset mid-match: no crash, the Mic meter follows. Quit and restart the
   game while the app runs: the Game meter comes back.
7. A 30–60 minute recording: the file plays to the end in sync; Task Manager shows SplitTrack's
   memory flat over time.

### Recorder (milestone 3)

1. Copy `SplitTrack.RecordCli.exe` and its `ffmpeg` folder over.
2. In a Discord call, with the game running:
   `SplitTrack.RecordCli.exe record --game <Game>.exe --app Discord=Discord.exe --mic --system --seconds 60 --out C:\Temp\poc`
3. Open the MKV in VLC and switch between the tracks (Audio → Audio Track). Check:
   - Game has only the game, Discord only the call, Mic only you, System everything except the game;
   - gunshots and voices line up with the picture;
   - the summary shows every track at the same length, and `Corr` within a few hundred ppm.
4. Start a recording *before* the game, quit the game and start it again mid-recording: the Game
   track picks it up again within a quarter of a second each time.
5. Unplug or switch the mic (or headset) mid-recording: no crash, the Mic track continues.
6. A 30–60 minute session: no audible clicks, sound still in sync at the end.

### Audio

1. Copy `SplitTrack.AudioPoc.exe` over. Join a Discord voice call and start a game with sound.
2. `SplitTrack.AudioPoc.exe list`: Discord.exe and the game should appear, marked `active`.
3. `SplitTrack.AudioPoc.exe record --process Discord.exe --process <Game>.exe --exclude <Game>.exe --mic --seconds 30 --out C:\Temp\poc`
   Talk, let a friend talk, and make game noise. Each meter should move only when its own source is audible.
4. Open the WAVs in VLC or Media Player and check:
   - every file is 30.000 s long;
   - `proc_Discord` has only the call, `proc_<Game>` only the game, `mic` only you, `system` everything but the game;
   - playback on your speakers or headphones was normal while recording;
   - the summary shows `MaxDrift` of a few ms at most.
5. Edge cases, none of which should crash: start recording before the game makes sound; mute
   Discord for 10 s; switch Discord's output device mid-call; close the game mid-recording.

If a player refuses the float WAVs, rerun with `--format pcm44`.

### Video

1. Copy `SplitTrack.VideoPoc.exe` and its `ffmpeg` folder over.
2. `SplitTrack.VideoPoc.exe list`: your GPU appears, and `amf` (AMD), `nvenc` (NVIDIA) or `qsv`
   (Intel) says `OK`; the last line names it as the auto choice.
3. Start a game, then `SplitTrack.VideoPoc.exe record --monitor 0 --seconds 30 --out C:\Temp\poc`.
   Repeat with `--window <Game>.exe` (borderless or windowed mode).
4. Check the summary and the file in VLC:
   - `skipped` is 0 or close to it, and encode time stays a few ms per frame;
   - motion is smooth at 60 fps with no stutter, and colours look like the game;
   - the game's own frame rate did not drop noticeably while recording.
5. Edge cases: close the game mid-recording (`--window`), alt-tab out and back, record with
   `--height 1080` on a 1440p or 4K monitor.

## Layout

```
src/SplitTrack.Common    QPC clock, precise timer, logging, app data paths, console status, Windows version gate, AudioFormat, peak meter
src/SplitTrack.Capture   Audio: WASAPI sources, device watcher, session scanner, aligner. Video: D3D11 device, Windows Graphics Capture
src/SplitTrack.FFmpeg    FFmpeg loading and logging, packet and stream helpers
src/SplitTrack.Encode    GPU BGRA→NV12 conversion, H.264 encoder (AMF / NVENC / QSV / x264), resampler, AAC encoder
src/SplitTrack.Mux       WAV writer, MKV writer
src/SplitTrack.Buffer    Replay buffer: the last seconds of encoded packets, cut into keyframe-aligned clips
src/SplitTrack.Library   Library folder naming, JSON sidecars, thumbnails
src/SplitTrack.Engine    Capture session: video loop, audio tracks, source watcher (process restarts, device changes),
                         packet routing to file and replay buffer, clip saving, global hotkey, settings.json
src/SplitTrack.Export    Export to MP4: track mix, keyframe or exact cut, size limit; runs ffmpeg.exe
src/SplitTrack.Preview   Preview player: decode to BGRA frames and per-track audio rings, live mix to a WASAPI output
src/SplitTrack.App       The WPF tray app: recorder service and hotkeys, library, player and export, settings
tools/SplitTrack.AudioPoc
tools/SplitTrack.VideoPoc
tools/SplitTrack.RecordCli
tools/SplitTrack.ExportCli
tools/FFmpeg.targets     Copies the FFmpeg DLLs (and ffmpeg.exe for export) next to the video tools
tools/get-ffmpeg.ps1
tools/build-installer.ps1  Publishes the app and builds its installer
installer/SplitTrack.iss   Inno Setup script of the installer
```

Win32/COM bindings are generated by CsWin32 from each project's `NativeMethods.txt`. The few WinRT
interop calls CsWin32 cannot generate are in `src/SplitTrack.Capture/Video/Direct3DInterop.cs`.
