# DictationApp: design and implementation plan

Windows dictation app in the style of Wispr Flow, built on AssemblyAI. This document is the agreed plan before any code is written; it will be revised as phases land.

## Context

- Goal: a Windows tray app that behaves like Wispr Flow. Hold **Ctrl+Win**, speak, release, and cleaned-up text lands in whatever text box has focus. If nothing editable has focus, the text goes to the clipboard with a toast.
- Speech-to-text and LLM cleanup both run through one **AssemblyAI API key**: the streaming WebSocket for transcription and the AssemblyAI LLM Gateway for formality and cleanup rewrites. No second vendor account is needed.
- Repo `ThomasWCode/DictationApp` is empty (one blank `.gitignore`), so this is greenfield.
- Decisions taken with the user:
  - Stack: **C# / .NET 8 + WPF**, Windows 10/11 x64. Wispr Flow itself is Electron and is widely criticised for ~800 MB idle RAM; native is deliberate.
  - STT model: **Universal-3.5 Pro** (`speech_model=universal-3-5-pro`, ~$0.45/hr) over base Universal-Streaming (~$0.15/hr) because it supports `keyterms_prompt` and `prompt` (personal dictionary) and always returns formatted turns. Switchable in settings.
  - History: **text + audio** (WAV per dictation) with playback and retention settings.
  - v1 extras: **personal dictionary** and **app-aware tone**. Snippets and command mode (rewrite a selection) are deferred to v2.

## API facts the implementation depends on (verified 2026-09-22)

| Item | Value |
|---|---|
| Streaming endpoint | `wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&speech_model=universal-3-5-pro&encoding=pcm_s16le`, header `Authorization: <key>` |
| Audio in | PCM16 LE, mono, 16 kHz, binary frames of 50–1000 ms (plan: 100 ms = 3200 bytes) |
| Optional query params | `prompt` (≤1750 chars), `keyterms_prompt` (JSON array, ≤100 terms, ≤50 chars each, no extra cost on Pro), `language_codes`, `min_turn_silence`, `max_turn_silence` (Pro default 1536 ms), `vad_threshold` (Pro default 0.2), `inactivity_timeout` (5–3600 s) |
| Client messages | binary audio; `{"type":"UpdateConfiguration","keyterms_prompt":[...],"prompt":"..."}` (mid-session, no reconnect); `{"type":"ForceEndpoint"}`; `{"type":"Terminate"}`; `{"type":"KeepAlive"}` |
| Server messages | `Begin{id,expires_at}`, `SpeechStarted`, `Turn{turn_order,end_of_turn,transcript,utterance,words[],end_of_turn_confidence,language_code}`, `Termination{audio_duration_seconds,session_duration_seconds}` |
| Turn semantics | Immutable per turn; `end_of_turn=true` carries the complete `utterance`. Pro ends a turn on terminal punctuation after `min_turn_silence`, or force-ends at `max_turn_silence`. Long dictations arrive as many turns; concatenate by `turn_order`. |
| Shutdown | `Termination` is a summary, not a guaranteed flush. Handshake: stream 300 ms of silence → `ForceEndpoint` → wait for `end_of_turn` Turn (≤1.5 s) → `Terminate` → wait for `Termination` (≤1 s). Hard cap 2.5 s total. |
| Session cap / billing | 3 h server cap; billed per session second, so no idle warm connection by default. App enforces a 20-minute per-dictation cap (Wispr Flow parity). |
| LLM Gateway | `POST https://llm-gateway.assemblyai.com/v1/chat/completions`, header `Authorization: <key>`, body `{model, messages[], max_tokens, temperature}`, OpenAI-compatible; `GET /v1/models` (unauthenticated) lists IDs. 0% markup on provider prices. |
| LLM models | Default `gemini-2.5-flash-lite` (~$0.10/M input). Fallback chain `gemini-2.5-flash` → `claude-haiku-4-5-20251001`. Free-text override in settings. Re-verify IDs against `/v1/models` during Phase 4. |

