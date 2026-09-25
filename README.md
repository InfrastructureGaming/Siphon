# Siphon

A tiny Windows utility that records what your computer is playing to an uncompressed WAV file. One window, one big button, one job.

- **Bit-for-bit capture.** Siphon writes exactly what Windows mixes, 32-bit float at the device's native sample rate, with no conversion, resampling, or dither. Do your downsampling elsewhere.
- **Honest timing.** Silence is still time. When nothing is playing, Windows sends no audio at all, so Siphon fills those gaps with digital silence. A 60-second recording with a 20-second pause is a 60-second file.
- **Crash-safe.** The WAV header is rewritten every second, so if the app is killed mid-recording, everything up to about the last second still plays.
- **Drag it out.** The last recording sits in the footer. Drag it straight into a DAW, a sampler, or Explorer.

## Using it

| | |
|---|---|
| Record / stop | Click the button, or press **Space** or **Enter** |
| Level meter | Live even when idle, so you can see the source is playing before you record |
| Last file | Drag it out, double-click to play it, or 📂 to show it in Explorer |
| Settings (⚙) | Output folder and always on top |
| Pin (📌) | Keep the window above others |

Recordings are saved to `Music\Siphon` by default as `Siphon_2026-09-25_14-32-07.wav`. Settings live in `%APPDATA%\Siphon\settings.json`.

## Requirements

- Windows 10 or 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Building

```sh
dotnet build
dotnet run --project src/Siphon.App

# Release: a ~1 MB single-file exe in ./publish
dotnet publish src/Siphon.App -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
```

## Layout

| Project | What it is |
|---|---|
| `src/Siphon.Core` | Capture sources, the recording session (writer task, gap filling, header flush, size guard), the WAV sink, and the level meter |
| `src/Siphon.App` | The WPF app |
| `src/Siphon.Harness` | A console tool for checking the core without the UI (see below) |

The harness automates the quality checks:

```sh
dotnet run --project src/Siphon.Harness -- gaptest    # 5 s tone, 10 s silence, 5 s tone; checks the file is ±20 ms
dotnet run --project src/Siphon.Harness -- killtest   # kills a recording mid-way; checks the file survives
dotnet run --project src/Siphon.Harness -- inspect <file.wav>
```

`gaptest` plays a quiet tone through your speakers, so pause other audio first.

## Not yet

Per-app recording, handling of a changed default output device, and an app icon are designed but not built. See phases 4 and 5 in [`SIPHON_BUILD_GUIDE.md`](SIPHON_BUILD_GUIDE.md). Until then, if the output device disappears mid-recording, the recording is designed to stop and save what it has, but that path hasn't been tested yet.

## Credits

- [NAudio](https://github.com/naudio/NAudio) (MIT) for WASAPI capture and WAV writing
- [Barlow](https://github.com/jpt/barlow) by Jeremy Tribby (SIL Open Font License 1.1, see `src/Siphon.App/Assets/Fonts/OFL.txt`)
