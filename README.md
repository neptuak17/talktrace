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

To transcribe a recording from the command line (speaker labels aren't available yet):

```powershell
.\src\Talktrace.Cli\bin\Release\net10.0-windows\win-arm64\Talktrace.Cli.exe "C:\path\to\meeting.m4a" --timestamps
```

The transcript is saved next to the recording as `<name> - transcript.txt`, and the CLI prints how long each stage took.

## Licence

No licence has been chosen yet; all rights reserved. Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
