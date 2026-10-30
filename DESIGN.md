# Talktrace — Design

The working design for Talktrace. It replaces the original build brief, which is not kept.
Last updated 2026-10-30.

## 1. Goal

Talktrace is a small native Windows desktop app that turns a meeting recording into a plain-text transcript with speaker labels (Speaker 1, Speaker 2, and so on). Pick an existing recording or record a meeting in the app, click Transcribe, and get a .txt file.

- **User:** an independent IT consultant, a few times a month, on recordings of client meetings: typically 30–90 minutes, English, 2–6 speakers.
- **Machine:** Windows 11 Home on ARM64 (Snapdragon X Elite X1E78100, 12 cores, 32 GB RAM). The existing local transcription apps have no working native ARM64 build, which is why this app exists.
- **Why local:** the recordings are confidential client conversations. Audio and transcripts never leave the machine.
- **Quality bar:** a dependable personal tool, not a product. Plain, clean, reliable. No accounts, no cloud, no extras.

## 2. Hard constraints

Non-negotiable. If one can't be met, stop and say so.

1. **Native ARM64 only.** Every executable and native DLL ships as ARM64: not x64, not ARM64EC. Nothing runs under emulation.
2. **Fully offline at runtime.** No network calls of any kind: no telemetry, update checks, crash reporting or downloads. The only network access is `scripts/get-models.ps1`, run deliberately.
3. **No Python, PyTorch or ffmpeg.** Self-contained .NET publish. Running on a clean Windows install is a goal, not a hard requirement: if a Visual C++ runtime dependency turns up, ship those DLLs next to the app.
4. **CPU inference only** for v1. No NPU, GPU, DirectML or QNN.
5. **Open-source components** whose licences allow personal use, each recorded in `THIRD-PARTY-NOTICES.md`.
6. **No admin rights** to install or run the app. Per-user, runs from any folder. (Developer tools such as the .NET SDK are exempt.)
7. **Nothing private in logs or source control.** Logs may hold timings, durations, file names, paths and errors, never transcript text or audio. The GitHub repo is public: recordings, transcripts, test data and models are never committed (see the `.gitignore` rules in section 5).

Alongside these, one standing design rule: **nothing in v1 may compromise a possible Microsoft Store release.** Section 18 lists the specific rules. A Store release would add a separate x64 build next to the ARM64 one; hard constraint 1 applies to the ARM64 build.

## 3. Decisions

| Decision | Why |
| --- | --- |
| sherpa-onnx and Parakeet are settled. No evaluation of other engines, ASR models or embedding models. | Tested on a 50-minute meeting: transcription took about 2 minutes, speaker identification about 3× that, and accuracy was good. |
| The Multilingual profile uses Parakeet TDT 0.6B v3. Whisper is dropped. | Same engine and model type as the Fast profile, so one code path. Meetings are English; Talktrace supports whatever Parakeet supports. |
| Transcription jobs run in a worker process (`Talktrace.Cli --worker`), not in the app process. | sherpa-onnx ignores the diarization progress callback's return value, so speaker identification can't be cancelled in-process. A worker gives instant cancel, crash isolation and clean memory per job. |
| Speakers are assigned word by word, using Parakeet's token timestamps. | Cutting the audio at speaker changes clips words at the boundaries and leaves short clips with little context. Fallback in section 7 if timestamps aren't available. |
| While recording, Talktrace plays silence on the monitored playback device. | Windows delivers no system audio while nothing is playing. Playing silence keeps it flowing, which removes gap-filling and lets NAudio's standard capture classes do the job. |
| Clock drift is handled by a writer that runs on the microphone's clock and occasionally drops or repeats one system-audio sample. | The two device clocks drift about 0.3–0.5 s apart over 90 minutes. The writer has to pair samples from both devices anyway, so the correction is a few lines. |
| One job at a time. No transcribing while recording. | Meetings are never back to back with a transcription running. |
| The transcript header lists speakers by name. | Renamed speakers show up in the header, not just a count. |
| Package versions are pinned exactly: sherpa-onnx 2.0.1 (fallback 1.13.8), NAudio 3.3.0. | 2.0.x fixes a VAD crash on long audio and returns NULL instead of crashing when a model fails to load. |
| Models are looked for next to the exe, then in `%LOCALAPPDATA%\Talktrace\models`. | Upgrading means unzipping a new folder without moving about 1 GB of models, and it still works when the install folder is read-only (a future MSIX package). |
| Speaker embeddings use `nemo_en_titanet_small.onnx`. | Used in the 50-minute test with good results. |
| Three call setups are supported: a combined speakerphone in the home office, Bluetooth headphones with a built-in mic when travelling, and the laptop's own speakers and mic. Talktrace remembers the mode for each device. | All three are in use. See section 9. |
| The repo stays public with no licence for now. | |
| The repository layout in section 5 is confirmed. | |
| No Visual C++ runtime needs shipping. | Milestone 1 found that sherpa-onnx and ONNX Runtime import no Visual C++ runtime DLLs, and .NET's own copy for WPF ships in the publish folder. |
| ARM64X files pass the ARM64 check. | .NET's WPF ships `vcruntime140_cor3.dll` as ARM64X. Its header says ARM64 and an ARM64 process runs its native ARM64 code, so nothing is emulated. |
| Tests use xUnit v3 on Microsoft.Testing.Platform. | The .NET 10 SDK no longer runs xUnit v3 through the older VSTest runner; this also drops two test packages. |
| The recognizer uses 6 threads by default and decodes one chunk at a time. | Milestone 2 benchmark, first 10 minutes of the test recording: 4, 6, 8 and 11 threads gave real-time factors of 0.030, 0.029, 0.031 and 0.033. Batching 2, 4 or 8 chunks per call was slower (0.033 to 0.036). |
| Nothing in v1 may compromise a possible Microsoft Store release; section 18 lists the rules, and every milestone is checked against them. | Audited after Milestones 2 and 3: no conflicts. Milestone 3 writes nothing new, reads its models through the same locator, assumes no processor, and uses only models already listed with commercial-use licences. The only file writes are transcripts next to the recording. Paths come from the app folder or Windows known folders. The `win-arm64` sherpa-onnx package reference is harmless for an x64 build, because the main package already brings every platform's native files and an x64 build selects only the x64 ones. |
| With speaker labels off, paragraphs split at pauses only, never on length. | A 100-word cap ending paragraphs at the next sentence end was tried in Milestone 2 and cut paragraphs off mid-thought. Long paragraphs are the accepted trade-off. |
| Speaker segmentation uses pyannote's full-precision `model.onnx`, not `model.int8.onnx`. | On the 49-minute test recording (fixed count 5), the int8 model found no speaker for 34% of the recognised words, spread evenly through the recording; full precision missed 0.3%. Adding 10 dB of gain didn't help int8, so quantization is the cause. Full precision took 258 s against about 200 s. |
| Speaker identification uses the same thread count as the recognizer (default 6) for both segmentation and embeddings. | The user's earlier 50-minute test used 6 threads for each. One thread setting keeps Advanced simple. |
| A speaker count of 1 skips speaker identification and labels every word Speaker 1. | There is nobody to tell apart, and it saves several minutes. |
| In smoothing (section 7, step 6), a pause means a gap of 0.5 s or more between words. | The same length the VAD treats as a pause (`VadMinSilenceSeconds`). Tunable as `ShortRunPauseSeconds`. |
| Speakers are found in two stages: sherpa-onnx over-splits, then Talktrace merges the groups by voice (section 7, step 4). The user chose this over only raising sherpa-onnx's threshold. | sherpa-onnx clusters with complete linkage and has no minimum cluster size, so stray embeddings become near-empty "speakers". On the test recording, Auto found 61 speakers at its default threshold of 0.5 and still 10 at 1.1; a fixed count of 4 or 5 was spent on stray groups and merged two real people into one. The merge is how pyannote's own pipeline works, uses only the models already shipped, and takes about 2 s. |
| Merge settings: split at 1.0, merge voices closer than 0.6, and a speaker needs 15 s of speech unless a chosen count needs more speakers. | On the test recording, the four main voices were 0.71–0.91 apart, and fragments of one voice 0.17–0.53 from it. Auto gave the same four speakers at every merge threshold from 0.4 to 0.7, from both a 0.9 and a 1.1 split. A 5 s minimum let a 12 s group holding 3 words become a fifth speaker on Auto. Each group costs memory in sherpa-onnx (about 35 MB on 98 minutes): splitting at 0.9 pushed the 98-minute job to a 2.69 GB peak; at 1.0, speaker identification peaked at 1.83 GB (27 groups) and at 1.1 at 1.41 GB (15 groups). 1.0 keeps the finer split and still stays below the transcription stage's peak. |
| With speaker labels on, each turn is one paragraph however long. The user chose this, settling open item 1. | On the test recording (Auto, four speakers): 72 turns, median 80 words; 18 turns over 200 words held 61% of the words, and the longest was 539 words. Splitting at pauses over 1 s would give 145 paragraphs (longest 303 words); over 2 s, 84 paragraphs (longest still 539). |
| The CLI takes `--threshold` for Auto-detect's clustering threshold. | The app offers it under Advanced; the CLI mirrors the app's options, as it does for `--threads`. |
| A diarizer or embedding extractor whose model fails to load is detected and reported as a plain error. | sherpa-onnx 2.0.1 returns a null handle, and its C# wrappers don't check it, so the first call would crash the process. Talktrace reads the wrappers' private handle field to check. |

