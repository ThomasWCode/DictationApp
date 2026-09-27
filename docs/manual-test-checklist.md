# Manual test checklist

Run on an unlocked Windows 10/11 desktop with a microphone and a valid AssemblyAI key. For every row check:
hold/release works, live text appears on the Flow bar, text is inserted, the previous clipboard content is
restored where the paste can be read back (otherwise the dictation stays on the clipboard and the bar says "also
copied"), and the Start menu stays closed (Ctrl+Win chord). Record the Windows and app versions with the results.

Automated coverage that does not need a desktop: `dotnet test DictationApp.sln` and
`DictationApp.exe --stream-test sample.wav` (real streaming session). `DictationApp.exe --simulate sample.wav --delay 5`
runs the whole pipeline with a WAV in place of the microphone and pastes into whatever has focus after 5 s.

## Insertion targets

| Target | Expected | Result |
|---|---|---|
| Notepad (Win11) | UIA reports Edit/Document; text pasted at caret | |
| Word, Outlook desktop (new + classic) | Rich paste OK; default tone chip (Formal only if you added an app rule) | |
| Chrome/Edge: Gmail compose | Address bar read in log; a `mail.google.com` rule, if added, applies | |
| Chrome/Edge: plain textarea | Inserted | |
| Chrome/Edge: address bar (omnibox) | Inserted into omnibox | |
| Teams, Slack, WhatsApp desktop | Electron; default tone chip (Casual only if you added an app rule) | |
| VS Code | Level None (no LLM call, see log) | |
| Windows Terminal, PowerShell console | Caret fallback / allowlist; inserted | |
| Elevated Notepad (Run as administrator) | Toast "Target is elevated, text copied"; clipboard holds text | |
| Desktop / Explorer with no edit focus | Toast "No text box has focus, text copied" | |
| Chrome/Edge page with no field focused | Paste goes nowhere; bar shows "also copied"; clipboard holds the text | |
| Switch to another window during "Inserting" (e.g. a temporary delay build) | No paste into the wrong place: pasted into the new foreground or copied with a toast naming the app | |
| Target window closed during "Cleaning up" | Toast "... was closed, text copied"; record CopiedOnly | |

## Hotkey edge cases

| Case | Expected | Result |
|---|---|---|
| Tap chord < 300 ms | Cancelled after the 400 ms double-tap window; no history record; log shows "Short tap" | |
| Tap chord while the window lookup is slow (a heavy browser page) | Still a tap: cancelled, not a "Nothing heard" dictation | |
| Win+E while app running | Explorer opens; no dictation | |
| Win alone | Start menu opens | |
| Win+Ctrl+Left during dictation | Virtual desktop does NOT switch; tone chip changes | |
| Esc during dictation (held or hands-free) | Bar shows "Discarded"; nothing inserted or stored | |
| Esc right after releasing (while "Finishing"/"Cleaning up") | Ignored by DictationApp (reaches the app); the dictation is inserted | |
| Esc after the time cap ended a hands-free dictation | Ignored; the dictation is inserted | |
| Custom chord (e.g. Ctrl+Alt, F8, CapsLock) | Works after Save | |
| Double-tap toggle mode | Tap twice starts (words spoken right after the second tap are kept), tap once stops | |
| Tray "Start dictation" | Runs hands-free: Esc discards, the chord or "Stop dictation" stops | |
| 5-minute dictation | Many turns concatenated in order; nothing lost at release | |
| Chord pressed again mid-dictation | Bar flashes; ignored | |
| Pause hotkey (tray) | Chord does nothing until unpaused | |
| First-run wizard hotkey step | "Detected!" then "It works. Nothing was recorded."; no history record, no clipboard change | |
| Settings chord recorder | Pressing the current chord in the box records it and starts no dictation | |
| Quit from the tray during a hands-free dictation | No "Dictation limit" toast; History shows a Failed "Interrupted" record with audio; Retry works | |
| End the process (Task Manager) during a dictation, then start the app | Within ~15 s a "Dictation recovered" toast; History shows it as Failed; Retry works | |

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
| Re-insert | Pastes into the window that gets focus after History minimises; no stray leading space in an empty field | |
| Re-insert with no text box focused | Toast "No text box has focus, text copied"; History status says so | |
| Retry on a Failed record of ~30 s | Takes about 30 s; the full text (compare with Play) is copied; status CopiedOnly | |
| Press the hotkey while that Retry runs (History window closed or open) | The bar flashes busy; no dictation starts; the Retry finishes | |
| Delete a record while its Retry runs, or Delete all history in Settings | The Retry stops: no "Retry complete" toast and nothing copied; a new dictation starts normally | |
| Retry with Wi-Fi off halfway | Toast "Retry failed"; record keeps its earlier words and stays retryable | |
| Undo AI edit | Raw transcript copied; record marked | |
| Retention 24 h / 14 d / forever | Old records and WAVs purged hourly and at startup | |
| "Store audio" off | No WAV written; Play disabled | |
| Delete all history (first copy an extra WAV into the audio folder, and play one in History) | Records, every WAV (the extra one too) and the log files gone; a new log file for today appears at the next dictation and keeps growing; status counts what was removed | |
| Correct last dictation from Settings › Dictionary, then Save Settings | The new starred terms are still there afterwards | |

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