## Architecture

### Solution layout

```
DictationApp.sln
Directory.Build.props              # net8.0, nullable, LangVersion latest, TreatWarningsAsErrors
Directory.Packages.props           # central package versions
src/DictationApp.Core/             # net8.0 class library, zero Windows deps, fully unit-testable on Linux CI
  Transcription/   IStreamingTranscriber, AssemblyAiStreamingTranscriber, TurnMessage DTOs, TranscriptAssembler, SessionOptions
  Cleanup/         ITextPostProcessor, PostProcessorRouter, PassthroughPostProcessor, LlmGatewayPostProcessor,
                   PromptBuilder, SpokenCommandNormaliser, CleanupLevel, Tone
  Insertion/       InsertionTextFormatter (spacing/capitalisation heuristics, pure)
  Rules/           AppRule, AppRulesResolver, DefaultAppRules
  Dictionary/      DictionaryTerm, KeytermsSelector, CorrectionDiffer
  History/         IHistoryRepository, SqliteHistoryRepository (Microsoft.Data.Sqlite + FTS5), DictationRecord, RetentionPolicy
  Settings/        AppSettings, ISettingsStore, JsonSettingsStore, HotkeyChord, ISecretStore
  Session/         DictationStateMachine (Stateless), DictationOrchestrator, DictationSession
  Abstractions/    IAudioCapture, IAudioSink, IHotkeyService, IForegroundContextProvider, ITextInserter, IClipboard, INotifier, IClock
src/DictationApp.Windows/          # net8.0-windows class library: all Win32/UIA/audio
  Hotkey/          LowLevelKeyboardHook (WH_KEYBOARD_LL via CsWin32), HotkeyService, WinKeySuppressor
  Audio/           WasapiAudioCapture, AudioDeviceEnumerator, WavFileSink, WavPlayer
  Focus/           ForegroundContextProvider (process, title, browser URL via UIA), FocusedEditableDetector, ElevationProbe
  Insertion/       ClipboardService (STA thread, retry), ClipboardPasteInserter
  Security/        DpapiSecretStore
  Startup/         RunKeyAutostart
  Native/          CsWin32 NativeMethods.txt
src/DictationApp/                  # net8.0-windows WPF exe, tray-resident, Generic Host DI, CommunityToolkit.Mvvm
  Program.cs (Velopack bootstrap, single-instance mutex), App.xaml(.cs)
  Tray/            TrayIcon (H.NotifyIcon), TrayViewModel
  Overlay/         FlowBarWindow + VM  (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, bottom-centre, topmost)
  Settings/        SettingsWindow + VM (tabs: General, API, Hotkey, Audio, Style, Dictionary, App rules, History & privacy)
  History/         HistoryWindow + VM
  Services/        WpfNotifier (toasts), UpdateCheckService, HistoryRetentionService (hosted)
tests/DictationApp.Core.Tests/     # xUnit, runs on windows + ubuntu CI
tests/DictationApp.Windows.Tests/  # xUnit, [Trait("Category","Interactive")] for anything needing a desktop
build/                             # pack.ps1 (dotnet publish + vpk pack), app icon
docs/manual-test-checklist.md
PROGRESS.md                        # working progress file; deleted before the final commit
```

### Key services

