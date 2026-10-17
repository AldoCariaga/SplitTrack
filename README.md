# SplitTrack

A Windows game recorder that keeps **one audio track per source** (game, Discord, mic, system), so
you can choose later which ones end up in the exported clip. Per-app audio comes from WASAPI
process loopback, so no virtual audio cables are needed.

Status: **Milestone 3, the recorder** (`tools/SplitTrack.RecordCli`): one MKV with video and one
named AAC track per audio source. The milestone 1 and 2 proofs of concept (`tools/SplitTrack.AudioPoc`,
`tools/SplitTrack.VideoPoc`) are still there.

## Requirements

- Windows 11, or Windows build 20348 or newer (process loopback needs it), on x64.
- Building: .NET 10 SDK. Running the published exe needs nothing installed.
- No admin rights.

## Build, run, publish

The video tools need FFmpeg's DLLs, which are not in git. Download them once (BtbN's 9.0 shared
build, checked against its SHA-256; FFmpeg.AutoGen 9.0 is bound to exactly that major version):

```
powershell -File tools\get-ffmpeg.ps1
dotnet build -c Release
```

```
dotnet run --project tools\SplitTrack.AudioPoc -c Release -- list
dotnet run --project tools\SplitTrack.AudioPoc -c Release -- record --process msedge.exe --mic --seconds 15 --out C:\Temp\poc
dotnet publish tools\SplitTrack.AudioPoc -c Release -o out\audio-poc

dotnet run --project tools\SplitTrack.VideoPoc -c Release -- list
dotnet run --project tools\SplitTrack.VideoPoc -c Release -- record --monitor 0 --seconds 15 --out C:\Temp\poc
dotnet publish tools\SplitTrack.VideoPoc -c Release -o out\video-poc

dotnet run --project tools\SplitTrack.RecordCli -c Release -- list
dotnet run --project tools\SplitTrack.RecordCli -c Release -- record --game msedge.exe --mic --system --seconds 15 --out C:\Temp\poc
dotnet publish tools\SplitTrack.RecordCli -c Release -o out\record-cli
```

`out\audio-poc\SplitTrack.AudioPoc.exe` (about 45 MB, self-contained) is the only file to copy to
another PC. For video, copy `out\video-poc\SplitTrack.VideoPoc.exe` **and** the `ffmpeg` folder
next to it (about 185 MB together); the same goes for `out\record-cli\SplitTrack.RecordCli.exe`.

## RecordCli commands

```
SplitTrack.RecordCli list
SplitTrack.RecordCli record [--monitor <n> | --window <hwnd|exe|title text> | --no-video]
                            [--fps 60] [--bitrate <Mbps, 20>] [--height <px>] [--encoder auto|amf|nvenc|qsv|x264]
                            [--no-cursor] [--adapter <n>]
                            [--game <exe|pid>] [--app <Title>=<exe|pid>]... [--mic [name]] [--system]
                            [--audio-bitrate <kbps, 160>] [--seconds <n>] [--out <dir>]
```

`record` writes `<ts>.mkv`: the video as in VideoPoc, then one AAC track (48 kHz stereo) per audio
option, titled and ordered like this:

| Option | Track title | What it records |
|---|---|---|
| `--game X` | `Game` | Process tree X. It may start after the recording and restart during it |
| `--app Title=X` (repeatable) | `Title` | Process tree X, same rules, e.g. `--app Discord=Discord.exe` |
| `--mic [name]` | `Mic` | Default microphone (or first name match); follows device changes and replugs |
| `--system` | `System` | Everything except the game (everything while no game runs) |

All tracks start and end on the same instant; a track whose source is quiet, absent or restarting
is filled with silence. Each source's clock is gently resampled to the recording clock, so long
recordings do not drift (the status line's `corr` column shows the correction in ppm). In VLC,
choose the track under Audio → Audio Track. Stop with `--seconds`, Enter, or Ctrl+C; recording
also stops when a captured window closes.

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
every 2 s, average bitrate `--bitrate` with peaks up to 1.5×. Capture is Windows Graphics Capture;
colour conversion and scaling run on the GPU. `auto` uses the GPU vendor's encoder (AMD → AMF,
NVIDIA → NVENC, Intel → QSV) and falls back to x264 on the CPU. A window that is resized keeps the
first size (cropped or padded with black); a window that closes ends the recording.

Logs: `%LOCALAPPDATA%\SplitTrack\logs`.

## Testing on a gaming PC

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
src/SplitTrack.Common    QPC clock, precise timer, logging, console status, Windows version gate, AudioFormat, peak meter
src/SplitTrack.Capture   Audio: WASAPI sources, device watcher, session scanner, aligner. Video: D3D11 device, Windows Graphics Capture
src/SplitTrack.FFmpeg    FFmpeg loading and logging, packet and stream helpers
src/SplitTrack.Encode    GPU BGRA→NV12 conversion, H.264 encoder (AMF / NVENC / QSV / x264), resampler, AAC encoder
src/SplitTrack.Mux       WAV writer, MKV writer
src/SplitTrack.Engine    Recording session: video loop, audio tracks, source watcher (process restarts, device changes)
tools/SplitTrack.AudioPoc
tools/SplitTrack.VideoPoc
tools/SplitTrack.RecordCli
tools/FFmpeg.targets     Copies the FFmpeg DLLs next to the video tools
tools/get-ffmpeg.ps1
```

Win32/COM bindings are generated by CsWin32 from each project's `NativeMethods.txt`. The few WinRT
interop calls CsWin32 cannot generate are in `src/SplitTrack.Capture/Video/Direct3DInterop.cs`.
