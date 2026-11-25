# Talktrace

A native Windows on ARM64 desktop app that turns meeting recordings into speaker-labelled text transcripts, entirely offline.

**[DESIGN.md](DESIGN.md) is the source of truth.** Read it before changing anything, and update it when a decision changes. Work milestone by milestone (DESIGN.md section 14) and stop at the end of each one for review.

Progress: Milestones 1 (scaffold), 2 (transcription without speakers), 3 (speaker labels), 4 (the app) and 5 (polish) done and reviewed; the Multilingual profile was dropped from v1. Milestone 6 (package: a per-user ARM64 MSI with the models inside) done; the user installed it, transcribed offline, kept the icon, and checked installing a rebuild over an installed copy, uninstalling and reinstalling. Not yet tried: a real Teams or Zoom MP4. Milestone 7 (record a meeting): recording, the two-track path and the Record a meeting tab built and checked by Claude; waiting on the user's Teams calls on the three setups (the agreed check-in), then polish and repackaging as 1.0.0.

## Working agreement

- One milestone at a time. At the end of each, check the work against the Store rules in DESIGN.md section 18, show the "Show:" items from section 14, and stop for the user's review.
- Commit and push only when the user says so, straight to `main` (no branches or pull requests). Before committing, check `git status` shows nothing from `testdata\`, `models\` or `artifacts\`.
- Record each decision and finding in DESIGN.md as it happens (the section 3 table for decisions), and keep the Progress line above current.
- Choices about what the user sees (output format, behaviour, defaults) are the user's: put the options to them rather than choosing silently. Example: a 100-word paragraph cap was added without asking and rejected because it cut paragraphs off mid-thought.
- The user runs anything that downloads models (see Notes), and tries each milestone's output themselves.

## Test data

- `testdata\test_recording.m4a` is the user's real meeting: 49:09, AAC at 48 kHz stereo, five speakers (the user and four others, confirmed by the user; the earlier sherpa-onnx test's count of 3 was a guess). It's confidential and local only.
- Check output only by counts (paragraphs, words, speakers, timestamps). Never read, quote, print or log transcript text; the user judges accuracy.
- Baselines: Milestone 2 (labels off) took 110 s in total (real-time factor 0.037) with a 1.86 GB peak. Milestone 3 (labels on, Auto) took 360 s (0.122) with a 1.88 GB peak and found 4 speakers; the 98-minute joined file (`testdata\test_recording x2.wav`) took 722 s with a 2.59 GB peak. Milestone 4's app (Auto) took 355 s with a 1.88 GB worker peak, and its transcript matched the CLI's.
- Run timing jobs (full transcriptions, `--benchmark`) with nothing else running and the laptop on mains power; a concurrent build or test run skewed timings up to 3×, and battery power about 2×.
- Speaker identification takes 4–9 minutes per run. To compare speaker settings, save the diarization segments and word times once (numbers only, outside the repo) and re-run the merge and assignment on them, rather than re-running the whole job.

## Speaker labels (Milestone 3)

- Speakers are found in two stages (DESIGN.md section 7, step 4): `SpeakerDiarizer.Diarize` runs sherpa-onnx set to over-split, and `SpeakerMerger` merges the groups by voice using TitaNet voice prints. A chosen count is applied by the merge, never by sherpa-onnx's own cluster count.
- Speaker segmentation uses pyannote's full-precision `model.onnx`; the int8 model missed a third of the speech.
- With labels on, turns stay whole however long (the user's decision; DESIGN.md section 3).

## The app (Milestone 4)

- The app runs every job through Core's `WorkerClient`, which starts `Talktrace.Cli.exe --worker` from the app's own folder. The app's project reference to the CLI puts the exe there in builds; the tests reference it the same way for the worker tests.
- WPF with `ThemeMode="System"` (the Fluent theme, still experimental: WPF0001 is suppressed in the app's project). WPF projects' implicit usings leave out `System.IO`.
- `ProgressBar.Value` (any `RangeBase`) binds two-way by default; bind it to a read-only view-model property with `Mode=OneWay`, or the window crashes on load.
- To check the app on a real recording without reading the transcript, drive it with UI Automation from PowerShell and read only these elements, by automation ID: `StageText`, `TimeText`, `StatusText`, `ErrorMessage`, `ModelsMessage`, `SpeakerLabel` and `SpeakerNameError`. Never read `SpeakerSample` (each speaker's sample sentence is transcript text) or the `Transcript` box; count in the script and print only numbers. Never screenshot the window while a real recording's transcript is on screen, and never press a play button on a real recording (it plays the meeting aloud). Screenshots of the window: `PrintWindow` with flag 2 after `SetProcessDPIAware`. A public test file with two voices: the sherpa-onnx sample joined to a slowed copy of itself, transcribed with `--speakers 2`.
  - Match stage text exactly ("Cancelled", not "Cancel").
  - The window is found as soon as it has a title, before its content loads; wait for the Transcribe button.
  - A WPF `ToggleButton` breaks a one-way `IsChecked` binding when clicked, so the play buttons are plain buttons whose name and icon follow the state ("Play a sample of Speaker 1", "Stop the sample of Speaker 1").
  - Invoking a WPF button through UI Automation returns before its click runs, so a dialog it opens appears a moment later; wait for it.
  - The file dialogs' controls show as panes, without patterns. Open dialog: `WM_SETTEXT` the file name into the last control with id 1148, then click Open (id 1). Save dialog: the file name box is the id 1001 control whose name is the current file name (another 1001 is the address bar).
  - Click dialog buttons with `PostMessage(BM_CLICK)`, not `SendMessage`: if the click opens another modal (such as "replace the file?"), `SendMessage` blocks until it closes.
  - The public two-voice file: the sherpa-onnx sample, 0.8 s of silence, a copy slowed to 0.7× (which also lowers its pitch), silence, then the sample again. A 0.8× copy is too alike and comes out as one speaker.
- The Fluent theme draws an indeterminate progress bar as a moving dash with no track, so a screenshot can miss it.

## Polish (Milestone 5)

- The log (`Talktrace.Core.Logging.Log`) is shared by the app, the worker and the CLI; nothing is written until a process calls `Log.Start`. `TALKTRACE_LOG_DIR` points it somewhere else; set it when running the app or CLI during development so the log is readable, and `LogTests` checks a real job's log for transcript words. `Log.Redact` keeps a path's folder and name out of every line: call it for each recording and transcript path before anything that might log it.
- Settings live in `%LOCALAPPDATA%\Talktrace\settings.json` (`SettingsFile`), which Claude's runs write to the virtualised copy, not the user's.
- NAudio 3.3 marks `WasapiOut` obsolete; play audio with `WasapiPlayerBuilder().WithSharedMode().Build()`.

## Package (Milestone 6)

- Talktrace installs from a per-user ARM64 MSI with the models inside, built with WiX 5.0.2 (pinned in `global.json`; WiX 6 and later charge a maintenance fee, so don't upgrade without asking). `scripts\package.ps1` is the only way to build it: it publishes into a fresh `artifacts\package\Talktrace-<version>` with `ContinuousIntegrationBuild=true` (otherwise the source path, and with it the user name, ends up in the assemblies and symbol files), runs the ARM64 check, adds the docs and the hash-checked models, refuses to package recordings, transcripts, logs, unlisted model files or local paths, and builds `installer\`. Details in DESIGN.md section 13.
- Never install the MSI from Claude's session: Windows Installer would really install it into the user's profile, Start menu and Installed apps. Check it with `msiexec /a <msi> /qn TARGETDIR=<folder under artifacts>`, which only unpacks it, and run the CLI and the app from the unpacked `LocalApp\Programs\Talktrace` on public audio only (the UI Automation notes above apply). Delete the unpacked folder afterwards; it's 840 MB.
- A new model file, or a changed one, needs its SHA-256 in `package.ps1`'s list.
- The README is the user's install guide. When behaviour, options or limits change, update it too.

## Recording (Milestone 7)

- `Talktrace.Recording` runs in the app process: `MeetingRecorder` (devices, writer thread, keep-awake), `AlignedWriter` (pure; unit-tested with simulated clocks), `RecordingWavFile` (crash-safe WAV and recovery), `AudioInput` (NAudio 3.3 `WasapiRecorderBuilder`). The two-track transcription path is in Core (`Transcriber.TranscribeCall`, `Calls\`), chosen by the sidecar (`Recordings\RecordingSidecar`).
- System audio is process loopback excluding Talktrace (`AudioInput.OpenSystemAudio`), never a device's loopback: on this laptop a device's loopback fails (`0x8007045D`) once any app has the microphone open. Process loopback's activation blocks; open it off the UI thread.
- The user's microphone was muted in Windows during Claude's tests, so capture gave zeros; check `AudioDevices.IsMuted` before blaming the code. Whether the microphone captures sound from inside the Claude app's package (where Claude's processes run) hasn't been checked; system audio does.
- To test recording without touching the user's Documents, point Claude's (virtualised) `settings.json` `recordingsFolder` at the scratchpad, then drive the app with UI Automation (automation IDs `RecordTab`, `ModeInPerson`, `ModeCallHeadphones`, `ModeCallSpeakers`, `StartButton`, `PauseButton`, `ResumeButton`, `StopButton`, `ElapsedText`, `RecordingBadge`, `RecordError`, `MicrophoneNote`). Say before any test that plays sound aloud (section 3). A test recording captures whatever the PC plays, so check it by counts only.
- `scripts\verify-arm64.ps1` needs `-Path` when run with `powershell -File` (its default uses `$PSScriptRoot`, empty there in PowerShell 5.1).

## Hard constraints

Short form; the full text is in DESIGN.md section 2.

1. **Native ARM64 only.** Every exe and native DLL is ARM64, never x64 or ARM64EC. Run `scripts\verify-arm64.ps1` after every publish.
2. **Offline at runtime.** No network code anywhere except `scripts\get-models.ps1`.
3. **No Python, PyTorch or ffmpeg.** Self-contained .NET publish.
4. **CPU inference only.**
5. **Open-source components only**, each listed in `THIRD-PARTY-NOTICES.md`.
6. **No admin rights** to install or run the app.
7. **Never log transcript text or audio. The repo is public:** never commit recordings, transcripts, test data or models.

**Keep the Microsoft Store path open** (DESIGN.md section 18). A Store release is a possible future direction, and nothing may compromise it:
- Never write to the app's own folder.
- No hard-coded install paths, and no architecture assumptions in code.
- Only licences that allow commercial use.
- No runtime downloads.
- Only the microphone capability.
- Expect AppData to be virtualised.
- Keep licensing code out of v1.

If a choice would break one of these, ask the user.

## Ask before

- adding any package (versions live only in `Directory.Packages.props`, pinned exactly)
- writing code that touches the network
- doing anything that needs admin rights
- changing the stack or the repository layout

## Commands

The .NET 10 SDK is in `C:\Program Files\dotnet`. If `dotnet` isn't on PATH in a shell, call it by its full path.

```powershell
dotnet build -c Release
dotnet test -c Release        # integration tests skip unless TALKTRACE_MODELS_DIR is set