## 4. Technology

| Layer | Choice | Notes |
| --- | --- | --- |
| Language and runtime | C# on .NET 10 (LTS) | `net10.0-windows`, runtime identifier `win-arm64`, self-contained. SDK pinned in `global.json`. |
| UI | WPF, single window, light MVVM with CommunityToolkit.Mvvm | No third-party UI frameworks. Follow the Windows light/dark setting with WPF's built-in Fluent theme (`ThemeMode="System"`, marked experimental); if it misbehaves, light only. |
| Speech engine | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) via NuGet: `org.k2fsa.sherpa.onnx` with the native `org.k2fsa.sherpa.onnx.runtime.win-arm64` | Offline ASR (Parakeet), Silero VAD and offline speaker diarization, all on ONNX Runtime. Start from its `dotnet-examples` folder. |
| Audio decoding | NAudio `MediaFoundationReader` and `MediaFoundationResampler` (package NAudio.Wasapi 3.3.0) | Uses the Media Foundation codecs built into Windows: .wav, .mp3, .m4a, .mp4, .wma, and the audio track of video files. |
| Audio capture | NAudio `WasapiCapture` (microphone), `WasapiLoopbackCapture` (system audio), `WasapiOut` (silence keep-alive) | Shared mode only, so capture never blocks Teams or other apps. |
| Logging | A small hand-written file logger | No dependency. |
| Settings | System.Text.Json | |
| Tests | xUnit v3 | Runs on Microsoft.Testing.Platform, opted in through `global.json`. |

All package versions live in `Directory.Packages.props`, pinned exactly; no floating ranges.

## 5. Repository layout

```
Talktrace.slnx                  solution (.NET 10's default format)
global.json                     pins the .NET 10 SDK
Directory.Build.props           shared settings: target framework, win-arm64, nullable, version
Directory.Packages.props        every package version, pinned
.gitignore  .gitattributes  .editorconfig
src/
  Talktrace.Core/               transcription pipeline: decoding, VAD, diarization, ASR, speaker
                                assignment, turns, formatting, options, sidecar format, worker
                                protocol. No UI and no capture code.
  Talktrace.Recording/          microphone and system-audio capture, silence keep-alive, aligned
                                WAV writer, sidecar writing, crash recovery. References Core.
  Talktrace.App/                WPF app. References Core and Recording. Runs jobs through the worker.
  Talktrace.Cli/                console app: CLI, benchmarking, and the --worker mode the app launches.
tests/
  Talktrace.Tests/              unit tests (no models needed) and integration tests (skip unless
                                TALKTRACE_MODELS_DIR is set)
scripts/
  get-models.ps1  verify-arm64.ps1  package.ps1  install-shortcut.ps1
README.md  DESIGN.md  CLAUDE.md  THIRD-PARTY-NOTICES.md
```

