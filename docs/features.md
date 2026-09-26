# Features

DictationApp is a Windows tray application. There is no main window: everything happens from the hotkey, the
Flow bar overlay, and the tray menu (Settings, History, Correct last dictation, Pause hotkey, Check for
updates, Open logs folder, Quit). Double-clicking the tray icon opens History.

## Dictating

| Feature | Behaviour |
|---|---|
| Hold-to-talk | Hold the chord (default **Ctrl+Win**), speak, release. Audio is captured from the instant the chord goes down, before the server connection exists, so nothing is lost to connect latency. Releasing ends the recording at once, even while the connection is still being set up; the last fraction of a second before the release is kept. |
| Microphone ready | The microphone is opened once while idle and only started on the key press, so recording begins 15–100 ms after the chord instead of up to 1.5 s (opening DSP microphones such as Intel Smart Sound is slow). Windows shows the microphone as in use only while dictating. The window and browser-URL lookup runs after the microphone has started. A device change, error or turning off Settings › Audio › "Keep the microphone ready" falls back to opening the device on each press. |
| Double-tap for hands-free | Tap the chord twice within 400 ms and the dictation the first tap started keeps running with nothing held (its microphone has been on since that first tap); press the chord once to stop. Both ways work all the time, no mode to choose. In hands-free mode only Escape is intercepted (discard, while it records); the keyboard otherwise behaves normally, so tone and level are changed with the Flow bar chips. "Start dictation" in the tray menu starts a hands-free dictation too. |
| Short tap | A lone press shorter than 300 ms (timed from the key presses themselves, however long the start-up takes) is cancelled once the 400 ms double-tap window has passed: nothing is inserted or stored. The streaming session has usually not begun by then; if it has, at most a fraction of a second is billed. |
| Start menu stays closed | When a chord containing Win fires, an unassigned virtual key (0xE8) is injected so Windows treats the Win press as part of a combination. Win alone still opens Start; Win+E, Win+D and friends work normally. |
| Escape | Discards the current dictation while it records (the chord held, or hands-free until it stops): nothing is inserted or stored. Once released or stopped, the dictation always finishes into the field or onto the clipboard, and Escape goes to the app as usual. |
| Quitting mid-dictation | Quit, sign-out or shutdown during a dictation keeps it in History as Failed ("Interrupted") with its audio and the words heard, for Retry. A dictation cut short by a crash or a power cut is recovered the same way at the next start. |
| Arrow keys while holding | ←/→ cycle the tone (Neutral → Formal → Casual), ↑/↓ cycle the cleanup level. While the chord is held every other key is swallowed so Win+Ctrl+Left cannot switch virtual desktops mid-sentence. |
| Remembered style | A tone or level change (arrow keys or chips) is remembered for next time: if an app rule applied, that rule is updated (change it in Teams, Teams remembers); otherwise it becomes the new default. Settings › Style › "Remember tone and cleanup changes" turns this off, making changes apply to one dictation only. |
| 20-minute cap | A dictation is finalised automatically after 20 minutes (configurable 1–180). AssemblyAI bills per session second and caps sessions at 3 h. |
| Flow bar | A dark pill at the bottom centre of the target window's monitor, never focusable (WS_EX_NOACTIVATE). Three modes (Settings › General): **Full** shows a pulsing dot while recording, the state (Listening, Finishing, Cleaning up, Inserting), a level meter, the live transcript, and tone/level chips that can be clicked with the mouse, and lingers for 1.8 s after a dictation to show "Nothing heard", "Discarded", "cleanup skipped" or "Failed". **Minimal** is a 132×16 px pill containing only the microphone level, red while listening and breathing blue while the text is finished and inserted, gone as soon as the dictation ends. **Hidden** shows nothing. |
| Chord pressed again while busy | Ignored; the bar flashes. |

## Where the text goes