$env:TALKTRACE_MODELS_DIR = "$env:LOCALAPPDATA\Talktrace\models"
dotnet test -c Release        # now including the tests that run the real models

.\scripts\get-models.ps1      # Fast profile into %LOCALAPPDATA%\Talktrace\models; -Profile All adds Multilingual

.\scripts\package.ps1         # publish, ARM64 check, models, then artifacts\Talktrace-<version>-arm64.msi (about 3 minutes)

dotnet publish src/Talktrace.App -c Release -r win-arm64 --self-contained true -o artifacts/publish
dotnet publish src/Talktrace.Cli -c Release -r win-arm64 --self-contained true -o artifacts/publish
.\scripts\verify-arm64.ps1

.\artifacts\publish\Talktrace.exe                                                   # the app
.\artifacts\publish\Talktrace.Cli.exe <recording> [--speakers auto|none|1..8] [--threshold <x>] [--timestamps] [--threads <n>] [--name <your name>]   # saves "<name> - transcript.txt"
.\artifacts\publish\Talktrace.Cli.exe <recording> --benchmark [--minutes 10]        # thread and batch timings
```

Test recordings live in the gitignored `testdata\` folder. Never print, log or commit their transcripts.

## Notes

- **Claude's writes to `%LOCALAPPDATA%` are invisible to the user.** Claude Code runs inside the Claude desktop app's Windows package, so Windows redirects anything it (or a process it starts) writes under `%LOCALAPPDATA%` to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`. The user's own terminal can't see those files. Model downloads, settings and logs created during a session land there. Have the user run `scripts\get-models.ps1` themselves, or pass `-Destination` to a folder outside AppData. Reads aren't affected: Claude sees files the user puts in `%LOCALAPPDATA%`.