`CLAUDE.md` holds the build, test and publish commands and a short copy of the hard constraints, so later sessions stay inside them.

Because the repo is public, `.gitignore` ignores at least: `bin/`, `obj/`, `artifacts/`, `models/`, `testdata/`, `.vs/`, `*.user`, every audio and video type (`*.wav *.mp3 *.m4a *.mp4 *.wma *.aac *.flac *.ogg *.opus *.webm`), `*.talktrace.json`, `*transcript*.txt`, `*.log`, and the original build brief.

## 6. Architecture

Two processes:

- **Talktrace.App** (WPF) owns the window, settings, recording (the Recording library runs in the app process), the speakers panel and saving.
- **Talktrace.Cli in worker mode** runs one transcription job and exits.

Core exposes one entry point that the CLI and the worker both call:
`Task<TranscriptResult> TranscribeAsync(string path, TranscribeOptions options, IProgress<TranscribeProgress> progress, CancellationToken ct)`.

**Worker protocol**

- The app starts `Talktrace.Cli.exe --worker` with no window, at below-normal priority, and sends the job (input path, options, output path) as JSON on stdin.
- The worker writes one JSON message per line to stdout: `progress` (stage, done, total), `result` (turns with start, end, speaker and text; speakers; duration; stage timings) or `error` (a plain-language message plus details).
- The worker writes the auto-save .txt itself before it sends `result`, so a finished job is never lost, even if the app has gone.
- **Cancel:** the app sends `cancel` on stdin and the worker stops at the next chunk. If it hasn't exited within about 2 s (for example, during speaker identification), the app kills it. Partial results are discarded.
- **Crash:** if the worker exits without a `result`, the app shows "Transcription stopped unexpectedly" and logs the exit code.

**One thing at a time.** One job at a time, and no transcribing while recording: Transcribe is disabled while a recording runs. Starting a recording while a job runs asks whether to cancel the job; the recording always wins.

## 7. Processing pipeline

Core runs these stages in order. Every threshold lives in one options class so it can be tuned later. Call-mode recordings take the two-track path in section 9.

1. **Decode.** Open the file with Media Foundation, downmix to mono, resample to 16 kHz, convert to float32 in −1 to 1. Holding the whole recording in memory is fine (about 230 MB an hour). Record the duration. An unsupported or corrupt file gets a clear error that names the file type.
2. **Find speech.** Silero VAD over the whole track gives the speech regions. Drop regions shorter than about 0.3 s; very short clips make the recognizer invent text. After about 20 s of continuous speech the VAD splits at the next short pause, which keeps regions near chunk size.
3. **Build chunks.** Pack consecutive speech regions into chunks of at most about 25 s, splitting at the gaps between them, and start a new chunk after any pause longer than about 2 s so chunks don't carry long silences. A single region longer than 25 s is split at its quietest 100 ms (energy minimum) in its second half. Pad each chunk by about 0.15 s on each side, clamped to the recording's bounds and to half the gap to the next chunk, so neighbouring chunks never overlap and no word is transcribed twice.
4. **Find who spoke when** (speaker labels on). A count of 1 skips this step. Two stages:
   - **Split.** Run sherpa-onnx `OfflineSpeakerDiarization` on the whole track: pyannote segmentation 3.0 (full precision), the speaker-embedding model, and fast clustering with a deliberately low threshold (`SplitThreshold`, cosine distance 1.0) so it over-splits into groups. Never use sherpa-onnx's fixed cluster count (section 3). Wire its progress callback to the progress bar; it reports only the embedding pass, so the bar sits at 0% while segmentation runs first.
   - **Merge by voice.** Take one voice print per group with the same embedding model, from up to 60 s of the group's speech, longest stretches first, leaving out stretches where another group also speaks. Groups with at least 15 s of speech merge while their voices are closer than the clustering threshold (cosine distance 0.6, adjustable under Advanced). With a chosen count, they then keep merging down to it, weighting each merge by speaking time (Ward's method) so small groups go first; if there are fewer than the count, the most distinct small groups become speakers too. Every remaining small group joins the speaker whose voice is closest. A group too short for a voice print is dropped, and its words take a neighbour's speaker in step 6.

   Output: a list of (start, end, speaker), sorted by start; different speakers' segments can overlap.
5. **Transcribe.** Create one `OfflineRecognizer` for the chosen model and reuse it for every chunk, each chunk as its own stream. Keep the text and the token timestamps, and group tokens into words with absolute start and end times. Progress = chunks done out of total. Thread count comes from a setting, default 6 (fastest in the Milestone 2 benchmark). Chunks are decoded one at a time; batching was slower.
6. **Assign speakers to words.** Each word takes the diarization speaker that overlaps it most. A word that falls in a gap between speaker segments takes the nearest segment within about 1 s, otherwise the previous word's speaker. A run of words shorter than about 0.5 s, inside another speaker's run with no pause (0.5 s or more) on either side, is given to that speaker. With speaker labels off, every word gets one speaker.
7. **Assemble turns.** Consecutive words from the same speaker form one turn. With speaker labels off, start a new paragraph at pauses longer than about 2 s, and only there. Renumber speakers in order of first appearance, so the first voice heard is Speaker 1. Trim whitespace and drop empty turns.
8. **Format and save** (section 11). Auto-save the moment the job finishes.

**Fallback if token timestamps aren't available** through the C# API (checked in Milestone 1): transcribe the diarization segments themselves instead. Sort them, merge same-speaker segments less than about 1 s apart, drop segments under 0.3 s, split any over 25 s at the quietest point, pad by 0.15 s, transcribe each, and go straight to step 7.

## 8. Models