| Service | Responsibility |
|---|---|
| `LowLevelKeyboardHook` | `SetWindowsHookEx(WH_KEYBOARD_LL)` on a dedicated thread with a message pump. Callback does no work: posts `(vk, up/down, LLKHF_INJECTED)` to a channel and returns within the 300 ms `LowLevelHooksTimeout`. Tracks only modifier and chord keys, never buffers characters. Decision: hand-rolled hook via CsWin32, not SharpHook, so per-event suppression and injected-flag detection are fully under our control. |
| `HotkeyService` / `WinKeySuppressor` | Tracks the configured `HotkeyChord` (default Ctrl+Win; ≤3 keys; modes Hold and DoubleTapToggle). When a chord containing Win actually fired, injects VK `0xE8` via `SendInput` before the Win key-up so the Start menu does not open. If any other key is pressed while Win is held, the Win press is marked "consumed by shortcut" and left alone, so Win+E etc. keep working. |
| `WasapiAudioCapture` | NAudio `WasapiCapture` shared mode on the selected device → `WdlResamplingSampleProvider` → 16 kHz mono PCM16 → 100 ms frames; forks frames to `WavFileSink` when audio storage is on. |
| `AssemblyAiStreamingTranscriber` | `ClientWebSocket` wrapper: builds query string, sends frames, parses `Begin`/`Turn`/`Termination` with `System.Text.Json` source-gen, performs the shutdown handshake above, reconnects once on transient failure during Arming. |
| `TranscriptAssembler` | Pure. `SortedDictionary<turn_order, Turn>`; last-wins per turn_order with formatted preferred; `LiveText` (finals + current partial) for the overlay; `FinalText` (joined `utterance` of `end_of_turn` turns, plus a trailing non-final partial if the server never closed it). |
| `SpokenCommandNormaliser` | Pure regex pass: "new line", "new paragraph", "bullet point", "period", "comma", "question mark", "scratch that". Runs alone when cleanup is None; included in the LLM instructions otherwise. |
| `PromptBuilder` / `LlmGatewayPostProcessor` / `PostProcessorRouter` | Router sends None+Neutral to passthrough (no network call). Otherwise one chat call, temperature 0.1, `max_tokens = clamp(2×input tokens + 100, 200, 4000)`, 4 s per attempt through the fallback chain, 8 s total. Output validation strips quotes/code fences and rejects (falls back to raw) if empty, >2.5× or <0.4× input length, or starts with "Here is"/"Sure". |
| `ForegroundContextProvider` | Captured on chord-down before anything else moves: `GetForegroundWindow` → process name, window title; for `chrome`/`msedge`/`firefox` reads the UIA address-bar value for URL rules. |
| `FocusedEditableDetector` | Order: FlaUI UIA3 `FocusedElement` ControlType ∈ {Edit, Document, ComboBox} or `ValuePattern.IsReadOnly=false` or supports `TextPattern` → `GetGUIThreadInfo().hwndCaret != 0` → user-editable process allowlist (`WindowsTerminal`, `Code`, `Teams`, `slack`, `chrome`, `msedge`) → unknown is treated as editable (paste into a non-editable control is a harmless no-op and the text stays on the clipboard). |
| `ElevationProbe` | Detects an elevated target (`OpenProcess` + token check). Then paste is skipped, text stays on the clipboard, toast says "Target is elevated, text copied". |
| `ClipboardPasteInserter` | Snapshot clipboard (text, HTML, RTF, DIB only, the formats we can round-trip) → set `CF_UNICODETEXT` only → release held modifiers → `SendInput` Ctrl+V (per-app rule may choose Ctrl+Shift+V) → wait 250 ms and until `GetClipboardSequenceNumber` is stable → restore snapshot unless someone else changed the clipboard meanwhile. Never types character by character. |
| `InsertionTextFormatter` | Pure. Prepends a space when the previous insertion in this window did not end with whitespace and the new text starts alphanumeric; capitalises after sentence end. Tracked per session, not read from the target. |
| `AppRulesResolver` | Pure. Ordered rules `(process glob | URL host) → (Tone, CleanupLevel, pasteMode)`; URL rule beats process rule beats default. Seed: Outlook/Gmail → Formal+Medium; Teams/Slack/WhatsApp → Casual+Light; Word/Docs → Formal+Medium; VS Code/terminals → Neutral+None. |
| `KeytermsSelector` / `CorrectionDiffer` | Pure. Selector: starred first, then use count, then recency, cap 100 terms / 50 chars. Differ: word-level LCS between inserted and user-edited text → candidate terms for "Correct last dictation". |
| `SqliteHistoryRepository` | WAL mode, FTS5 search, versioned SQL migrations. Record: id, timestamps, rawTranscript, cleanedText, tone, level, processName, windowTitle, url, durationMs, audioPath, costEstimate, status ∈ {Inserted, CopiedOnly, Failed, Pending}. |
| `DictationOrchestrator` | Hosted service owning the state machine below; one `DictationSession` at a time; publishes state for the Flow bar. |
| `FlowBarWindow` | Never takes keyboard focus. Shows state, level meter, live text, tone/level chips (mouse, or arrow keys while the chord is held). Clicking it restores foreground to the captured hwnd before paste. |
| `DpapiSecretStore` | `ProtectedData` CurrentUser scope for the API key inside `%LOCALAPPDATA%\DictationApp\settings.json`. |

