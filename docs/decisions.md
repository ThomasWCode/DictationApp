# Decisions

Choices made while building, with the reasoning, including the places where the implementation departs from
[the plan](PLAN.md). Dates are 2026-09-22 unless stated.

## Kept from the plan

| Decision | Why |
|---|---|
| C# / .NET 8 + WPF, tray-resident, no main window | Native footprint (~60 MB idle vs Electron's hundreds), first-class Win32/UIA access, and WPF's clipboard and windowing are mature. |
| Universal-3.5 Pro as the default speech model | It accepts `keyterms_prompt` at no extra cost and always returns formatted turns; the standard model remains selectable in Settings for cost. |
| One AssemblyAI key for both STT and LLM | No second vendor account; the gateway is OpenAI-compatible so the client is a few dozen lines. |
| Paste, never type | `SendInput` of characters is slow, breaks on IME/layout differences and floods undo stacks; a single Ctrl+V is atomic and respects the app's own paste handling. |
| Clipboard snapshot and restore | Users lose copied text otherwise. Only formats that round-trip losslessly (Unicode/ANSI text, HTML, RTF, DIB, file list) are captured, and restore is skipped when another app changed the clipboard after our paste. |
| No warm connection | Streaming is billed per session second; an idle socket would cost ~$0.45 per hour. Audio is buffered from chord-down and flushed on `Begin`, so connect latency delays only the live preview, never the result. |
| Short tap under 300 ms cancels | Distinguishes an accidental press from a dictation and avoids paying for a session that never carried speech. |
| Stateless for the state machine | Declarative transitions with `Ignore` for every irrelevant trigger; the whole table is unit-tested. |
| SQLite + FTS5 with external content and triggers | One file, no server, instant prefix search over thousands of dictations; triggers make the index impossible to forget. |
| DPAPI (CurrentUser) for the key | Reversible per-user protection without a password prompt; the file is useless on another machine or account. |
| Velopack for install/update | Maintained successor to Squirrel; per-user install to `%LOCALAPPDATA%`, delta updates, GitHub Releases as the feed. |

## Departures from the plan

| Plan | Implemented | Why |
|---|---|---|
| CsWin32 source-generated P/Invoke | Hand-written `DllImport` declarations in one `NativeMethods` file | About twenty-five functions is small enough to write by hand, the signatures are stable, and generated code under `TreatWarningsAsErrors` was one more moving part than the benefit justified. |
| `IClock` abstraction | .NET 8's `TimeProvider` | Built in, and `Microsoft.Extensions.TimeProvider.Testing` gives `FakeTimeProvider` for the state machine, the gateway timeouts, retention and the formatter memory window. |
| "Test key" hits `GET /v1/models` | Requests a streaming token, then a two-token gateway completion | `/v1/models` is unauthenticated, so it proves nothing about the key. The token endpoint validates the key for the core feature; the tiny completion reveals whether the account has LLM Gateway access, which turned out to matter (see below). |
| History status enum named `DictationStatus` | `RecordStatus` | The name collided with the Flow bar's status snapshot record in `Session`; renaming the enum was clearer than aliasing in every file. |
| `PROGRESS.md` inside the repo | Kept above the repo and deleted at the end | The user asked for the progress file to live outside the repo. |
| "Keep connection warm" setting if p95 connect > 400 ms | Not added; measured ~960 ms connect-to-`Begin`, documented instead | The latency is real (TLS + session setup) but it only delays the first live words, never the inserted result, because arming buffers audio. A warm socket would bill continuously; a "warm window after a dictation" variant is the sensible follow-up if the preview delay bothers users. |
| Watchdog "re-installs the hook if events stop arriving while keys are pressed" | Unconditional re-install every 60 s | Detecting "events stopped" reliably is hard; re-hooking is cheap and bounds any silent-unhook outage to a minute. Chord state lives outside the hook, so re-installing mid-hold is harmless. |
| Hook callback "does no work" | Callback runs the chord filter (a few set operations) synchronously | Swallowing a key requires deciding inside the callback. The filter never blocks and never touches user code; handlers are queued to an ordered channel. |

## Choices the plan left open

