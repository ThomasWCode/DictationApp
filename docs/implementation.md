# Implementation

How the app is built, in the order it was built. The [plan](PLAN.md) defined six phases; the code landed as
eight commits grouped by layer, each phase's acceptance criteria being checked against unit tests, the
`--stream-test` / `--simulate` switches, and the [manual checklist](manual-test-checklist.md).

## Solution layout

```
DictationApp.sln
Directory.Build.props        nullable, LangVersion latest, TreatWarningsAsErrors, EnableWindowsTargeting
Directory.Packages.props     central package versions
src/DictationApp.Core        net8.0 class library, no Windows dependencies
  Abstractions/   IAudioCapture(+Factory), IAudioSink(+Factory), IHotkeyService, IForegroundContextProvider,
                  ITextInserter, IClipboard, INotifier, ISecretStore, IApiKeyProvider
  Transcription/  SessionOptions, Messages (+parser), IStreamingTranscriber, AssemblyAiStreamingTranscriber,
                  TranscriptAssembler
  Cleanup/        CleanupLevel/Tone, SpokenCommandNormaliser, PromptBuilder, OutputValidator,
                  ITextPostProcessor + Passthrough + LlmGatewayPostProcessor + PostProcessorRouter
  Insertion/      InsertionTextFormatter
  Rules/          AppRule, AppRulesResolver, DefaultAppRules
  Dictionary/     DictionaryTerm, KeytermsSelector, CorrectionDiffer
  History/        DictationRecord, RecordStatus, RetentionPolicy, IHistoryRepository, SqliteHistoryRepository,
                  CostEstimator
  Settings/       AppSettings, ISettingsStore, JsonSettingsStore, HotkeyChord, SettingsApiKeyProvider, AppPaths
  Session/        DictationStateMachine, DictationStatusHub, DictationOrchestrator
src/DictationApp.Windows     net8.0-windows class library
  Native/         NativeMethods (P/Invoke), WindowHelper
  Hotkey/         LowLevelKeyboardHook, HotkeyService, WinKeySuppressor
  Audio/          Pcm16Pipeline, WasapiAudioCapture, WavReplayAudioCapture, WavFileSink, AudioDevices (enumerator,
                  WavPlayer, capture factory)
  Focus/          FocusedEditableDetector, ElevationProbe, ForegroundContextProvider
  Insertion/      ClipboardService, ClipboardPasteInserter
  Security/       DpapiSecretStore
  Startup/        RunKeyAutostart
src/DictationApp             net8.0-windows WPF WinExe
  Program.cs, App.xaml(.cs), HostFactory, Shell, Cli/CommandLine
  Tray/TrayIcon, Overlay/FlowBar*, Settings/Settings*, History/History*, FirstRun/FirstRun*,
  Dictionary/Correction*, Services/{WpfNotifier, HistoryRetentionService, UpdateCheckService}, Converters
tests/DictationApp.Core.Tests, tests/DictationApp.Windows.Tests
build/make_icon.py, build/app.ico, build/pack.ps1
.github/workflows/ci.yml, release.yml
```

## Step 1: scaffold

- `dotnet new sln` plus hand-written csproj files. Central Package Management pins versions in one place;
  `TreatWarningsAsErrors` keeps the codebase warning-free (WPF's generated code and the MVVM source generators
  are clean under it).
- The icon is produced by `build/make_icon.py`: it rasterises a microphone glyph with supersampling and writes an
  ICO container of PNG images (16–256 px) using only `zlib` and `struct`, so the repo needs no image tool.
- CI: windows-latest builds, tests both projects and publishes a framework-dependent win-x64 build as an
  artifact; ubuntu-latest builds and tests Core to prove it has no Windows dependency.

## Step 2: Core foundations

- **Abstractions** are deliberately tiny. Everything the orchestrator needs from Windows is an interface with
  one job, which is what makes the end-to-end orchestrator tests possible with in-memory fakes.
- **Settings** are a POCO serialised with `System.Text.Json` (enums as strings, indented). Writes go to a temp
  file then `File.Move(overwrite)`, so a crash mid-write cannot corrupt the file; an unreadable file is renamed
  `settings.json.corrupt-<timestamp>` and defaults are used.
- **HotkeyChord** stores virtual-key codes, normalises left/right modifiers, sorts modifiers first and renders
  names like `Ctrl+Win`. Up to three keys.
- **AppRulesResolver** is pure: it takes the foreground context and the rule list and returns the effective
  tone/level/paste mode plus which rule matched (for the log).
- **KeytermsSelector** and **CorrectionDiffer** are pure functions; the differ is a classic word-level LCS whose
  replaced runs become candidate terms.
- **SqliteHistoryRepository** uses `Microsoft.Data.Sqlite` (bundled e_sqlite3 has FTS5). Schema v1 creates the
  `dictations` table, an FTS5 external-content table over five columns, and insert/update/delete triggers that
  keep the index in sync. Migrations are SQL strings applied in a transaction with the version recorded in
  `schema_version`. Search turns free text into quoted prefix terms (`"isoni"*`), so user input can never break
  the FTS query syntax.
