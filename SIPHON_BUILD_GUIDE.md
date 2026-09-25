# Siphon — Build Guide

*Working title. Rename freely.*

A tiny Windows utility that records system audio (or a single app's audio) to an uncompressed WAV file. It has one window, one big button, and one job.

This guide is written for Claude Code. Build it in phase order and verify each phase before moving on.

---

## 1. Scope

### In scope (v1)
- Record **all system audio** from the default output device via WASAPI loopback.
- Record **a single app's audio** via Windows process loopback.
- Save as **WAV** in 32-bit float (native), 24-bit PCM, or 16-bit PCM (dithered).
- Give the file a **correct duration even when the source goes silent** (gap filling; see §5).
- **Crash-safe files**: if the app dies mid-recording, everything up to the last second is still playable.
- **Tiny window** with a big record button, timer, stereo level meter, and source picker.
- **Drag the finished file** straight out of the window into a DAW, Explorer, or sampler.

### Out of scope (v1)
- Compressed formats (MP3/FLAC/etc.).
- Recordings over ~3 hours (a size guard handles this; see §6).
- Microphone input or mixing multiple sources.
- Resampling. The file uses whatever sample rate the device/format provides.
- Tray icon or global hotkeys. These are parked in §11.

---

## 2. Stack

| Piece | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, C# | Current LTS. .NET 8 support ends Nov 2026. |
| UI | **WPF** | Mature, easy custom styling, drag-out support is trivial. |
| Audio | **NAudio** (latest stable 2.x) | `WasapiLoopbackCapture`, `WaveFileWriter`, and device/session enumeration. |
| Process loopback | **Custom COM interop** | NAudio has no process-loopback support, so we write a small wrapper (§4). |
| MVVM | `CommunityToolkit.Mvvm` | Cuts boilerplate. Optional but nice. |

No other dependencies. Keep it lean.

**Publishing:**
- Default: framework-dependent single file (`-p:PublishSingleFile=true --self-contained false`). This produces a few MB and requires the .NET 10 Desktop Runtime.
- Optional: self-contained single file with compression enabled, for a no-install build. It is larger (tens of MB), but still nothing like Electron.

---

## 3. Architecture

```
┌─────────────┐     ┌──────────────────┐     ┌────────────────┐
│ ICaptureSource│──▶│  CaptureSession  │──▶ │   WavSink      │
│  (system or │ buf │  - gap filler    │ q  │  - format conv │
│   process)  │     │  - level meter   │    │  - dither      │
└─────────────┘     │  - clock         │    │  - periodic    │
                    └──────────────────┘    │    header flush│
                             │              └────────────────┘
                             ▼
                      MainViewModel ──▶ MainWindow (WPF)
```

**Key rule:** the audio callback thread does *almost nothing*. It copies the buffer into a `Channel<byte[]>` (or a pooled buffer queue) and returns. A dedicated writer task drains the channel, fills gaps, converts the format, and writes to disk. Disk I/O must never block the capture callback, because a blocked callback drops audio.

### Core types
- `ICaptureSource`: `Start()`, `Stop()`, `WaveFormat Format`, and the events `DataAvailable(ReadOnlyMemory<byte>, flags)` and `Stopped(Exception?)`.
  - `SystemLoopbackSource` wraps NAudio's `WasapiLoopbackCapture`.
  - `ProcessLoopbackSource` wraps our interop (§4).
- `CaptureSession` owns one source and one sink, runs the writer task, computes peak levels, and tracks elapsed time.
- `WavSink` wraps `WaveFileWriter`, handles bit-depth conversion and dither, and flushes the header periodically.
- `AudioSourceCatalog` lists "System audio" plus the apps currently playing sound.

---

## 4. Capture sources

### 4a. System loopback
- Use `new WasapiLoopbackCapture()` on the default render device (`MMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)`).
- The format is the device mix format. That is almost always **IEEE float, 32-bit, 48 kHz, stereo**, but don't assume it. Read `capture.WaveFormat` and handle 44.1k, 96k, etc.
- Handle multichannel devices (5.1/7.1). v1 can simply record all channels as-is, since WAV supports that. Only the level meter needs to decide what to show (show the first two channels).

### 4b. Process loopback (per-app)
Requires Windows 10 2004+ / Windows 11. Check at startup, and if unsupported, hide per-app entries and show only "System audio".

**How it works:**
1. Build `AUDIOCLIENT_ACTIVATION_PARAMS` with:
   - `ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`
   - `ProcessLoopbackParams.TargetProcessId = <pid>`
   - `ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`. **Include the tree.** Browsers and many apps play audio from child processes.
2. Wrap it in a `PROPVARIANT` (`VT_BLOB`) and call `ActivateAudioInterfaceAsync` with the device ID `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` and `IID_IAudioClient`.
3. Implement `IActivateAudioInterfaceCompletionHandler` and wait for completion. The callback arrives on an MTA thread, so marshal carefully.
4. `IAudioClient.Initialize` with `AUDCLNT_SHAREMODE_SHARED`, `AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK`, and **an explicit format**. Process loopback does *not* support `GetMixFormat`, so you must choose one:
   - Request **48 kHz / 32-bit float / stereo** first.
   - If `Initialize` fails, fall back to 48 kHz / 16-bit PCM / stereo.
5. Get `IAudioCaptureClient`, run an event-driven capture loop on a dedicated thread (with MMCSS "Pro Audio" priority via `AvSetMmThreadCharacteristics`), and drain packets with `GetBuffer` / `ReleaseBuffer`.
6. Honor `AUDCLNT_BUFFERFLAGS_SILENT`: when the flag is set, write zeros for that packet instead of the buffer contents.

**Reference:** Microsoft's *ApplicationLoopback* sample (Windows-classic-samples on GitHub). It is C++, but it maps 1:1 onto the interop we need. Port the COM definitions from it.

### 4c. Listing apps
- Use NAudio's `AudioSessionManager` on the default render device to enumerate sessions. For each session, get its process ID, then its `Process.ProcessName` and main module icon.
- **Group by executable name.** Chrome, for example, may show several sessions or a separate audio-service process. Pick the PID of the session's own process, since that is the one actually rendering. With include-tree mode, that is enough.
- Show only sessions that are active or were recently active. Refresh the list each time the dropdown opens.
- Skip the System Sounds session (PID 0).

---

## 5. Gap filling (the most important quality feature)

**The problem:** WASAPI loopback sends **no packets at all** while nothing is playing. If you write only what arrives, a 60-second recording with a 20-second pause becomes a 40-second file. Timing drifts, and the recording no longer matches reality.

**The fix: clock-based padding.**
- When recording starts, start a `Stopwatch` and set `framesWritten = 0`.
- Each time the writer processes a buffer (or at least every 50 ms via a timer, even when no data arrives):
  - `expectedFrames = elapsedSeconds × sampleRate`
  - If `expectedFrames − framesWritten > threshold` (about 20 ms worth of frames), write that many frames of **digital silence** *before* writing the incoming buffer.
- Then write the real buffer and add its frames to `framesWritten`.
- **Never pad during active streaming** just because of small jitter. The threshold handles that. Only fill genuine gaps.
- On **Stop**, pad up to the stop time, so a recording that ends in silence still has the right length.

**Why a timer tick and not only on data arrival:** if the source stays silent for a long time, the timer keeps the file (and the header flush) moving, so the file on disk always reflects the true elapsed time.

**Test:** play 5 s of audio, pause 10 s, play 5 s, stop. The file must be **20 s ± 20 ms**.

---

## 6. Writing WAV files

- Use NAudio's `WaveFileWriter` with a target `WaveFormat` chosen from settings:

| Setting | Format | Notes |
|---|---|---|
| **32-bit float** (default) | IEEE float | Bit-for-bit what Windows mixed. No conversion. |
| 24-bit PCM | PCM | Clamp to [−1, 1], scale, and apply TPDF dither. |
| 16-bit PCM | PCM | Clamp to [−1, 1], scale, and apply TPDF dither. |

- **TPDF dither:** add `(rand() − rand()) × 1 LSB` before rounding. Use a fast, cheap PRNG such as xorshift, with separate state per channel.
- If the source is already 16-bit PCM (process-loopback fallback), write it directly when 16-bit is chosen, and upconvert losslessly for the other options.
- **Crash safety:** call `writer.Flush()` about once per second. In NAudio this updates the RIFF/data size fields in the header, so a killed process still leaves a valid file.
- **Size guard:** at 3.8 GB, stop automatically, finalize the file, and show "Recording stopped at the file size limit." This will almost never trigger, but a silent failure there would be awful.
- **Filenames:** `Siphon_2026-09-25_14-32-07.wav` for system audio and `Siphon_Spotify_2026-09-25_14-32-07.wav` for a single app. Sanitize app names.
- **Output folder:** defaults to `%USERPROFILE%\Music\Siphon`. It can be changed in settings and is created if it doesn't exist.

---

## 7. Device changes and failure handling

- Register an `IMMNotificationClient` (NAudio: `MMDeviceEnumerator.RegisterEndpointNotificationCallback`).
- If the default output device changes, or the device is removed, while recording **system audio**: stop cleanly, finalize the file, and show "Output device changed. Recording saved." Don't try to stitch into the new device in v1.
- If the target **app exits** during per-app capture: the capture keeps delivering silence or nothing, and gap filling keeps time correct. Show a subtle "App closed" note, but keep recording until the user stops.
- If the capture thread throws: finalize whatever was written, and show the error plainly, including what happened and what to do.
- **Never lose a file silently.** Every stop path goes through the same `FinalizeAsync()`.

---

## 8. UI

### Window
- About **300 × 380 px**, not resizable, with a custom chrome (drag area at the top, minimize/close only).
- A pin toggle for **always on top**.
- Remember the window position between launches.

### Layout
```
┌─────────────────────────────────┐
│ ⠿ Siphon                 📌 ─ ✕ │  drag bar
│                                 │
│  [ 🔊 System audio          ▾ ] │  source picker
│                                 │
│              ╭───╮              │
│             │  ●  │             │  record button (~120 px)
│              ╰───╯              │
│                                 │
│            00:03:42             │  timer
│   L ▮▮▮▮▮▮▮▮▮▮▮▮▮▯▯▯▯▯▯▯        │  level meter
│   R ▮▮▮▮▮▮▮▮▮▮▮▯▯▯▯▯▯▯▯▯        │
│                                 │
│ ─────────────────────────────── │
│ ≡ Siphon_2026-09-25_…wav   📂 ⚙ │  last file (drag me) · folder · settings
└─────────────────────────────────┘
```
Center-aligned column. The record button is the star, and everything else stays quiet.

### Visual direction: "studio hardware"
Think of a well-worn tape deck in a dim room: a graphite faceplate, warm amber meter glow, and one red lamp that means *recording*. This fits the warm, dark-UI taste without falling into the generic near-black-plus-neon look.

| Token | Hex | Use |
|---|---|---|
| Faceplate | `#2B2C30` | Window background |
| Panel | `#36383D` | Picker, footer strip, and inset areas |
| Label | `#E9E2D2` | Primary text (warm off-white) |
| Muted | `#8D8980` | Secondary text and idle meter segments |
| Amber | `#F0A43C` | Meter segments and focus rings |
| Lamp red | `#E2473B` | Record button while recording |

**Type:** **Barlow Semi Condensed**, embedded as a resource. It reads like equipment labeling. Use Medium for UI and Semibold with **tabular figures** for the timer (about 32 px) so digits don't jiggle. Sentence case everywhere.

**The one memorable thing, the record button:**
- **Idle:** a matte dark circle with a muted red dot. It looks like an unlit lamp.
- **Recording:** the dot fills lamp-red, with a soft radial glow behind it that **breathes** slowly (opacity 0.6 → 1.0 over 2 s). This is the only ambient animation in the app. Respect reduced-motion settings (`SystemParameters.ClientAreaAnimation`) by making the glow steady instead.
- **Press:** a quick scale to 0.96 and back, like a physical click.
- The button is at least 120 px, keyboard-focusable, and toggles with **Space** or **Enter**.

**Meter:** segmented bars (not smooth gradients), updated about 30 times per second from peak values with a fast attack and a ~300 ms release. The top segments turn lamp-red at or above −1 dBFS. The meter works even when idle, as a preview, so you can see that the source is live before hitting record.

### Copy
| Moment | Text |
|---|---|
| Idle | "Ready" (under the timer, muted) |
| Recording | Timer running; the lamp says the rest |
| Saved | "Saved Siphon_…wav" in the footer, draggable |
| No per-app support | Picker shows "System audio" only, with a tooltip: "Per-app recording needs Windows 10 (2004) or later" |
| Device changed | "Output device changed. Recording saved." |
| Source picker, app idle | App name shown dimmed with "not playing" |

### Drag-out
The footer's last-file row starts `DragDrop.DoDragDrop` with a `DataObject(DataFormats.FileDrop, new[] { path })`, so the file can be dropped straight into a DAW, sampler, or Explorer. A double-click opens the file in the default player, and the 📂 button opens Explorer with the file selected (`explorer /select,"path"`).

### Settings (small popover, not a separate window)
- Output folder
- Bit depth: 32-bit float / 24-bit / 16-bit
- Always on top (mirrors the pin)

Store them as JSON in `%APPDATA%\Siphon\settings.json`.

---

## 9. Build phases

**Phase 1 — Capture core (no UI)**
- A console harness records system audio for N seconds to 32-bit float WAV.
- Channel-based writer, gap filler, and periodic flush.
- ✅ Verify: gap test (§5), and killing the process mid-record leaves a playable file.

**Phase 2 — UI shell**
- Window, record button states, timer, live meter, drag bar, and pin.
- Wire it to system loopback only.
- ✅ Verify: the meter moves while idle, the timer matches the file length, and the button is keyboard operable.

**Phase 3 — Formats**
- 24/16-bit conversion with TPDF dither, settings popover, and output folder.
- ✅ Verify: files open correctly in Audacity at each bit depth, and there is no clipping on a full-scale source.

**Phase 4 — Per-app capture**
- Process loopback interop, the app list in the picker, OS version check, and fallback format.
- ✅ Verify: record Spotify (or a browser) while a YouTube video plays in another app; only the target is audible. The browser test must capture tab audio (tests include-tree mode).

**Phase 5 — Resilience and polish**
- Device-change handling, app-exit handling, size guard, drag-out, window position memory, embedded font, and app icon.
- ✅ Verify: unplug headphones mid-record, and the file is saved and the message is shown.

---

## 10. Test checklist
- [ ] 20 s gap test comes out at 20 s ± 20 ms (system *and* per-app).
- [ ] Recording ending in silence still has the right length.
- [ ] Task Manager kill mid-record → the file plays up to about the last second.
- [ ] 44.1 kHz and 96 kHz output devices record at their native rate.
- [ ] A 5.1 device records without crashing, and the meter shows L/R.
- [ ] 16-bit export of a quiet fade has no audible truncation distortion (dither works).
- [ ] Device switch mid-record → file saved, message shown, no crash.
- [ ] Target app closes mid-record → recording continues with silence.
- [ ] Drag-out into Explorer and into a DAW both work.
- [ ] CPU while recording stays near zero (well under 1–2%).

---

## 11. Parking lot (maybe later)
- Global hotkey to start and stop recording.
- Tray mode.
- "Arm" mode: start writing automatically when audio appears, and optionally stop after N seconds of silence. This would be great for sampling.
- Seamless continuation across device changes.
- FLAC export (still lossless, much smaller).
- Trim the silent head and tail of the last recording with one click.