| Question | Choice | Why |
|---|---|---|
| Which keys to swallow while the chord is held | Everything except the chord keys; arrows and Escape become commands | Win+Ctrl+Left/Right switches virtual desktops and Win+Ctrl+D creates one; letting any key through mid-dictation is a trap. |
| How to end the hold | Releasing any chord key | Users lift fingers in any order; waiting for all keys would feel laggy. |
| Double-tap window | 400 ms | Comfortable for a deliberate double tap, short enough not to catch two separate presses. |
| Injected key events | Ignored by default, debug switch to accept them | Our own `SendInput` (0xE8, Ctrl+V) must not be tracked, and other automation should not trigger dictation; the switch exists for UI automation and tests. |
| Elevation detection when the token cannot be opened | Treat as elevated | A same-integrity process always lets us open its token with `TOKEN_QUERY`; a denial is the signal. The cost of a false positive is a clipboard-only result, never a lost dictation. |
| Editable detection default | Editable | A paste into a non-editable control is a no-op and the text stays on the clipboard; the opposite default would silently withhold text from apps with poor UIA support. |
| Clipboard thread model | A fresh STA thread per operation | WPF's `Clipboard` needs STA and OLE; a fresh thread avoids a long-lived pump and any re-entrancy with the WPF dispatcher. Operations are rare and short. |
| Reading the browser URL | UIA name of the address bar per browser, 250 ms budget, URL-shaped Edit as fallback | The names are stable in English builds; the fallback catches localised builds. A timeout keeps chord-down latency bounded. |
| History window "Re-insert" | Minimise History, wait 350 ms, paste into the newly focused window | History itself is the foreground when the button is clicked; minimising returns focus to the previous app. |
| "Undo AI edit" | Copies the raw transcript and marks the record | Reversing a paste in a foreign app is not safe in general; the user pastes over the selection. |
| Model prices in the cost estimate | Static table from `GET /v1/models` on 2026-09-22 | Live lookups would add a network call per dictation; unknown models estimate at zero and are labelled as such. |
| Output validation thresholds | Reject > 2.5× or < 0.4× input length, banned prefixes, skipped under 20 characters | From the plan; the short-input exemption was added because a two-word input legitimately becomes a full sentence. |
| Where CLI output goes | Attached parent console plus optional `--output` file | A WinExe launched from a tool or a pipe may have no console; the file keeps the report. |

## Findings from live testing

- **The provided account has no LLM Gateway access.** Every model, including the plan's default
  `gemini-2.5-flash-lite`, returns HTTP 400 "Your account does not have access to this LLM Gateway model". The
  fallback chain therefore ends in raw text with the "cleanup skipped" badge in roughly 400 ms (three fast 400s),
  which is the designed behaviour. The gateway client is verified with a scripted HTTP handler. Enabling the
  gateway on the AssemblyAI account is a billing/dashboard action outside the app.
- **Streaming behaves as documented**: turns arrive with growing `utterance` partials, then a formatted
  `end_of_turn` turn; the assembler's ordering rules were exercised by real traffic.
- **Connect latency ~960 ms** on this network; the shutdown handshake takes ~890 ms when no turn is open.
- **A locked workstation blocks clipboard and foreground access** for every process (`OpenClipboard` fails,
  `GetForegroundWindow` is null). The `--simulate` run made during a locked session reached Inserting and then
  recorded a Failed entry with the clipboard error, so the failure path was exercised too.
- **On the unlocked desktop both insertion outcomes were verified live**: with no editable focus the text was
  copied with the "No text box has focus" toast (record `CopiedOnly`); with a Notepad document focused, UIA
  reported `Document`, the paste landed (record `Inserted`) and the clipboard was restored to its prior value.
- **Windows 11 Notepad is single-instance and restores the selection state of unsaved tabs**, so a paste into it
  replaces whatever is selected. `--simulate` targets the real foreground window; a scratch document is the
  right target when testing.
- **Window activation from a background process is refused by Windows** (`AppActivate`/`SetForegroundWindow`
  without the input lock), which is why the inserter attaches to the foreground thread's input queue before
  refocusing the captured target.

## Changes requested after the first delivery (2026-09-22)

