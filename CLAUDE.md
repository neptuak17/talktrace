# Talktrace

A native Windows on ARM64 desktop app that turns meeting recordings into speaker-labelled text transcripts, entirely offline.

**[DESIGN.md](DESIGN.md) is the source of truth.** Read it before changing anything, and update it when a decision changes. Work milestone by milestone (DESIGN.md section 14) and stop at the end of each one for review.

Progress: Milestones 1 (scaffold) and 2 (transcription without speakers) done and reviewed. Next: Milestone 3 (speaker labels).

## Hard constraints

Short form; the full text is in DESIGN.md section 2.

1. **Native ARM64 only.** Every exe and native DLL is ARM64, never x64 or ARM64EC. Run `scripts\verify-arm64.ps1` after every publish.
2. **Offline at runtime.** No network code anywhere except `scripts\get-models.ps1`.
3. **No Python, PyTorch or ffmpeg.** Self-contained .NET publish.
4. **CPU inference only.**
5. **Open-source components only**, each listed in `THIRD-PARTY-NOTICES.md`.
6. **No admin rights** to install or run the app.
7. **Never log transcript text or audio. The repo is public:** never commit recordings, transcripts, test data or models.

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

.\artifacts\publish\Talktrace.Cli.exe <recording> [--timestamps] [--threads <n>]   # saves "<name> - transcript.txt"
.\artifacts\publish\Talktrace.Cli.exe <recording> --benchmark [--minutes 10]        # thread and batch timings
```

Test recordings live in the gitignored `testdata\` folder. Never print, log or commit their transcripts.

## Notes

- **Claude's writes to `%LOCALAPPDATA%` are invisible to the user.** Claude Code runs inside the Claude desktop app's Windows package, so Windows redirects anything it (or a process it starts) writes under `%LOCALAPPDATA%` to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`. The user's own terminal can't see those files. Model downloads, settings and logs created during a session land there. Have the user run `scripts\get-models.ps1` themselves, or pass `-Destination` to a folder outside AppData. Reads aren't affected: Claude sees files the user puts in `%LOCALAPPDATA%`.

- Tests use xUnit v3 on Microsoft.Testing.Platform, opted in through `global.json`.
- sherpa-onnx receives model paths as ANSI strings, so model paths must be plain ASCII.
- sherpa-onnx's diarization progress callback can't cancel a run; that's why jobs run in a worker process.
- .NET's WPF ships `vcruntime140_cor3.dll` as ARM64X. It runs as native ARM64 in our process, and `verify-arm64.ps1` accepts it.