All models come from the sherpa-onnx GitHub releases. They are never compiled into the binary, never committed, and never downloaded by the app. Check each file name and URL against the [sherpa-onnx docs](https://k2-fsa.github.io/sherpa/onnx/) before hard-coding it.

| Role | Model | sherpa-onnx release | Licence |
| --- | --- | --- | --- |
| Transcription, **Fast** profile (English, default) | `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` | `asr-models` | CC-BY-4.0 |
| Transcription, **Multilingual** profile (25 European languages, detected automatically) | `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | `asr-models` | CC-BY-4.0 |
| Voice activity detection | `silero_vad.onnx` | `asr-models` | MIT |
| Speaker segmentation | `sherpa-onnx-pyannote-segmentation-3-0` (`model.onnx`, full precision; the archive also holds an int8 version, not used) | `speaker-segmentation-models` | MIT |
| Speaker embeddings | `nemo_en_titanet_small.onnx` | `speaker-recongition-models` (the misspelling is real; copy it exactly) | Apache-2.0 (the NeMo Toolkit licence, per NVIDIA's model card) |

The 50-minute test used a fixed speaker count (`--clustering.num-clusters=3`) and 6 threads each for segmentation and embeddings. The meeting actually had five speakers (the user and four others, one of whom said very little), so 3 was a guess. Milestone 3 found that sherpa-onnx's own clustering can't count speakers reliably and added a merge by voice (section 7, step 4; results in section 12).

Configure Parakeet as a NeMo transducer, or let sherpa-onnx detect the type from the model's metadata. A generic "transducer" setting can fail on a vocabulary-size metadata lookup.

**Where models live.** The app looks in `models\` next to the exe, then in `%LOCALAPPDATA%\Talktrace\models\`. The `TALKTRACE_MODELS_DIR` environment variable overrides both, for development and tests.

**`scripts/get-models.ps1`** downloads and extracts the models (Windows' built-in `tar` handles .tar.bz2). It checks SHA-256 hashes recorded in the script, taken from GitHub's release-asset digests where shown and otherwise recorded on first download. It skips files already present, is safe to re-run, and takes `-Profile Fast|Multilingual|All` and `-Destination` (default `%LOCALAPPDATA%\Talktrace\models`).

**Startup check.** The app checks that the selected profile's models are present. If any are missing, it names them and shows the command to run, with an Open models folder button. Profiles whose models are missing are disabled in the Model dropdown.

## 9. Recording a meeting

Talktrace can record a meeting and transcribe the recording when it stops. Windows shares the microphone between apps, so recording never blocks Teams, Zoom or anything else using the mic. Transcription is after the fact, not live.

**Recording modes**

| Mode | Captures | Transcribed as |
| --- | --- | --- |
| In person | Microphone only | One track; speakers found by diarization as usual |
| Online call, headphones | Microphone and system audio, as two tracks | Microphone track = me, labelled with my name; diarization runs on the system-audio track only |
| Online call, speakers | Microphone and system audio, as two tracks | As headphones, plus removal of the other participants' voices that the microphone picks up from the speaker (echo) |

My name comes from a **Your name** setting, default "Me".

**Supported setups**

All three setups are in use and must work.

| Setup | What the microphone hears | Mode |
| --- | --- | --- |
| Bluetooth headphones with a built-in mic (travelling) | Only me | Online call, headphones |
| Combined speakerphone (home office) | Me, plus a little of the others. The speakerphone cancels most echo in its own hardware, before Windows sees the audio, so Talktrace gets the same cleaned signal Teams does. | Online call, speakers |
| The laptop's own speakers and mic | Me, plus the others at full strength. Teams' echo cancellation only cleans Teams' own copy of the microphone, not Talktrace's. How much gets through depends on the laptop's own mic processing; testing will show. | Online call, speakers |

What this means for the build:

- **Devices follow Windows.** Teams is set to "Same as system" and Talktrace defaults to the Windows default devices, so plugging in the headphones or the speakerphone moves both apps together. A default-device change mid-meeting is handled (see Capture).
- **The mode is remembered per device.** Talktrace remembers the mode last used with each playback device and preselects it, so the speakerphone comes up as Online call, speakers and the headphones as Online call, headphones. For a device it hasn't seen before, it guesses from the device type Windows reports: headset or headphones → headphones mode, anything else → speakers mode. The selected mode is always shown before Start and can be changed.
- **One device, both directions.** A headset or speakerphone is both the microphone and the playback device. The silence keep-alive plays to it; silence is inaudible. Because one device clock usually drives both directions, drift is tiny in these setups; the aligned writer matters most when the microphone and playback are different devices.
- **Bluetooth headphones.** They switch to hands-free mode as soon as any app opens their mic (Teams, or Talktrace's level meter), and both directions drop to phone quality. Windows may also present that as a different device mid-call, which the device-change handling covers. Recording still works, but accuracy falls, most of all on narrowband (8 kHz) links. Talktrace warns when the microphone runs below 16 kHz. Bluetooth also delays the microphone track by a few tenths of a second relative to system audio; that only nudges my turns slightly later and doesn't change their order.
- **Laptop speakers and mic.** The hardest case for echo removal, because the others reach the microphone at full strength. The echo check in section 15 covers it. If word matching isn't enough, evaluate Windows 11's own echo cancellation in Milestone 7. NAudio 3 exposes its control for capture streams (`AudioClient.TryGetAcousticEchoCancellationControl`), which may let Windows remove the speaker audio from Talktrace's microphone track where the device supports it.

**Capture**

- **Microphone:** NAudio `WasapiCapture`, shared mode only, never exclusive. Defaults to the Windows default communications microphone; selectable.
- **System audio:** NAudio `WasapiLoopbackCapture` on the default playback device; selectable. Teams' speaker is set to "Same as system", so that's the device Teams plays to. It captures everything that plays, including notification sounds; the recording screen says so.
- **Silence keep-alive:** while capturing, `WasapiOut` plays silence in shared mode on the same playback device. Windows delivers no loopback data while nothing plays; this keeps it flowing. Silence is inaudible.
- **Format on the way in:** each source is converted to 16 kHz mono as it arrives.
- **Aligned writer:** a writer thread runs on the microphone's clock. Every ~100 ms it takes the microphone samples that have arrived and the same number of system-audio samples. It keeps the system-audio buffer near a target level by dropping or repeating a single sample now and then. If the buffer runs dry, it writes zeros. This keeps the tracks within about 0.25 s of each other over 90 minutes.
- **Pause:** capture keeps running but nothing is written; on Resume the writer's buffers are reset.
- **Device changes:** if a device is unplugged or the default device changes mid-meeting, keep recording, switch to the new default device, reset the writer's buffers and show a warning. Never stop silently.
- **Sleep:** stop the PC from sleeping while recording (`SetThreadExecutionState`) and keep recording when the window is minimised. Closing a laptop lid can still sleep the PC, depending on power settings; the README says so.

**Recording screen** (the Record a meeting tab)

1. Mode selector with the three modes above.
2. Microphone and playback device dropdowns, each with a live level meter, so both can be checked before the meeting starts.
3. Start, Pause/Resume and Stop buttons, elapsed time, and a clear recording indicator in the window and on the taskbar icon.
4. Two standing notes under Start: "Let participants know you're recording" and "Muting in Teams doesn't pause Talktrace; use Pause."
5. Free disk space, with a warning below about 1 GB.
6. On Stop: save the file, then open it in the transcribe flow with options preset from the mode. A **Transcribe automatically when I stop** setting, on by default, starts the job straight away.
7. Closing the window while recording asks for confirmation, then stops and saves cleanly.

**Files**

- Saved to `Documents\Talktrace\Recordings\` by default (changeable), named by date and time, for example `Recording 2026-10-29 1405.wav`.
- One WAV per recording at 16 kHz, 16-bit: mono for In person (about 115 MB an hour), stereo for the call modes (about 230 MB an hour), left channel the microphone, right channel system audio.
- A sidecar, `<recording>.talktrace.json`, records the mode, which channel is which, the start time, the device names and a status (`recording` or `complete`). The pipeline reads it to choose the two-track path. A stereo file without a sidecar is treated as an ordinary recording.
- **Crash safety:** write to disk continuously and refresh the WAV header every few seconds, so a crash or power loss leaves a playable file. On the next launch, a sidecar still marked `recording` means an unfinished recording: offer to recover it (fix the header from the file size) and transcribe it.
- A **Keep recordings after transcribing** setting, on by default. When off, the WAV and sidecar go to the Recycle Bin once the transcript is saved.

**How recordings are transcribed**

- **In person:** the normal pipeline on the mono file.
- **Headphones:** the microphone track goes through VAD, chunking and transcription, and every word is labelled with my name. The system-audio track goes through the full pipeline, diarization included. Words from both tracks are merged in time order, then assembled into turns.
- **Speakers:** as headphones, then remove echo: a microphone phrase (words between pauses) is dropped when most of its words (about 60%, tunable) also appear in the system-audio transcript within about 1.5 s (tunable). My own speech while others are talking is kept, because its words don't match theirs. With a speakerphone there is little echo left to remove; with plain speakers there is a lot. The README recommends headphones or a speakerphone over plain speakers.
- For call recordings, the Speakers option counts the other participants only and also offers 1, which skips diarization.
- In the speakers panel, I appear already named; the others appear as Speaker 1, Speaker 2 and so on.

## 10. User interface

One resizable window, about 900 × 700 to start, with two tabs: **Transcribe a file** and **Record a meeting** (section 9). The file tab, top to bottom:

1. **File.** A drop zone ("Drop a recording here") and a Choose recording… button with a file dialog filtered to supported types. Once chosen, show the file's name, duration and size.
2. **Options**, in one row:
   - **Speakers:** Don't label speakers · Auto-detect · 2 · 3 · 4 · 5 · 6 · 7 · 8. Default Auto-detect. For call recordings: Don't label speakers · Auto-detect · 1–8 (other participants).
   - **Model:** Fast (English) · Multilingual. Default Fast.
   - **Include timestamps:** checkbox, off by default.
   - **Advanced** (collapsed): clustering threshold, thread count, segment and chunk limits.
3. **Transcribe** button, the primary action. It becomes **Cancel** while a job runs.
4. **Progress.** A stage label (Reading audio · Identifying speakers · Transcribing 42 of 310 · Saving), a progress bar, elapsed time, and estimated time left once transcription starts.
5. **Transcript.** A scrollable, editable text box showing the result.
6. **Speakers panel**, beside or below the transcript. One row per speaker: the label, an editable name box, and the first full sentence that speaker said. A play-a-sample button if it's cheap with NAudio. Renaming updates the transcript immediately.
7. **Actions:** Save as… (default `<recording name> - transcript.txt` in the recording's folder), Copy all, Open folder.

**Behaviour**

- Auto-save to the default path as soon as a job finishes. Never overwrite an existing file; add " (2)" instead. Later renames and edits are saved with Save, which overwrites that auto-saved file.
- Remember the last options, the recording mode for each playback device, and the window size in `%LOCALAPPDATA%\Talktrace\settings.json`.
- The window stays responsive throughout. Cancel takes effect within about 2 s at any stage. Closing the window mid-job asks for confirmation, then cancels.
- Errors appear inline in plain language, with a Copy details link; the full details go to the log.
- One file at a time; no queue.
- Shortcuts: Ctrl+O choose recording, Ctrl+S save, Esc cancel a running job.

## 11. Output format

Plain UTF-8 text: a short header, then one paragraph per speaker turn with a blank line between turns. No hard line wrapping.

```
Transcript: Q3 planning session.m4a
Transcribed: 2026-10-29 · Duration: 52:14 · Model: Fast (English)
Speakers: Dana, Raj, Speaker 3

Dana: Okay, let's get started. I want to cover the ERP timeline first and then budget.

Raj: Sounds good. I've got the updated vendor numbers.

Dana: Great, go ahead.
```

- **Speakers line:** every speaker in order of first appearance, by current name; unrenamed speakers show as Speaker N. Omitted when speaker labels are off.
- **With timestamps:** each turn starts with its start time: `[00:03:12] Speaker 1: …`. For recordings, times are from the start of the recording.
- **Renamed speakers:** the chosen names replace the labels everywhere.
- **Speaker labels off:** no labels; paragraphs split at pauses longer than about 2 s, and only there.

The CLI writes the same format:

```
Talktrace.Cli.exe <file> [--speakers auto|none|1..8] [--model fast|multilingual]
                         [--timestamps] [--out <path>] [--threads <n>] [--threshold <x>]
```

`--speakers` defaults to auto. `--threshold` sets Auto-detect's clustering threshold.

When it finishes, the CLI prints per-stage timings and the real-time factor.

## 12. Performance, cancellation and logging

- **Threading.** All heavy work happens in the worker. The app's UI thread only handles worker messages and user input.
- **Cancellation.** Within about 2 s at any stage (section 6).
- **Native resources.** Dispose recognizers, diarizers and streams deterministically in the worker; the process exiting after each job frees anything left.
- **Speed target.** Fast profile, 60-minute 3-speaker recording, under 20 minutes on this machine. Baseline from the 50-minute test: about 2 minutes transcription and about 6 minutes speaker identification. Measure in Milestones 2 and 3; if far off, profile and report where the time goes before optimising.
- **Milestone 2 result** (speaker labels off, 49-minute M4A test recording, quiet machine): reading audio 13 s, finding speech 10 s, transcribing 88 s; 110 s in total, real-time factor 0.037, peak memory 1.86 GB.
- **Milestone 3 result** (speaker labels on, Auto, quiet machine, on mains power): 49-minute test recording: reading audio 13 s, finding speech 10 s, identifying speakers 247 s, transcribing 91 s; 360 s in total, real-time factor 0.122, peak memory 1.88 GB; 4 speakers, 69 turns. `--speakers 5` gave the same 4 speakers: the fifth person said very little, and no fifth voice with words of its own was found. 98-minute file (the test recording joined to itself, 16 kHz WAV): 722 s in total, real-time factor 0.122, the same 4 speakers across both halves. A 60-minute recording would take about 7.3 minutes, well inside the 20-minute target. On battery, the same work ran about 2× slower.
- **Memory, Milestone 3.** 2.59 GB peak on the 98-minute file, about 90 MB over the target; scaling between 49 and 98 minutes puts 90 minutes at about 2.47 GB. Speaker identification alone peaked at 1.83 GB on that file, so the overall peak comes later, most likely from memory sherpa-onnx keeps after speaker identification while the speech model loads. Not yet investigated.
- **Threads.** 6 threads was fastest in the Milestone 2 benchmark and is the default. `Talktrace.Cli <file> --benchmark` re-runs the comparison, for example after a sherpa-onnx upgrade.
- **Memory target.** Under about 2.5 GB peak in the worker for a 90-minute recording on the Fast profile. Report the actual figure. Milestone 2 measured 1.86 GB for 49 minutes without speaker labels. A 90-minute file needs a few hundred MB more for audio, which leaves speaker identification only a few hundred MB of headroom, so measure it in Milestone 3.
- **Logging.** `%LOCALAPPDATA%\Talktrace\logs\`, one file per day, kept for 14 days, shared by the app and the worker (each line tagged with its process). Log app version, model profile, thread count, audio duration, file name and type, wall time per stage, real-time factor, and errors with stack traces. **Never** log transcript text or audio.

## 13. Build and packaging

- **Publish** with `dotnet publish src/Talktrace.App -c Release -r win-arm64 --self-contained true`, and publish the CLI into the same output folder so both share the native DLLs and the app can launch the worker. A plain folder; use single-file only if the native libraries load reliably that way.
- **Verify the architecture.** `scripts/verify-arm64.ps1` reads the PE header of every native binary in the output (the .exe files, `onnxruntime.dll`, the sherpa-onnx DLLs and any others) and fails unless each is ARM64 (machine type 0xAA64). ARM64X hybrids pass, because an ARM64 process runs their native ARM64 code; ARM64EC and x64 files fail. Managed IL-only assemblies, detected by their CLR header (they report an i386 machine type), are architecture-neutral and pass. It also lists any Visual C++ runtime DLLs the native files import, for information. Run it after every publish.
- **Package.** `scripts/package.ps1` produces `Talktrace-<version>-win-arm64.zip` with the publish folder, README and THIRD-PARTY-NOTICES, and no models.
- **Shortcut.** `scripts/install-shortcut.ps1` adds a Start menu shortcut for the current user. No admin rights.
- **Signing.** Unsigned. The README explains the SmartScreen prompt on first launch (More info → Run anyway).
- **Prerequisites** in the README: the ARM64 .NET 10 SDK and Git. Visual Studio is optional.
- **Where it's built.** On the ARM64 machine itself, so everything is built and tested natively. If ever built elsewhere (Linux or cloud, with `EnableWindowsTargeting=true`), say so clearly and give exact test steps rather than reporting it as working.

## 14. Milestones

Work through these in order. Stop at the end of each one and show the results before moving on.

1. **Scaffold and smoke test.** Repo layout, `CLAUDE.md`, `.gitignore`, and `get-models.ps1` for the Fast profile, VAD and diarization models. The CLI transcribes a sherpa-onnx sample WAV with Parakeet, and token timestamps come through the C# API. Run `verify-arm64.ps1`.
   - Show: the build output, the ARM64 check, and the sample transcript with word times.
2. **Transcription without speakers.** Media Foundation decoding for WAV, MP3, M4A and MP4; VAD; chunking; ASR with word times; output with speaker labels off; thread benchmark. CLI only.
   - Show: a benchmark on the test recording, with per-stage times, the real-time factor and the thread results.
3. **Speaker labels.** Diarization, speaker assignment to words, turn assembly, speaker renumbering, and unit tests for that logic. CLI only, with `--speakers auto|none|1..8` (default auto). Also:
   - Run the test recording with a fixed count of 3 and with Auto-detect. The earlier 50-minute test only tried a fixed count, so tune the default clustering threshold if Auto finds the wrong number.
   - Measure peak memory with speaker identification on a recording over 90 minutes (see section 15, Long input).
   - Settle open item 1 (long single-speaker turns) with the user.
   - Show: the labelled transcript of the test recording, with timings.
4. **The app.** The WPF window wired to the worker: choose or drop a file, options, progress, cancel, transcript view, save and copy.
   - Show: the running app, tried on a real recording.
5. **Polish.** The speakers panel with rename, settings persistence, auto-save, plain-language errors, logging, and the Multilingual profile.
6. **Package.** Publish, ARM64 verification, the zip, README, THIRD-PARTY-NOTICES and the shortcut script.
7. **Record a meeting.** The Record a meeting tab, microphone and system-audio capture, silence keep-alive, the aligned writer, the three modes, crash-safe files and recovery, the two-track transcription path and echo removal. Then repeat the Milestone 6 packaging steps.
   - Show: a recorded Teams test call with my voice labelled by name, and the 90-minute alignment check.

**Ask first before:**

- adding any dependency this document doesn't name
- writing any code that touches the network outside `get-models.ps1`
- doing anything that needs admin rights
- changing the stack or the repository layout

## 15. Testing and acceptance

**Testing**

- **Unit tests, no models needed:** chunk building (packing, splitting, padding), assigning speakers to words and smoothing, speaker renumbering, turn assembly, paragraphs with labels off, echo removal, formatting with and without timestamps, renaming and the Speakers line, the aligned writer (with simulated device clocks), and WAV header recovery.
- **Integration tests** run only when `TALKTRACE_MODELS_DIR` points at the models (using the sherpa-onnx sample WAVs), plus local recordings when `TALKTRACE_TESTDATA` points at `testdata\`; otherwise they skip.
- **Audio inputs:** WAV, MP3, M4A, a Teams or Zoom MP4 export, WMA, a stereo file, 44.1 and 48 kHz files, a corrupt file and a zero-length file.
- **Long input:** one recording over 90 minutes, checking memory and time. No real one exists yet; Milestone 3 made `testdata\test_recording x2.wav` (98 minutes, the test recording joined to itself, 16 kHz mono) for this.
- **Test recordings:** provided on request and kept in the gitignored `testdata\` folder. Nothing from it is ever committed.
- **Offline check:** run a full job with networking turned off.
- **Recording:** a Teams call while Teams has the mic open, once on the speakerphone, once on the Bluetooth headphones and once on the laptop's own speakers and mic; connecting the headphones mid-call; an in-person meeting; pause and resume; unplugging a device mid-call; long silences on the playback device; killing the app mid-recording, then recovering.
- **Echo check:** on the speakerphone and on the laptop's speakers and mic, the other participants' words must not also appear under my name.
- **Alignment check:** record for 90 minutes with the laptop's built-in microphone and the speakerphone as the playback device, while a short beep plays every minute. The right channel gets each beep directly and the left channel through the room, so the gap between them should stay the same; it must not change by more than 0.25 s from start to end. (Using the speakerphone's own microphone wouldn't work: its echo cancellation removes the beep. Two separate devices are also the worst case for drift.)

**Done when**

- [ ] `verify-arm64.ps1` passes: every shipped native binary is ARM64.
- [ ] A full transcription completes with the network disconnected.
- [ ] In the app, I can choose a recording, click Transcribe and get a saved .txt with speaker labels.
- [ ] WAV, MP3, M4A and Teams/Zoom MP4 files all work.
- [ ] The window stays responsive, and Cancel stops a run within a few seconds at any stage.
- [ ] Renaming a speaker updates the on-screen transcript, the Speakers line and the saved file.
- [ ] The Fast profile meets the speed target, or the reason it can't is explained.
- [ ] The README covers setup, model download, usage, settings and known limits.
- [ ] The logs contain no transcript text.
- [ ] I can record a Teams call while Teams is using the microphone, and both my voice and the other participants are captured, on the speakerphone, the Bluetooth headphones and the laptop's own speakers and mic.
- [ ] Talktrace preselects the right call mode for each of those setups.
- [ ] In the call modes, my speech is labelled with my name and everyone else gets speaker numbers.
- [ ] On the speakerphone and the laptop's speakers, the other participants' words don't also appear under my name.
- [ ] Killing the app mid-recording leaves a playable file that Talktrace offers to recover.
- [ ] The two tracks stay within about 0.25 s of each other over a 90-minute recording.

## 16. Known pitfalls

- **Speaker errors.** Diarization sometimes splits one person into two speakers, merges two similar voices, or pins a short interjection on the wrong person. The speaker-count option and the clustering threshold are the fixes; the README explains both.
- **Diarization can't be cancelled in-process.** sherpa-onnx ignores the progress callback's return value. This is why jobs run in a worker.
- **A diarizer that fails to load crashes on first use.** sherpa-onnx returns a null handle, which its C# wrapper doesn't check. Talktrace checks it (section 3); re-check after any sherpa-onnx upgrade, since the check reads a private field.
- **The int8 segmentation model misses speech.** On the test recording it found no speaker for a third of the words. Use the full-precision `model.onnx`.
- **sherpa-onnx's clustering over-splits and wastes fixed counts.** Complete linkage with no minimum cluster size turns stray embeddings into near-empty speakers. Talktrace merges by voice afterwards (section 7, step 4). Each extra group also costs memory: 61 groups pushed a 49-minute job to a 3.4 GB peak.
- **Very quiet participants.** Someone with less than about 15 s of speech is folded into the closest voice on Auto. Choosing the speaker count keeps them as a separate speaker when their voice is distinct.
- **Parakeet model type.** Configure it as a NeMo transducer, or let sherpa-onnx detect the type from the metadata.
- **Benchmark on a quiet machine.** Stage timings varied up to 3× when other heavy work (a build, a test run) ran at the same time.
- **Model paths must be plain ASCII.** sherpa-onnx's C# wrapper passes paths as ANSI strings, so a model folder under a user name with accented characters may not open. Talktrace checks and says so; `TALKTRACE_MODELS_DIR` can point somewhere else.
- **The release name `speaker-recongition-models`** is misspelled in sherpa-onnx's own URLs. Copy it exactly.
- **Wrong native package.** An ARM64 process can't load x64 DLLs: a win-x64 runtime package builds but fails at load time with a DLL or bad-image error. Always use the win-arm64 runtime package.
- **Windows "N" editions** ship without Media Foundation. Detect this and point to the Media Feature Pack.
- **Unsupported codecs.** Some formats, such as Opus or WebM, may not decode depending on installed codecs. Show a clear message rather than failing silently.
- **Loopback silence.** WASAPI loopback delivers no data while nothing plays; the silence keep-alive handles it.
- **Two clocks.** The microphone and playback devices run on separate clocks and drift apart; the aligned writer handles it.
- **Bluetooth headphones** drop into a low-quality hands-free mode when any app opens their mic, and may appear to Windows as a different device at that moment. Talktrace follows the device change and warns about low microphone quality. The README explains the trade-off and that wired or USB headphones give better transcripts.
- **Speakerphone echo cancellation** removes anything played through the speakerphone from its own microphone. That's what makes it work well for calls, but it also means a test sound played through it won't reach its microphone.
- **Exclusive mode.** If another app has taken exclusive control of the microphone, capture fails. Show a clear message pointing to the Windows sound setting.
- **Microphone permission.** Windows privacy settings must allow desktop apps to use the microphone. Detect access-denied and say how to fix it.
- **Laptop lid.** Closing the lid can sleep the PC despite `SetThreadExecutionState`, depending on power settings.

## 17. Out of scope for v1

Real-time (live) transcription, NPU or GPU acceleration, Whisper or any other ASR engine, summaries or any LLM features, a batch queue, an installer or MSIX package, code signing, auto-update, translation, capturing just one app's audio (such as only Teams) instead of all system audio, and any cloud service.

## 18. Keeping the Microsoft Store path open

A Microsoft Store release (MSIX, ARM64 and x64, possibly paid or with a Pro tier) is a possible future direction. It isn't built until asked, but **no v1 design choice may compromise it**. Check every milestone against the rules below. If a choice would break one, stop and put it to the user rather than working around it quietly.

**Rules**

1. **Never write to the app's own folder.** A Store install is read-only. Settings, logs and downloaded models go under `%LOCALAPPDATA%\Talktrace`, recordings under Documents, and transcripts next to the recording. The `models\` folder next to the exe is only ever read.
2. **Find files relative to the app or to a known folder.** Use `AppContext.BaseDirectory` or Windows known folders. No hard-coded install paths, and no reliance on the current directory or PATH. The app launches the worker from its own folder.
3. **Keep the processor architecture in the build, not the code.** `Directory.Build.props` sets `win-arm64` as the default, and it must stay overridable with `-r win-x64`. No code may assume ARM64, and defaults such as the thread count must be safe on other processors. When x64 builds start, `verify-arm64.ps1` gets an architecture switch.
4. **Commercial-use licences only.** Everything shipped must allow commercial use, with its attribution recorded in `THIRD-PARTY-NOTICES.md`. All current components do. Parakeet's CC-BY-4.0 licence requires attribution, which the Store build shows on an About screen.
5. **No downloads at runtime, ever** (hard constraint 2). The Store build ships the Fast profile models inside the package. Extra models, such as Multilingual, would come as an app update or an MSIX optional package, never a download by the app.
6. **Need only the `runFullTrust` and microphone capabilities.** Shared-mode WASAPI capture, including loopback, works in a packaged full-trust app. Don't add features that need broader capabilities, such as unrestricted file-system access.
7. **Expect app data to be virtualised.** A packaged app's writes under `%LOCALAPPDATA%` land in its private package folder, which other programs can't see. Development already ran into this: the Claude desktop app is packaged, so files Claude Code wrote there were invisible to the user's own terminal (see CLAUDE.md). So never ask the user to find a file by its AppData path: give in-app Open folder and Copy details actions instead. Never exchange files with outside programs through AppData.
8. **Keep version numbers MSIX-compatible.** Use three parts now (`0.1.0`), which map to MSIX's four-part form (`0.1.0.0`).
9. **Keep licensing out of v1.** When a trial or Pro check is added, it sits behind one interface so the unpackaged build runs without it. The Store APIs (`StoreContext`) need a Windows-SDK-versioned target framework, such as `net10.0-windows10.0.19041.0`; that's a one-line change when the time comes.

**What a Store release adds** (Milestones 8 and 9, when asked):

- An MSIX bundle with ARM64 and x64 builds and the Fast profile models inside. Confirm that microphone and system-audio capture both work when packaged.
- An About screen listing every model and library with its licence and attribution.
- Trial or Pro licensing through `StoreContext`, working offline once a licence is cached.
- A privacy policy at a public URL, a recording-consent note in the app and the listing, the listing materials, and a test with a private audience before public release.

The business decisions for a Store release (name clearance, pricing, account type, tax) are the user's and are kept outside this public repository.

## 19. Open items

(Open item 1, long single-speaker turns, was settled in Milestone 3: turns stay whole. See section 3.)

2. **Peak memory on long recordings.** The 98-minute file peaked at 2.59 GB, about 90 MB over the target (section 12); about 2.47 GB is expected at 90 minutes. Speaker identification alone peaks at 1.83 GB, so find which later stage sets the peak before trying to reduce it. The user chose to leave this for later after Milestone 3.

## 20. References

- sherpa-onnx: [GitHub](https://github.com/k2-fsa/sherpa-onnx), [documentation](https://k2-fsa.github.io/sherpa/onnx/), [C# wrapper sources](https://github.com/k2-fsa/sherpa-onnx/tree/master/scripts/dotnet) and [.NET examples](https://github.com/k2-fsa/sherpa-onnx/tree/master/dotnet-examples)
- [sherpa-onnx win-arm64 runtime package on NuGet](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx.runtime.win-arm64)
- [sherpa-onnx speaker diarization models](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html)
- [Parakeet TDT 0.6B v2 in sherpa-onnx](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/nemo/parakeet-tdt-0.6b-v2.html)
- [NAudio on GitHub](https://github.com/naudio/NAudio) (the official repository; ignore forks)
- [Choose a distribution path for your Windows app](https://learn.microsoft.com/windows/apps/package-and-deploy/choose-distribution-path), [Microsoft Store policies](https://learn.microsoft.com/windows/uwp/publish/store-policies) and [free developer registration for individuals](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/), for the future Store release
