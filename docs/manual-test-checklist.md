# Manual test checklist

Run on an unlocked Windows 10/11 desktop with a microphone and a valid AssemblyAI key. For every row check:
hold/release works, live text appears on the Flow bar, text is inserted, the previous clipboard content is
restored, and the Start menu stays closed (Ctrl+Win chord).

Automated coverage that does not need a desktop: `dotnet test DictationApp.sln` (143 tests) and
`DictationApp.exe --stream-test sample.wav` (real streaming session). `DictationApp.exe --simulate sample.wav --delay 5`
runs the whole pipeline with a WAV in place of the microphone and pastes into whatever has focus after 5 s.

## Insertion targets

| Target | Expected | Result |
|---|---|---|
| Notepad (Win11) | UIA reports Edit/Document; text pasted at caret | |
| Word, Outlook desktop (new + classic) | Rich paste OK; Formal tone chip pre-selected (app rule) | |
| Chrome/Edge: Gmail compose | URL rule → Formal; address bar read in log | |
| Chrome/Edge: plain textarea | Inserted | |
| Chrome/Edge: address bar (omnibox) | Inserted into omnibox | |
| Teams, Slack, WhatsApp desktop | Electron; Casual tone chip pre-selected | |
| VS Code | Level None (no LLM call, see log) | |
| Windows Terminal, PowerShell console | Caret fallback / allowlist; inserted | |
| Elevated Notepad (Run as administrator) | Toast "Target is elevated, text copied"; clipboard holds text | |
| Desktop / Explorer with no edit focus | Toast "No text box has focus, text copied" | |

## Hotkey edge cases

| Case | Expected | Result |
|---|---|---|
| Tap chord < 300 ms | Nothing happens; no history record; no billing (session never began) | |
| Win+E while app running | Explorer opens; no dictation | |
| Win alone | Start menu opens | |
| Win+Ctrl+Left during dictation | Virtual desktop does NOT switch; tone chip changes | |
| Esc during dictation | Bar shows "Discarded"; nothing inserted or stored | |
| Custom chord (e.g. Ctrl+Alt, F8, CapsLock) | Works after Save | |
| Double-tap toggle mode | Tap twice starts, tap once stops | |
| 5-minute dictation | Many turns concatenated in order; nothing lost at release | |
| Chord pressed again mid-dictation | Bar flashes; ignored | |
| Pause hotkey (tray) | Chord does nothing until unpaused | |

## Flow bar

| Case | Expected | Result |
|---|---|---|
| Bar never steals focus | Caret stays in target while bar visible | |
| Click tone/level chip with mouse | Chip highlights; applies to this dictation only | |
| "cleanup skipped" badge | Shown when LLM gateway fails; raw text inserted | |
| "Nothing heard" badge | Silent dictation; no history record, no WAV left behind | |
| Multi-monitor | Bar appears on the monitor of the target window | |

## History and audio

| Case | Expected | Result |
|---|---|---|
| Every dictation listed with playable WAV | Play/Stop works | |
| Search | Prefix search over raw and inserted text | |
| Re-insert | Pastes into the window that gets focus after History minimises | |
| Retry on a Failed record | Re-streams WAV at 4×; text copied; status CopiedOnly | |
| Undo AI edit | Raw transcript copied; record marked | |
| Retention 24 h / 14 d / forever | Old records and WAVs purged hourly and at startup | |
| "Store audio" off | No WAV written; Play disabled | |
| Delete all history | Table and audio folder emptied | |

## Network, privacy, install

| Case | Expected | Result |
|---|---|---|
| Wi-Fi off before dictation | Toast "Network error, saved to history"; WAV kept; Retry later works | |
| Wi-Fi off mid-dictation | Same; partial transcript kept | |
| Mic blocked in Settings > Privacy | Toast with link to ms-settings:privacy-microphone | |
| Mic muted / silent | Toast "No sound from microphone" after 2 s | |
| Fresh install (Setup.exe) | Installs to %LOCALAPPDATA%, first-run wizard shown | |
| Autostart on | Run key present; app starts minimised at sign-in | |
| v+1 release | Update downloaded; applied on restart | |
| Uninstall | Run key removed | |