| Situation | Result |
|---|---|
| An editable control has focus | Text is pasted (Ctrl+V, or Ctrl+Shift+V per app rule). The characters before the caret are then read back: when they match, the previous clipboard contents (text, HTML, RTF, DIB image, file list) are restored about 250 ms later, unless another app changed the clipboard in the meantime. When the control does not show its text or it differs (a browser page with no field focused, an app without UI Automation text), the dictation stays on the clipboard as well ("also copied" on the Flow bar), so it can still be pasted by hand. |
| The window closed, cannot be brought to the front, or Windows blocks the keystrokes | Nothing is pasted; the text is copied and a toast names the app. |
| Nothing editable has focus (desktop, a button, a list) | Text is copied to the clipboard and a toast says so. |
| Target runs elevated (Run as administrator) | UIPI would silently drop the paste, so the text is copied instead and the toast says "Target is elevated, text copied". |
| Focus moved to another window during dictation | The text is pasted into the new window; the tone chosen at the start is kept. |
| Spacing before the text | The characters before the cursor are read through UI Automation (about 10 ms): a space is added only after a letter, digit or punctuation mark, and the first letter is capitalised after a sentence end. An empty field (for example a chat box after a message was sent) gets no space; invisible stand-ins such as a rich editor's empty paragraph count as empty. Where the control does not expose its text, the last insertion into that window (within 10 minutes) decides instead. |
| Sentences and pauses | AssemblyAI is asked to wait for 1 s of silence before ending a sentence at a full stop (its default is 100 ms); releasing the hotkey still ends the dictation at once. Longer pauses to think still end a turn, so the cleanup (Light and above) is told where each pause was, with the transcriber's full stop there removed, and rejoins the sentence when the words after the pause continue it. Real sentence ends and names are restored. The inserted text and History never contain the pause markers. |
| Nothing was heard | No record, no audio file, bar shows "Nothing heard". |

Editable detection order: UI Automation focused element (Edit/Document/ComboBox, writable Value pattern, Text
pattern; buttons, list items etc. count as not editable) → a caret in the foreground thread → a process
allowlist (terminals, editors, Office, browsers, chat apps) → editable by default, because a paste into a
non-editable control is harmless, and a paste that cannot be confirmed afterwards leaves the text on the clipboard as
well.

## Cleanup and tone

The raw transcript is passed once through Groq's OpenAI-compatible chat API (free tier is enough) with a system
prompt that forbids adding content, answering questions or wrapping in quotes, and describes the spoken
formatting commands. AssemblyAI is used for transcription only. Without a Groq key the raw transcript is
inserted.

| Cleanup level | What it does |
|---|---|
| None | No LLM call unless a tone is chosen. Spoken commands are applied by a regex pass. |
| Light | Removes fillers, false starts, stutters and immediate self-corrections; fixes punctuation and capitalisation. |
| Medium | Light plus grammar and agreement fixes, redundancy removal, run-on splitting. |
| High | Medium plus tighter wording, merged fragments and paragraph breaks at topic shifts; keeps every fact, name and number. |

| Tone | What it does |
|---|---|
| Neutral | Register unchanged, contractions as spoken. |
| Formal | Professional register, expanded contractions, complete sentences. |
| Casual | Relaxed, contractions allowed, short sentences. |

Spoken commands (both in the LLM prompt and in the regex fallback): "new line", "new paragraph", "bullet
point", "period"/"full stop", "comma", "question mark", "exclamation mark", "colon", "semicolon", "open/close
paren", "scratch that" (drops the preceding clause).

Lists: enumerations are written out as lists with one item per line. The LLM is told to turn "first…
second… third", "number one… number two", "one… two… three" or any clearly dictated list into a numbered
("1. ") or bulleted ("- ") list. The regex fallback (used for cleanup level None and whenever the LLM is
unavailable) handles the unambiguous forms itself: "1." / "1)" / "1:" markers and the phrases "number one",
"point one", "item one" (words or digits), provided at least two markers run 1, 2, 3… in order. Trailing
"and" / "then" before the next item is dropped. "Version 1.2" and "my number one priority" stay as prose.