| Request | Decision | Why |
|---|---|---|
| "Remember the previous formality and cleanup settings from last time" | A tone/level change made during a dictation is persisted immediately: it updates the app rule that matched, or the global defaults when none did. Setting `RememberStyleChanges` (default on) restores the plan's one-dictation behaviour. | Updating the matched rule keeps the app-aware model intact: choosing Casual in Outlook does not turn every other app casual. Persisting at override time rather than at the end means an Escape still keeps the choice, which matches "remember what I set". |
| "Write lists when the transcription says 1… 2… 3…" | Two layers: the LLM prompt now asks for numbered or bulleted lists for enumerations, and `ListFormatter` in the regex path turns unambiguous markers ("1." / "1)" / "number one" / "point one", at least two in ascending order from 1) into one item per line. | The user's account has no LLM Gateway access today, so the regex layer is what actually runs for them. It is deliberately conservative: ordinal words ("firstly, secondly") are left to the LLM because they are common in prose. |
| "It should run in the background automatically from boot" | `Autostart` defaults to true and an installed build rewrites its Run key on every start. The app is delivered as a Velopack `Setup.exe` and installed for the user. | Rewriting on every start survives updates that move the executable and repairs a deleted key. Development builds are excluded so a `dotnet run` never registers a `bin\` path. |
| Data location | Moved from `%LOCALAPPDATA%\DictationApp` to `%LOCALAPPDATA%\ThomasWCode\DictationApp`. | Velopack installs the program into `%LOCALAPPDATA%\DictationApp` and deletes that folder on uninstall; history, audio and the encrypted key must not live there. |

## Second round of changes (2026-09-22, v0.2.0)

| Request | Decision | Why |
|---|---|---|
| "Hold and double-tap should both work" | The `HotkeyMode` setting is gone. A press is a tap when shorter than 300 ms; a second tap within 400 ms of the first tap's release keeps the dictation running hands-free; any later press stops it. A long press is a hold. | The first tap still cancels its own session (short press) and the second tap starts a fresh one; the orchestrator chains the new session behind the old one's teardown so the second tap is never rejected as "busy". A session rarely reaches `Begin` within 400 ms, so the cancelled first tap is not billed. |
| Keys during hands-free dictation | Only Escape is intercepted; arrows and everything else pass through. | The swallow-everything rule exists to stop Win+Ctrl shortcuts while modifiers are physically held. Hands-free, nothing is held, and blocking the keyboard for minutes would be hostile. Tone and level are still changeable via the Flow bar chips. |
| "Use AssemblyAI only for transcription, Groq for the rest, free tier only" | `LlmPostProcessor` is an OpenAI-compatible client (Bearer auth) pointed at `https://api.groq.com/openai/v1/` by default; separate `GroqApiKeyProtected` key and `GROQ_API_KEY` fallback. Default chain `openai/gpt-oss-120b` → `qwen/qwen3.8-27b` → `openai/gpt-oss-20b`; gpt-oss models get `reasoning_effort: low`. Schema-v1 settings are migrated. | Measured on the user's free-tier key on 2026-09-22 with the real Medium+Formal prompt on a messy email transcript: gpt-oss-120b (low effort) 982 ms, 176 completion tokens, the most polished wording; qwen3.8-27b 756 ms, 78 tokens, equally correct on the scratch-that, the LSHTM spelling and the list. gpt-oss-20b **without** low effort spent its entire budget on reasoning and truncated the answer. The user preferred gpt-oss-120b for quality; the 200 ms difference is small next to the ~1 s connect and handshake. Free-tier limits seen in headers: 1000 requests/day and 8000 tokens/min per model (a call is ~650 tokens), so 429s are handled by moving down the chain. The base URL is a setting so any OpenAI-compatible endpoint can be substituted. |
| "A mode in between the full bar and nothing" | `FlowBarMode { Full, Minimal, Hidden }` replaces the boolean. Minimal is a 132×16 px pill with only the level meter: red while listening, breathing blue while finishing/inserting, hidden the moment the dictation ends. | The user asked for "just the bar going up and down as you speak, nothing else". A breathing fill is the smallest possible cue that the text is still on its way; without it the pill would freeze for a second and look stuck. |
| "How do updates work; do settings survive?" | Verified end to end and documented: check → download → "Restart to update" or apply on next exit (`WaitExitThenApplyUpdates`). Settings live outside Velopack's folder. A GitHub token setting was added because the repository is private and the releases API answers 404 anonymously. | See the update walk-through in `implementation.md`: a 0.1.9 build of this code, installed over 0.1.0, updated itself to the published 0.2.0 with its settings byte-identical afterwards. The original 0.1.0 predates the token setting and cannot read the private repository, so it needs the 0.2.0 `Setup.exe` once. |

## Scope changes

- 2026-09-22: the Android companion app was dropped at the user's request; only the Windows app is delivered.
