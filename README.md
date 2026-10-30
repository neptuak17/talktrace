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

To try transcription on the sample file that comes with the model:

```powershell
.\src\Talktrace.Cli\bin\Release\net10.0-windows\win-arm64\Talktrace.Cli.exe `
    "$env:LOCALAPPDATA\Talktrace\models\sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\test_wavs\0.wav" --words
```

## Licence

No licence has been chosen yet; all rights reserved. Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