Safety net: each model gets 4 s and the whole chain 8 s. The default chain is `openai/gpt-oss-120b` →
`qwen/qwen3.8-27b` → `openai/gpt-oss-20b`, all on Groq's free tier (about 1000 requests per day and 8000
tokens per minute per model; a 429 rate limit simply moves to the next model). The gpt-oss models are asked
for low reasoning effort so they answer in under a second instead of thinking through their token budget. Output is rejected if the model did not finish it (it ran out of tokens or was filtered), if it is empty, starts
with "Here is"/"Sure"/similar, or is more than 2.5× / less than 0.4× the input length; the next model is then tried.
A model the key may not use (403) or an unknown model also moves on; only a rejected key (401) stops the chain. On
any failure the raw transcript (with regex-applied commands) is inserted and the Flow bar shows **cleanup
skipped**; History keeps every attempt's reason. A settings problem that keeps recurring (rejected key, a model the
key may not use, a retired model) is shown once per run, even when a fallback model covered for it. "Undo AI edit"
in History puts the raw transcript on the clipboard. The endpoint and models are editable, so any
OpenAI-compatible service can replace Groq; Settings › API › Test keys tries every model of the chain.

## App-aware rules

Rules map a process name (wildcards allowed) or a browser tab host to a tone, cleanup level, paste mode and a
hint for the LLM. A URL rule beats a process rule, which beats the defaults; unset fields inherit.

The list starts **empty**: every app follows the Style tab's default tone and cleanup level until you add it
under Settings › App rules › Add app (a new row starts from the current defaults, so you only change what should
differ). Examples of useful rules:

| Target | Tone | Level |
|---|---|---|
| `OUTLOOK`, `olk`, host `mail.google.com` | Formal | Medium |
| `ms-teams`, `slack`, `WhatsApp`, host `web.whatsapp.com` | Casual | Light |
| `WINWORD`, host `docs.google.com` | Formal | Medium |
| `Code`, `WindowsTerminal`, `pwsh`, `cmd` | Neutral | None |

Versions 0.1 and 0.2 seeded rules like these into every settings file. Settings schema 3 removes those seeded
entries on upgrade, including ones whose tone or level "Remember tone and cleanup changes" altered on its own.
A seeded rule whose paste mode, hint or on/off switch you changed is kept, as is every rule for another app.

For Chrome, Edge, Brave, Vivaldi, Opera and Firefox the address bar is read through UI Automation (time-boxed
to 250 ms) so host rules work per tab.

## Personal dictionary

Terms (names, acronyms, drug names) are sent to AssemblyAI as `keyterms_prompt` on every session and listed in
the LLM prompt as spellings to preserve. Up to 100 terms of ≤50 characters are sent: starred first, then most
used, then most recently used. Use counts are updated automatically when a term appears in a dictation. Editing
the dictionary during a dictation pushes the new list mid-session (`UpdateConfiguration`).

**Correct last dictation** (tray menu or Settings › Dictionary) shows the last inserted text in an editor; after
you fix the misheard words, a word-level diff proposes the replacements as new starred terms. Terms added this way
while Settings is open appear there and survive its Save. Changes spread over thousands of words are refused with a
message rather than compared in one go.

## History

Every dictation is stored in SQLite with: raw transcript, cleaned text, inserted text, tone, level, process,
window title, URL, duration, audio path, cost estimate, status (Inserted / CopiedOnly / Failed / Pending),
model used and failure reason. The History window offers:

- prefix full-text search over transcript, inserted text, window title and process;
- Play/Stop of the stored WAV (16 kHz mono);
- Copy; Re-insert (minimises History and pastes into the window that gets focus, with the same checks, spacing
  and messages as a live dictation);
- Retry for Failed/Pending records with audio: re-streams the WAV at real time (AssemblyAI cannot process a faster
  stream in time, so a retry takes about as long as the recording), cleans it up, copies the result. A retry that
  does not finish keeps the earlier words and can be tried again;
