# Talktrace

Talktrace turns a meeting recording into a plain-text transcript with speaker labels, entirely on your own PC. It is a native Windows on ARM64 app. Nothing it hears or writes ever leaves the machine: it makes no network connections at all, and the installer includes everything it needs.

## What you need

- Windows 11 on an ARM64 PC, such as a Snapdragon X laptop.
- About 850 MB of disk space once installed, most of it the speech models.
- Free memory for the job: about 1.9 GB for a 50-minute meeting and about 2.5 GB for 90 minutes.

## Install

Double-click `Talktrace-<version>-arm64.msi`. A progress bar runs briefly, and that's it:

- Talktrace is installed for you only, in `%LOCALAPPDATA%\Programs\Talktrace`. It needs no admin rights and no internet connection.
- It appears in the Start menu, and in Settings → Apps → Installed apps.

Talktrace isn't signed, so if you downloaded the installer, Windows may say "Windows protected your PC". Choose **More info**, then **Run anyway**. (Or before running it, right-click the file, choose Properties, tick **Unblock** and click OK.)

**Upgrading:** run the newer installer. It replaces the old version and keeps your settings.

**Reinstalling:** if Talktrace says its models are missing or can't be loaded, right-click the installer and choose **Repair**, or uninstall Talktrace and run the installer again.

**Uninstalling:** Settings → Apps → Installed apps → Talktrace → Uninstall. This removes the app and its models. Your settings and logs, a few KB, stay in `%LOCALAPPDATA%\Talktrace`; delete that folder to remove them too.

## Using Talktrace

1. **Choose a recording.** Drop it anywhere on the window, or click **Choose recording…** (Ctrl+O). Talktrace reads WAV, MP3, M4A, MP4, WMA and AAC files, and the sound from MOV, M4V and WMV videos, such as a Teams or Zoom recording. It shows the file's length, size and folder.
2. **Pick the options.**
   - **Speakers:** **Auto-detect** (the default) works out how many people spoke. If you know how many there were, choosing the number usually gives better labels. **Don't label speakers** is quickest; the transcript is then split into paragraphs at pauses.
   - **Include timestamps** starts each paragraph with its time in the recording, such as `[00:03:12]`.
   - **Advanced** shows the fine-tuning settings (see Speaker labels below), **Reset**, and **Open log folder**.

   Options apply to the next transcription, never to the transcript on screen.
3. **Click Transcribe.** The window shows each stage, its progress, and the time left once transcription is under way. A 50-minute meeting takes about 6 minutes with speaker labels, most of it spent telling the voices apart, or about 2 minutes without. **Cancel** (or Esc) stops within a couple of seconds and saves nothing.
4. **Read and edit.** The transcript is saved the moment it's done, as `<recording name> - transcript.txt` next to the recording. An existing file is never overwritten; Talktrace adds " (2)" and so on instead. You can edit the text in the window.
   - **Save** (Ctrl+S) writes your edits over that saved file. **Save as…** saves a copy elsewhere. **Copy all** copies the transcript, and **Open folder** shows the file in Explorer.
   - If you start another transcription or close the window with unsaved edits, Talktrace asks first.
   - If the transcript can't be saved next to the recording (a read-only folder or a full disk, say), it still appears, with the reason; use **Save as…**.

### Speakers panel

With speaker labels on, a panel beside the transcript lists the speakers in the order they first spoke, each with a sample sentence.

- **Play** plays a few seconds of that speaker from the recording, to help you tell who's who. Click again to stop.
- **Rename** a speaker by typing a name and pressing Enter. The name replaces "Speaker 1" (or the earlier name) throughout the transcript and in its Speakers line, and your other edits are kept. Two speakers can't share a name. Clearing the name brings back "Speaker N". Click **Save** to write the names to the file.

### The transcript file

Plain UTF-8 text: a short header with the recording's name, the date, its length, the model and the speakers, then one paragraph per turn, with a blank line between them.

```
Transcript: Q3 planning session.m4a
Transcribed: 2026-10-29 · Duration: 52:14 · Model: Fast (English)
Speakers: Dana, Raj, Speaker 3

Dana: Okay, let's get started. I want to cover the ERP timeline first and then budget.

Raj: Sounds good. I've got the updated vendor numbers.
```