### Dictation state machine

| From | Trigger | To | Actions |
|---|---|---|---|
| Idle | ChordDown | Arming | Capture foreground context + focus + app rule; start mic capture into a local buffer; start WAV sink; open WebSocket; show Flow bar "Listening". |
| Arming | ChordUp < 300 ms | Idle | Cancel: stop mic, delete WAV, Terminate if connected. Counts as a tap for double-tap detection. |
| Arming | `Begin` received | Recording | Flush buffered frames, then stream live. Nothing is lost to connect latency. |
| Arming | ChordUp ≥ 300 ms, not yet connected | Finalising | Wait for connect up to 3 s, flush, run shutdown handshake. |
| Arming / Recording | Connect timeout or socket fault | Failed → Idle | Keep WAV; history `status=Failed`; toast "Network error, saved to history". |
| Recording | AudioFrame | Recording | Send frame, append WAV, update meter. |
| Recording | `Turn` | Recording | Assembler ingest; live text on bar. |
| Recording | ChordUp, Esc, or 20-min cap | Finalising | Stop mic; send tail; shutdown handshake. Esc discards instead. |
| Finalising | Handshake complete or 2.5 s cap | PostProcessing | `FinalText` from assembler. Empty → Idle with "Nothing heard". |
| PostProcessing | Router: None+Neutral | Inserting | `SpokenCommandNormaliser` only. |
| PostProcessing | LLM ok, failed, or 8 s timeout | Inserting | On failure insert raw text and badge "cleanup skipped". |
| Inserting | Editable and not elevated | Idle | Paste; `status=Inserted`. |
| Inserting | Not editable or elevated | Idle | Clipboard only; toast; `status=CopiedOnly`. |
| Any non-Idle | ChordDown | same | Ignore with a bar flash (v1). |

- Foreground context is re-verified at Inserting; if the hwnd changed, paste into the new one but keep the tone chosen at start.
- Connect latency is logged in Phase 1. Warm connections are off by default (billed per session second). A "Keep connection warm" setting is only added if measured p95 connect-to-`Begin` exceeds 400 ms.
- Failed/Pending records get "Retry" in History, which re-streams the WAV through the same transcriber at 4× real time.

### LLM prompt design

System prompt (built by `PromptBuilder`):

```
You are a dictation clean-up engine. You receive a raw speech-to-text transcript of what the user
just spoke and return ONLY the text to insert into their document. Rules, in priority order:
1. Never add information, opinions, greetings, sign-offs, or commentary. Never answer questions in
   the transcript. Never wrap the output in quotes or code fences.
2. Preserve the speaker's meaning, first-person voice, and language (do not translate).
3. Apply spoken formatting commands literally: "new line" -> line break; "new paragraph" -> blank
   line; "bullet point" -> "- " item; "period", "comma", "question mark" -> punctuation;
   "scratch that" -> drop the preceding clause. Keep existing line breaks and lists.
4. Preserve the exact spelling and capitalisation of these terms if present: {keyterms}
5. Cleanup level: {level_instructions}
6. Tone: {tone_instructions}
7. Context: the text is being typed into {app_name}{url_hint}. {app_hint}
Output length must stay within ±20% of the input word count except where disfluencies are removed.
```