- **DpapiSecretStore** (Windows project) wraps `ProtectedData` in CurrentUser scope with app-specific entropy.

## Step 3: streaming transcriber

- `SessionOptions.BuildQueryString()` emits `sample_rate`, `speech_model`, `encoding=pcm_s16le`, `format_turns`,
  `keyterms_prompt` (JSON array), optional `prompt` (Pro only, ≤1750 chars), `language_codes` and the VAD/turn
  tuning parameters.
- `AssemblyAiStreamingTranscriber` wraps `ClientWebSocket`: `Authorization: <key>` header, a receive loop that
  reassembles fragmented text frames and dispatches `Begin`/`Turn`/`Termination`/error messages, and a
  `SemaphoreSlim` that serialises sends (ClientWebSocket allows one in-flight send).
- Shutdown handshake, exactly as planned: 300 ms of silence → `ForceEndpoint` → wait for an `end_of_turn` Turn
  (1.5 s if a turn is open, 400 ms otherwise) → `Terminate` → wait ≤1 s for `Termination`, all under a 2.5 s hard
  cap, then `CloseOutputAsync`.
- `TranscriptAssembler` keeps a `SortedDictionary<turn_order, Turn>`; last delivery wins except that a closed turn
  is never replaced by a partial and a formatted final is never replaced by an unformatted one. `LiveText` and
  `FinalText` both include an open trailing partial (the plan's "trailing partial if the server never closed it").
- Verified live with `--stream-test`: a 10 s synthesised WAV produced four turns with growing `utterance`
  partials and formatted finals, connect-to-Begin of ~960 ms, handshake 890 ms, and the exact expected text.

## Step 4: cleanup

- `PromptBuilder` is a string template of the plan's prompt with the level and tone instructions and one
  few-shot pair per level.
- `LlmGatewayPostProcessor` posts to `https://llm-gateway.assemblyai.com/v1/chat/completions` through
  `IHttpClientFactory`. Budgets are enforced with linked `CancellationTokenSource`s cancelled by
  `TimeProvider.CreateTimer`, so the tests drive both the per-attempt and total timeouts with a
  `FakeTimeProvider`. A 401/403 aborts the chain (a bad key will not get better with another model); 4xx/5xx,
  timeouts and invalid output move to the next model.
- `OutputValidator.Strip` removes code fences and surrounding quotes; `Validate` applies the banned-prefix and
  length-ratio rules (skipped for inputs under 20 characters where ratios are meaningless).
- `PostProcessorRouter` sends None+Neutral to `PassthroughPostProcessor` (regex normaliser only, no network).

## Step 5: state machine and orchestrator

- `DictationStateMachine` uses Stateless with explicit `Permit`/`Ignore` for every trigger in every state, so an
  unexpected event can never throw. `ChordUp` in Arming is a `PermitDynamic` that compares the hold duration
  with the 300 ms threshold using `TimeProvider`.
- `DictationOrchestrator` (a `BackgroundService`) owns one session at a time:
  1. **ChordDown** → capture foreground context (window, process, title, editable, elevated, URL) and resolve
     the app rule; start microphone capture and the WAV sink; frames go into an unbounded channel; open the
     socket with a 3 s timeout.
  2. Wait for the first of: Begin, release, Escape, fault. Release before Begin: a short tap cancels
     (abort socket, discard WAV); a real hold waits for the connection then flushes the buffered audio and
     finalises.
  3. **Recording**: a sender task drains the channel into the socket; turns update the assembler and the
     status hub; arrow keys and chip clicks change tone/level; a `Task.Delay` on the `TimeProvider` enforces the
     cap.
  4. **Finalising**: stop capture, complete the channel, wait ≤1.5 s for the tail to drain, run the handshake
     under the 2.5 s cap, complete the WAV.
  5. **PostProcessing**: router → LLM or passthrough. Failure yields raw text and the "cleanup skipped" badge.
  6. **Inserting**: re-capture the foreground; paste if editable and not elevated, else clipboard + toast;
     write the history record; bump dictionary use counts.
- Faults at any point keep the WAV, write a Failed record and toast; `MicrophoneAccessDeniedException` gets a
  toast that deep-links to the Windows privacy page. A watcher warns after 2 s of all-silent frames.
- `RetryAsync` and `TranscribeWavAsync` re-stream a WAV through a fresh session (used by History › Retry and
  `--stream-test`); `SimulateDictationFromWav` runs a normal session with a WAV standing in for the microphone.

## Step 6: Windows adapters

- **P/Invoke**: a single `NativeMethods` file declares the ~25 functions and structs used (hook, message pump,
  SendInput, foreground/thread info, clipboard sequence, process token, window styles, monitor info).
- **Keyboard hook**: `SetWindowsHookEx(WH_KEYBOARD_LL)` on a dedicated above-normal-priority thread running
  `GetMessage`. The callback marshals `KBDLLHOOKSTRUCT`, calls a filter delegate and returns 1 to swallow or
  `CallNextHookEx` otherwise, inside a try/catch so nothing can escape into the hook chain. A timer posts a
  re-install message every minute because Windows silently drops slow hooks.
- **HotkeyService** keeps the set of physically held keys (injected events are ignored unless the debug switch
  is on). The chord fires when all chord keys and only chord keys are down and no other key was pressed while
  a modifier was held. Firing a Win chord injects VK 0xE8 down/up through `SendInput`. While the chord is
  active, Escape/arrows raise events and every other key is swallowed. Handlers are queued on a single-consumer
  channel so their order is preserved and the hook thread never blocks.
- **Audio**: `WasapiCapture` in shared mode with 20 ms buffers delivers the device mix format;
  `Pcm16Pipeline` runs `BufferedWaveProvider → mono mix → WdlResampler(16 kHz) → PCM16` and slices 3200-byte
  frames with a peak level. Our own 16 kHz mono WAVs bypass the float round trip. `WavFileSink` writes with
  NAudio's `WaveFileWriter`; `WavReplayAudioCapture` paces frames at 100 ms ÷ speed.
- **Focus**: `FocusedEditableDetector` runs the UIA probe on a task with a 350 ms budget (some apps stall UIA),
  then `GetGUIThreadInfo().hwndCaret`, then the allowlist. `ElevationProbe` opens the process with
  `PROCESS_QUERY_LIMITED_INFORMATION`, queries `TokenElevation`, and treats access-denied on the token as
  elevated. `ForegroundContextProvider` reads the omnibox by its accessible name per browser, falling back to the
  first Edit whose value looks like a URL.
- **Clipboard**: every operation runs on a new STA thread (`Clipboard` requires STA and OLE) with 10 retries on
  `CLIPBRD_E_CANT_OPEN`. The snapshot keeps UnicodeText, Text, HTML, RTF, DIB and FileDrop; streams are copied to
  byte arrays so they survive the thread. `ClipboardPasteInserter` sets the text, sends key-ups for any modifier
  still physically held, restores the target to the foreground (`AttachThreadInput` trick), sends Ctrl+V with
  scan codes, waits 250 ms plus a stable `GetClipboardSequenceNumber`, then restores the snapshot only if the
  sequence number still equals the one after our own set.

## Step 7: WPF app

- `Program.Main` runs `VelopackApp.Build().Run()` first (install hooks), parses switches, and either runs a
  headless mode or takes the single-instance mutex and starts `App`.
- `HostFactory` builds a Generic Host (`DisableDefaults` to skip appsettings/env config) with Serilog rolling
  files, `AddDictationCore` + `AddDictationWindows`, and either the WPF services (notifier, tray, windows, retention
  and update services) or a console notifier.
- MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`). Windows are resolved from DI per
  open; `Shell` keeps one instance of each. `WpfNotifier` and `TrayIcon` resolve each other lazily through
  `IServiceProvider` to avoid a constructor cycle with the orchestrator.
- `FlowBarWindow` sets `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` in `OnSourceInitialized`, positions
  itself with `MonitorFromWindow`/`GetMonitorInfo`/`GetDpiForMonitor`, animates the recording dot, flashes its
  border when a chord press is ignored, and lingers 1.8 s after Idle when a badge is set.
- `TrayIcon` uses H.NotifyIcon with a code-built context menu; toasts go through `ShowNotification` and a
  pending action URI is opened on click.
- The chord recorder in Settings collects keys held together (`PreviewKeyDown`/`Up`) and writes the chord
  text; the API tab's "Test key" requests a streaming token (proves the key) and then a two-token gateway
  completion (proves LLM access), reporting each separately.

## Step 8: tests

143 tests, all green, runnable with `dotnet test DictationApp.sln`:

- Core (129): assembler, state machine (fake clock), normaliser, prompt builder, router, validator, gateway
  client (scripted `HttpMessageHandler`, fake clock for timeouts), formatter, rules, keyterms, differ, SQLite
  repository on a temp DB, settings store, chord parsing, session options, message parser, cost estimator, and
  eleven orchestrator scenarios with in-memory fakes.
- Windows (14): DPAPI round trip, WAV sink, PCM pipeline, hotkey filter behaviour by invoking the hook's
  filter delegate directly (no real hook).

## Step 9: packaging and release

- `build/pack.ps1` publishes framework-dependent win-x64 and runs `vpk pack` into `artifacts/releases`.
- `release.yml` packs on `v*` tags and attaches the installer to a GitHub Release; `UpdateCheckService` reads the
  same release feed through Velopack's `GithubSource`.

## Verifying on a desktop

```powershell
dotnet test DictationApp.sln
dotnet run --project src/DictationApp -- --stream-test sample.wav          # real streaming session
dotnet run --project src/DictationApp -- --simulate sample.wav --delay 5   # full pipeline into the focused window
dotnet run --project src/DictationApp                                      # tray app
```

Note: on a locked session (`LogonUI` running) `OpenClipboard` fails for every process and there is no
foreground window, so `--simulate` ends in a Failed record with a clipboard error; run it on an unlocked desktop.