### Speaker labels

Telling voices apart is the least reliable part. Talktrace sometimes splits one person into two speakers, joins two similar voices into one, or gives a short interjection to the wrong person.

- **Choose the number of speakers** when you know it. This is the main fix.
- On Auto-detect, someone who says very little (less than about 15 seconds in all) is folded into the closest voice. Choosing the number keeps them separate when their voice is distinct.
- Under **Advanced**, the **clustering threshold** (default 0.6) sets how alike two voices must be to count as one person on Auto-detect. Lower it if two people come out as one; raise it if one person comes out as two.
- With speaker labels on, each turn is one paragraph however long it runs.

### Settings and the log

Talktrace remembers your last options (saved when a transcription starts) and the window's size and position.

It keeps a log, one file a day for 14 days, to help with problems. **Open log folder** (beside **Copy details** on any error, and under **Advanced**) shows it. The log holds timings, the recording's length, type and size, and error details. It never holds transcript text, audio, or the recording's file name or folder.

## Command line

The same transcription without the window:

```powershell
& "$env:LOCALAPPDATA\Programs\Talktrace\Talktrace.Cli.exe" "C:\path\to\meeting.m4a" --speakers auto --timestamps
```

- `--speakers auto|none|1..8`: as in the app (default `auto`).
- `--timestamps`: start each paragraph with its time.
- `--threshold <x>`: the clustering threshold for Auto-detect.
- `--threads <n>`: threads to use (default 6, or fewer on a smaller processor).
- `--out <path>`: where to save, instead of next to the recording.

It prints how long each stage took. `Talktrace.Cli.exe <recording> --benchmark [--minutes 10]` compares thread counts on the first minutes of a recording.

## Known limits

- **English only.**
- **ARM64 only.** There is no x64 build.
- **Speed depends on power.** On battery, Windows slows the processor and a transcription takes about twice as long. Plug in for long recordings.
- **Speaker labels make mistakes** (see Speaker labels above).
- **Windows "N" editions** don't include the media components Talktrace uses to read audio; install the Media Feature Pack from Windows' optional features.
- **Some formats may not open**, such as Opus or WebM, depending on the codecs installed. Talktrace says so rather than failing silently.
- **The models' folder path must be plain ASCII.** If your Windows user name has accented letters, the models in `%LOCALAPPDATA%\Programs\Talktrace\models` can't be loaded, and Talktrace says so. Copy that `models` folder to a plain path, such as `C:\TalktraceModels`, and tell Talktrace where it is with the `TALKTRACE_MODELS_DIR` environment variable (for your user only; no admin rights needed), then start Talktrace again:

  ```powershell
  [Environment]::SetEnvironmentVariable('TALKTRACE_MODELS_DIR', 'C:\TalktraceModels', 'User')
  ```
- **Unsigned.** Windows warns about it the first time (see Install). If Smart App Control is on, Windows blocks unsigned apps outright and Talktrace can't run.
- **No recording yet.** Talktrace transcribes existing recordings; recording meetings in the app is planned.
- **Not live.** Transcription happens after the meeting, never during it.

## Building from source

You need the ARM64 .NET 10 SDK and Git. Visual Studio is optional. Build on the ARM64 PC itself.

```powershell
git clone https://github.com/neptuak17/talktrace.git
cd talktrace
.\scripts\get-models.ps1      # the models, for the tests and the installer: about 680 MB from GitHub
dotnet build -c Release
dotnet test -c Release
```

The tests that run the real models skip unless `TALKTRACE_MODELS_DIR` points at the models:

```powershell
$env:TALKTRACE_MODELS_DIR = "$env:LOCALAPPDATA\Talktrace\models"
dotnet test -c Release
```

To build the installer (it publishes the app, checks that every native file is ARM64, adds the models, and writes `artifacts\Talktrace-<version>-arm64.msi` with the WiX Toolset, which NuGet fetches on first use):

```powershell
.\scripts\package.ps1
```

`DESIGN.md` in the repository describes the design and the decisions behind it.

## Licence

No licence has been chosen yet; all rights reserved. Third-party components and their licences are listed in `THIRD-PARTY-NOTICES.md`.