| Cleanup level | Instruction |
|---|---|
| None | No LLM call unless tone ≠ Neutral; then "Do not change wording; apply only the tone rules and formatting commands." |
| Light | Remove fillers (um, uh, like, you know), false starts, stutters, immediate self-corrections ("Monday, no, Tuesday" → "Tuesday"). Fix punctuation and capitalisation. Do not rephrase or reorder. |
| Medium | Light + fix grammar and agreement errors, remove redundant repetition, split run-on sentences. Keep the user's words and order where possible. |
| High | Medium + tighten wording, merge fragments, paragraph breaks at topic shifts. Keep every fact, name, number and instruction. Do not shorten by more than 30%. |

| Tone | Instruction |
|---|---|
| Neutral | Do not alter register; keep contractions as spoken. |
| Formal | Professional register: expand contractions, no slang, complete sentences, polite phrasing. No salutations or sign-offs unless spoken. |
| Casual | Relaxed conversational register: contractions allowed, short sentences, keep colloquial phrasing. No emoji unless spoken. |

One short few-shot pair per level is embedded to stop small models adding "helpful" content. The user message is the raw transcript verbatim.

### NuGet packages

| Package | Project | Purpose |
|---|---|---|
| `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http` | App, Core | Generic host, DI, options, `IHttpClientFactory` |
| `Serilog.Extensions.Hosting`, `Serilog.Sinks.File` | App | Rolling logs in `%LOCALAPPDATA%\DictationApp\logs` |
| `CommunityToolkit.Mvvm` | App | ObservableObject, RelayCommand source-gen |
| `H.NotifyIcon.Wpf` | App | Tray icon, menu, toasts (Hardcodet is stale) |
| `Stateless` | Core | Declarative state machine with guards |
| `Microsoft.Data.Sqlite` | Core | History DB (bundles e_sqlite3 with FTS5) |
| `NAudio` | Windows | WASAPI capture, resampling, WAV write, playback |
| `FlaUI.UIA3` | Windows | UI Automation focused element, patterns, browser URL |
| `Microsoft.Windows.CsWin32` | Windows | Source-generated P/Invoke (`SetWindowsHookEx`, `SendInput`, `GetGUIThreadInfo`, `GetForegroundWindow`, `GetClipboardSequenceNumber`, `OpenProcessToken`) |
| `System.Security.Cryptography.ProtectedData` | Windows | DPAPI |
| `Velopack` | App | Installer, delta updates, `UpdateManager` |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `NSubstitute`, `Microsoft.Extensions.TimeProvider.Testing` | Tests | Tests, mocks, fake clock for timeout paths |

Not used: `System.Windows.Automation` (slower, older than FlaUI UIA3), `SharpHook` (less control over suppression), `Squirrel.Windows` (unmaintained), `FluentAssertions` (licence change in v8; plain xUnit asserts).

