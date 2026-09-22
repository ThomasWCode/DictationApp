# DictationApp

Hold-to-talk dictation for Windows in the style of Wispr Flow, built on AssemblyAI. Hold **Ctrl+Win**, speak,
release: cleaned-up text lands in whatever text box has focus. If nothing editable has focus the text goes to the
clipboard with a toast. Native .NET 8 + WPF, tray-resident, ~60 MB idle.

- **Speech-to-text**: AssemblyAI Universal-3.5 Pro streaming (WebSocket, 16 kHz PCM), with the personal
  dictionary sent as keyterms.
- **Cleanup and tone**: one call to the AssemblyAI LLM Gateway (levels None/Light/Medium/High, tones
  Neutral/Formal/Casual) with a fallback chain and strict output validation. If the gateway is unavailable the
  raw transcript is inserted and the Flow bar shows "cleanup skipped".
- **App-aware**: per-application rules (Outlook → Formal, Teams → Casual, VS Code → no cleanup…), browser tab
  host detection for Gmail/Docs, tone and level chips on the Flow bar, arrow keys to override while holding.
- **History**: every dictation with searchable text and playable audio, retry for failed ones, "Undo AI edit",
  configurable retention.
- **Personal dictionary** with a "Correct last dictation" dialog that diffs your edits into new terms.

Docs: [features](docs/features.md) · [implementation](docs/implementation.md) · [decisions](docs/decisions.md) ·
[original plan](docs/PLAN.md) · [manual test checklist](docs/manual-test-checklist.md)

## Build and run

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/ThomasWCode/DictationApp && cd DictationApp
dotnet build
dotnet test
dotnet run --project src/DictationApp
```

First run opens a wizard for the API key, microphone and hotkey. The key is stored DPAPI-encrypted in
`%LOCALAPPDATA%\DictationApp\settings.json`; alternatively set `ASSEMBLYAI_API_KEY`. Logs are in
`%LOCALAPPDATA%\DictationApp\logs`, history in `history.db`, audio in `audio\`.

### Command-line switches

| Switch | Purpose |
|---|---|
| `--stream-test file.wav [--output report.txt]` | Streams a WAV through a real session and prints every turn, connect latency and the final text. |
| `--simulate file.wav [--delay 5]` | Runs a complete dictation with the WAV standing in for the microphone: context capture, streaming, cleanup, insertion into the focused window, history. |
| `--settings` | Open Settings on start. |
| `--minimized` | Used by the autostart entry; skips the first-run wizard. |
| `--accept-injected-keys` | Treat synthetic key events as real (automation/testing). |

A WAV for testing can be produced with Windows speech synthesis:

```powershell
Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$s.SetOutputToWaveFile("sample.wav"); $s.Speak("Hello, this is a test of the dictation app."); $s.Dispose()
```

## Packaging

```powershell
dotnet tool install -g vpk
./build/pack.ps1 -Version 0.1.0     # → artifacts/releases (Velopack Setup.exe + update feed)
```

Tagging `vX.Y.Z` runs the release workflow and attaches the installer to a GitHub Release, which the in-app
update check uses. The executable is unsigned; SmartScreen will show "More info → Run anyway".

## Layout

```
src/DictationApp.Core      platform-independent: transcriber, state machine, cleanup, rules, history (net8.0)
src/DictationApp.Windows   Win32/UIA/WASAPI/clipboard adapters (net8.0-windows)
src/DictationApp           WPF tray app: Flow bar, Settings, History, first-run wizard (net8.0-windows)
tests/                     xUnit: 129 Core + 14 Windows tests
build/                     icon generator, pack.ps1
```
