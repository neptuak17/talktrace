# Talktrace

A native Windows on ARM64 desktop app that turns meeting recordings into speaker-labelled text transcripts, entirely offline.

**[DESIGN.md](DESIGN.md) is the source of truth.** Read it before changing anything, and update it when a decision changes. Work milestone by milestone (DESIGN.md section 14) and stop at the end of each one for review.

Progress: Milestones 1 (scaffold), 2 (transcription without speakers) and 3 (speaker labels) done and reviewed. Next: Milestone 4 (the app).

## Working agreement

- One milestone at a time. At the end of each, check the work against the Store rules in DESIGN.md section 18, show the "Show:" items from section 14, and stop for the user's review.
- Commit and push only when the user says so, straight to `main` (no branches or pull requests). Before committing, check `git status` shows nothing from `testdata\`, `models\` or `artifacts\`.
- Record each decision and finding in DESIGN.md as it happens (the section 3 table for decisions), and keep the Progress line above current.
- Choices about what the user sees (output format, behaviour, defaults) are the user's: put the options to them rather than choosing silently. Example: a 100-word paragraph cap was added without asking and rejected because it cut paragraphs off mid-thought.
- The user runs anything that downloads models (see Notes), and tries each milestone's output themselves.

## Test data

- `testdata\test_recording.m4a` is the user's real meeting: 49:09, AAC at 48 kHz stereo, five speakers (the user and four others, confirmed by the user; the earlier sherpa-onnx test's count of 3 was a guess). It's confidential and local only.
- Check output only by counts (paragraphs, words, speakers, timestamps). Never read, quote, print or log transcript text; the user judges accuracy.
- Baselines: Milestone 2 (labels off) took 110 s in total (real-time factor 0.037) with a 1.86 GB peak. Milestone 3 (labels on, Auto) took 360 s (0.122) with a 1.88 GB peak and found 4 speakers; the 98-minute joined file (`testdata\test_recording x2.wav`) took 722 s with a 2.59 GB peak.
- Run timing jobs (full transcriptions, `--benchmark`) with nothing else running and the laptop on mains power; a concurrent build or test run skewed timings up to 3×, and battery power about 2×.
- Speaker identification takes 4–9 minutes per run. To compare speaker settings, save the diarization segments and word times once (numbers only, outside the repo) and re-run the merge and assignment on them, rather than re-running the whole job.

## Speaker labels (Milestone 3)

- Speakers are found in two stages (DESIGN.md section 7, step 4): `SpeakerDiarizer.Diarize` runs sherpa-onnx set to over-split, and `SpeakerMerger` merges the groups by voice using TitaNet voice prints. A chosen count is applied by the merge, never by sherpa-onnx's own cluster count.
- Speaker segmentation uses pyannote's full-precision `model.onnx`; the int8 model missed a third of the speech.
- With labels on, turns stay whole however long (the user's decision; DESIGN.md section 3).

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

dotnet publish src/Talktrace.App -c Release -r win-arm64 --self-contained true -o artifacts/publish
dotnet publish src/Talktrace.Cli -c Release -r win-arm64 --self-contained true -o artifacts/publish
.\scripts\verify-arm64.ps1

.\artifacts\publish\Talktrace.Cli.exe <recording> [--speakers auto|none|1..8] [--threshold <x>] [--timestamps] [--threads <n>]   # saves "<name> - transcript.txt"
.\artifacts\publish\Talktrace.Cli.exe <recording> --benchmark [--minutes 10]        # thread and batch timings
```

Test recordings live in the gitignored `testdata\` folder. Never print, log or commit their transcripts.

## Notes

- **Claude's writes to `%LOCALAPPDATA%` are invisible to the user.** Claude Code runs inside the Claude desktop app's Windows package, so Windows redirects anything it (or a process it starts) writes under `%LOCALAPPDATA%` to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`. The user's own terminal can't see those files. Model downloads, settings and logs created during a session land there. Have the user run `scripts\get-models.ps1` themselves, or pass `-Destination` to a folder outside AppData. Reads aren't affected: Claude sees files the user puts in `%LOCALAPPDATA%`.

- Tests use xUnit v3 on Microsoft.Testing.Platform, opted in through `global.json`.
- sherpa-onnx receives model paths as ANSI strings, so model paths must be plain ASCII.
- sherpa-onnx's diarization progress callback can't cancel a run; that's why jobs run in a worker process.
- .NET's WPF ships `vcruntime140_cor3.dll` as ARM64X. It runs as native ARM64 in our process, and `verify-arm64.ps1` accepts it.
- The shell is Windows PowerShell 5.1. It turns anything a native tool writes to stderr (git push progress, curl's progress bar) into red NativeCommandError text, and with `$ErrorActionPreference = 'Stop'` into a failure. Set `$ErrorActionPreference = 'Continue'` around native calls, pipe their output through `ForEach-Object { "$_" }`, and judge success by `$LASTEXITCODE`.
- NAudio 3 is span-based: `IWaveProvider.Read(Span<byte>)`. Its source-reader interface isn't public, so `MediaFoundationReader` can't be asked for 16 kHz directly; decode, then use `MediaFoundationResampler`.
- NAudio.Wasapi's NuGet version list includes a stray 22.0.0; keep versions pinned exactly.
- For a multi-line commit message, write it to a file and run `git commit -F <file>`. In Windows PowerShell 5.1, `git commit -F -` with a here-string doesn't reach git's stdin; the text arrives as a path and the commit fails.
