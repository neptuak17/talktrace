# Talktrace

Talktrace turns a meeting recording into a plain-text transcript with speaker labels, entirely on your own PC. It is a native Windows on ARM64 app, and nothing it hears or writes ever leaves the machine.

**Status:** early development. [DESIGN.md](DESIGN.md) describes the design and the plan.

## Building

You need the ARM64 .NET 10 SDK and Git.

```powershell
.\scripts\get-models.ps1      # downloads the models (about 500 MB) into %LOCALAPPDATA%\Talktrace\models
dotnet build -c Release
dotnet test -c Release
```

To run the app after building:

```powershell
.\src\Talktrace.App\bin\Release\net10.0-windows\win-arm64\Talktrace.exe
```

Drop a recording on the window or choose one, pick the options, and click Transcribe. Speaker identification takes most of the time: about 6 minutes for a 50-minute meeting on a Snapdragon X Elite. The transcript is saved next to the recording as `<name> - transcript.txt` as soon as it's done, and shown in the window, where you can edit it and save again.

The same transcription from the command line:

```powershell
.\src\Talktrace.Cli\bin\Release\net10.0-windows\win-arm64\Talktrace.Cli.exe "C:\path\to\meeting.m4a" --speakers auto --timestamps
```

The CLI prints how long each stage took.

## Licence

No licence has been chosen yet; all rights reserved. Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