- Tests use xUnit v3 on Microsoft.Testing.Platform, opted in through `global.json`.
- sherpa-onnx receives model paths as ANSI strings, so model paths must be plain ASCII.
- sherpa-onnx's diarization progress callback can't cancel a run; that's why jobs run in a worker process.
- .NET's WPF ships `vcruntime140_cor3.dll` as ARM64X. It runs as native ARM64 in our process, and `verify-arm64.ps1` accepts it.
- The shell is Windows PowerShell 5.1. It turns anything a native tool writes to stderr (git push progress, curl's progress bar) into red NativeCommandError text, and with `$ErrorActionPreference = 'Stop'` into a failure. Set `$ErrorActionPreference = 'Continue'` around native calls, pipe their output through `ForEach-Object { "$_" }`, and judge success by `$LASTEXITCODE`. It also reads a `.ps1` file without a byte-order mark as ANSI, so write non-ASCII characters in scripts as `[char]0x2026` and the like.
- NAudio 3 is span-based: `IWaveProvider.Read(Span<byte>)`. Its source-reader interface isn't public, so `MediaFoundationReader` can't be asked for 16 kHz directly; decode, then use `MediaFoundationResampler`.
- NAudio.Wasapi's NuGet version list includes a stray 22.0.0; keep versions pinned exactly.
- For a multi-line commit message, write it to a file and run `git commit -F <file>`. In Windows PowerShell 5.1, `git commit -F -` with a here-string doesn't reach git's stdin; the text arrives as a path and the commit fails.