- Undo AI edit; Delete;
- a footer with count, records with audio and total estimated cost.

Retention: text for 24 h / 14 days / forever, audio separately for 24 h / 14 days / forever. A background
service purges at startup and hourly and also sweeps WAVs no record refers to (after a day). A recording left
unfinished by a crash, a kill or a power cut is recovered into History as Failed at the next start, for Retry.
"Store audio" can be switched off entirely. "Delete all history" deletes every record, every recording in the audio
folder (including ones no record refers to) and the logs; a recording still in progress is kept and counted.

## Settings

Tabs: **General** (Flow bar mode, autostart, update check, GitHub token for a private fork, dictation cap,
language codes), **API** (DPAPI-encrypted AssemblyAI and Groq keys, "Test keys" reports AssemblyAI and every model of
the cleanup chain separately, speech model, cleanup endpoint, model and fallbacks), **Hotkey** (chord recorder,
which only watches the chord while it has focus; accept injected keys), **Audio** (device picker with live meter,
keep the microphone ready; a chosen microphone that is not connected falls back to the default one), **Style**
(default tone and level, remember changes), **Dictionary**, **App rules**, **History & privacy**. Save writes only
what was changed in the window, so terms, remembered styles and use counts changed meanwhile are kept.

## First run, autostart, updates

- A three-step wizard (AssemblyAI and Groq keys with test, microphone meter, hotkey test with a Ctrl+Alt
  alternative) runs on first start or when no AssemblyAI key is configured. The hotkey test only watches the chord:
  nothing is recorded, uploaded, pasted or stored.
- Autostart is on by default. An installed build re-registers the per-user Run key (launching
  `DictationApp.exe --minimized`) every time it starts, so after installing once the app is simply there in the
  tray after every sign-in; no terminal, no shortcut. Development builds only register when the Settings
  checkbox is saved.
- Updates (installed builds only): the app checks GitHub Releases 45 s after start and then daily, and the
  tray menu's "Check for updates…" checks on demand. A newer release is downloaded straight away (a toast says
  so) and a "Restart to update to x.y.z" entry appears in the tray menu. Choose it to apply immediately, or do
  nothing: the update is applied when the app next exits, so the next launch, including the autostart at
  sign-in, is the new version. Only the program folder is replaced; settings, history, audio and the encrypted
  keys live in `%LOCALAPPDATA%\ThomasWCode\DictationApp` and survive every update and reinstall. The repository is
  public, so the updater needs no token; the optional GitHub token (Settings › General) is only for a private fork.

## Privacy and cost

Audio goes to AssemblyAI for transcription and the transcript to Groq for cleanup; nothing else leaves the
machine. Both keys (and the optional GitHub token) are DPAPI-protected for the current user; a key that cannot be
decrypted (a data folder copied from another PC or user) is shown as missing. History shows an estimate per
dictation from the time the streaming session was open, which is what AssemblyAI bills ($0.45/h for Universal-3.5
Pro or $0.15/h for the standard model; Groq's free tier costs nothing). The AssemblyAI dashboard is authoritative.
No connection is kept warm while idle because sessions are billed per second.

## Diagnostics

- Logs: `%LOCALAPPDATA%\ThomasWCode\DictationApp\logs\dictation-YYYYMMDD.log` (14 days). No dictated text; one
  timing line per dictation (ms since the press for the microphone, window lookup, Begin, release, handshake,
  cleanup and insertion).
- `DictationApp.exe --stream-test file.wav` streams a WAV through a real session and prints every turn.
- `DictationApp.exe --simulate file.wav --delay 5` runs a full dictation with the WAV in place of the microphone.
- `DictationApp.exe --check-updates [--github-token T] [--apply-update]` runs the updater headlessly and
  prints the outcome (exit 0 up to date, 10 update downloaded, 1 error).
- Toasts link to `ms-settings:privacy-microphone` when the microphone is blocked or silent.