Developer machine setup (Windows):

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Microsoft.VisualStudio.2022.Community   # optional; VS Code + C# Dev Kit also works
dotnet tool install -g vpk                              # Velopack CLI, needed only for Phase 6 packaging
git clone https://github.com/ThomasWCode/DictationApp && cd DictationApp
dotnet restore && dotnet build
```

## Implementation phases

Each phase ends in a runnable build and a commit on `claude/kind-ride-s3akei`. `PROGRESS.md` is updated at every phase boundary and deleted at the end. Note: this cloud session is Linux, so Core builds and tests run here; the WPF and Windows projects compile here with `EnableWindowsTargeting=true` but can only be run and manually tested on the user's Windows machine.

| # | Milestone | New files | Acceptance |
|---|---|---|---|
| 0 | Scaffold + CI | `.sln`, `Directory.*.props`, 3 `src` + 2 `tests` csproj, `.github/workflows/ci.yml` (windows-latest + ubuntu for Core: build, test, publish artifact), `README.md`, `.editorconfig`, .NET `.gitignore`, `PROGRESS.md` | Build and tests green on CI; tray icon with Quit; no main window; Serilog log written; Settings API tab saves DPAPI-encrypted key and "Test key" hits `GET /v1/models`. |
| 1 | Hotkey + mic + streaming to overlay | `Hotkey/*`, `Audio/*`, `Transcription/*`, `TranscriptAssembler`, `DictationStateMachine`, `DictationOrchestrator` (no insertion yet), `FlowBarWindow`, mic picker, `--stream-test file.wav` console switch | Hold Ctrl+Win in any app → live text on bar; release → final text in bar and log; Start menu stays closed; Win+E works when not dictating; tap <300 ms cancels; connect latency logged; assembler and state-machine tests pass (ordering, duplicates, timeouts via fake clock). |
| 2 | Insertion engine | `ForegroundContextProvider`, `FocusedEditableDetector`, `ElevationProbe`, `ClipboardService`, `ClipboardPasteInserter`, `InsertionTextFormatter` + tests, `WpfNotifier` | Text lands in Notepad, Word, Outlook, Chrome/Edge textarea, Teams, VS Code, Windows Terminal; desktop/no focus → clipboard + toast; elevated Notepad → clipboard + toast; pre-existing clipboard text and image restored; spacing tests pass. |
| 3 | History + audio | `SqliteHistoryRepository` + migrations + tests, `WavFileSink`, `WavPlayer`, `HistoryWindow` (search, play, copy, re-insert, delete, retry), `HistoryRetentionService` (startup + hourly), Retry re-stream path | Every dictation searchable with playable WAV; retention (24 h / 14 d / forever) and separate audio retention purge correctly; "never store audio" leaves no file; failed dictation retried from history. |
| 4 | LLM cleanup + tone | `PromptBuilder` + tests, `SpokenCommandNormaliser` + tests, `LlmGatewayPostProcessor` (+ fake handler tests for fallback chain and validation), `PostProcessorRouter`, Flow bar chips + arrow-key override while holding, "Undo AI edit" in History | None+Neutral makes no gateway call (log); each level/tone gives expected transformation on a 30-utterance fixture set with no added content; gateway timeout inserts raw text with badge. |
| 5 | Dictionary + app rules | `DictionaryTerm`, `KeytermsSelector` + tests, `CorrectionDiffer` + tests, Dictionary settings tab, "Correct last dictation" dialog, `AppRulesResolver` + tests, App-rules tab with seed rules, browser URL detection | Terms like "LSHTM", "isoniazid", "rifapentine" transcribe correctly once added; >100 terms truncated by starred/usage; `UpdateConfiguration` sent when edited mid-session; Outlook defaults Formal, Teams Casual, Gmail tab Formal; override sticks for that dictation only; correction dialog proposes and adds changed words. |
| 6 | Polish + packaging | Full Settings (chord recorder, double-tap toggle, autostart, paste mode), first-run wizard (key, mic test, hotkey test), `RunKeyAutostart`, `UpdateCheckService`, `build/pack.ps1`, release workflow uploading Velopack artifacts, mic-permission toast with `ms-settings:privacy-microphone` link, cost estimate in History footer, `docs/manual-test-checklist.md` | Fresh Windows VM: Setup.exe installs to `%LOCALAPPDATA%`, autostart honours setting, updates from a v+1 release, uninstall removes Run key; custom chord and double-tap toggle work; full manual matrix passes. |

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Win-key suppression breaks Win+X shortcuts or Start | Inject VK 0xE8 only when the chord fired; a Win press with any other key is left untouched; Win alone still opens Start. First-run offers Ctrl+Alt as an alternative chord. |
| Hook callback too slow → Windows silently unhooks | Zero work in callback; channel hand-off; watchdog re-installs the hook if events stop arriving while keys are pressed. |
| UIPI blocks paste into elevated windows | `ElevationProbe` → clipboard-only + toast. "Run as administrator" documented as opt-in workaround. |
| Chromium/Electron/terminal UIA gaps | UIA → caret → allowlist → default editable. Allowlist user-editable. |
| Clipboard restore race | 250 ms + sequence-number check; skip restore if a third party changed the clipboard; restore only round-trippable formats. |
| Tail of speech lost on release | 300 ms silence tail → `ForceEndpoint` → wait `end_of_turn` → `Terminate`; trailing partial included if never closed; cases logged. |
| Rich-paste oddities in Word/Outlook | `CF_UNICODETEXT` only; per-app rule can select Ctrl+Shift+V. |
| Flow bar steals focus | `WS_EX_NOACTIVATE`, `ShowActivated=false`; on click, `SetForegroundWindow` back to captured hwnd before paste. |
| API cost | No warm connection; short taps cancel before billing; 20-min cap; cheapest gateway model default; per-dictation cost shown in History. |
| Windows 11 microphone privacy toggle blocks capture silently | `E_ACCESSDENIED` or 2 s of zero-amplitude frames → toast with `ms-settings:privacy-microphone` deep link. |
| Antivirus/SmartScreen on hook + `SendInput` + unsigned exe | Hook tracks only modifier/chord keys; sign with Azure Trusted Signing or an OV cert if available, else README documents SmartScreen "More info → Run anyway". |
| LLM adds content or truncates | Output validation (length ratio, banned prefixes, fence stripping) with raw-text fallback; few-shot examples; "Undo AI edit" in History. |

## Verification

```powershell
dotnet test tests/DictationApp.Core.Tests                               # pure logic; runs on Linux and Windows CI
dotnet test tests/DictationApp.Windows.Tests --filter Category!=Interactive
dotnet run --project src/DictationApp                                    # manual run on Windows
dotnet run --project src/DictationApp -- --stream-test sample.wav        # streams a WAV, prints turns; ASSEMBLYAI_API_KEY env var
```

- Core unit tests: `TranscriptAssembler` (out-of-order turns, duplicate formatted/unformatted, trailing partial); `DictationStateMachine` (every transition including short press, connect timeout, finalise cap, using `FakeTimeProvider`); `PromptBuilder` (level/tone/keyterms/app text present; None+Neutral routes to passthrough); `SpokenCommandNormaliser`; `InsertionTextFormatter`; `AppRulesResolver` precedence; `KeytermsSelector` cap and ordering; `CorrectionDiffer`; `LlmGatewayPostProcessor` fallback chain and output validation with a fake `HttpMessageHandler`; `SqliteHistoryRepository` CRUD, FTS search, retention on a temp DB.
- Windows tests: `DpapiSecretStore` round-trip; `WavFileSink` byte length; `FocusedEditableDetector` and `ClipboardPasteInserter` against a WPF TextBox test window (Interactive category).
- Manual matrix (`docs/manual-test-checklist.md`), each row checked for hold/release, live text, insertion, clipboard restored, Start menu closed:

| Target | Notes |
|---|---|
| Notepad (Win11) | UIA Edit/Document |
| Word, Outlook desktop (new + classic) | Rich paste, Formal default |
| Chrome/Edge: Gmail compose, textarea, address bar | URL rule, omnibox detection |
| Teams, Slack, WhatsApp desktop | Electron, Casual default |
| VS Code, Windows Terminal, PowerShell console | Caret fallback / allowlist |
| Elevated Notepad | Clipboard-only toast |
| Desktop / Explorer with no edit focus | Toast |
| Hotkey edge cases | tap <300 ms; Win+E while running; Win alone opens Start; custom chord; double-tap toggle; 5-min dictation |
| Network | Wi-Fi off mid-dictation → Failed record → Retry works |
| Privacy | Mic blocked in Settings → guided toast; "never store audio" leaves no WAV |
| Install/update | Fresh install, autostart, v+1 update, uninstall removes Run key |
