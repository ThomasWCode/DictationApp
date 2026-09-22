using System.IO;
using System.Runtime.InteropServices;
using DictationApp.Core.Abstractions;
using DictationApp.Core.History;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Hotkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DictationApp.Cli;

public sealed record CommandLineOptions(
    string? StreamTest,
    string? Simulate,
    int DelaySeconds,
    bool Minimized,
    bool AcceptInjectedKeys,
    bool ShowSettings,
    string? OutputFile = null)
{
    public bool IsCliMode => StreamTest is not null || Simulate is not null;

    public static CommandLineOptions Parse(string[] args)
    {
        string? streamTest = null;
        string? simulate = null;
        var delay = 0;
        var minimized = false;
        var injected = false;
        var showSettings = false;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--stream-test" when i + 1 < args.Length:
                    streamTest = args[++i];
                    break;
                case "--simulate" when i + 1 < args.Length:
                    simulate = args[++i];
                    break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d):
                    delay = d;
                    i++;
                    break;
                case "--minimized":
                    minimized = true;
                    break;
                case "--accept-injected-keys":
                    injected = true;
                    break;
                case "--settings":
                    showSettings = true;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
            }
        }

        return new CommandLineOptions(streamTest, simulate, delay, minimized, injected, showSettings, output);
    }
}

/// <summary>Prints toasts to the console when there is no tray icon.</summary>
public sealed class ConsoleNotifier : INotifier
{
    public void Toast(string title, string message, ToastKind kind = ToastKind.Info, string? actionUri = null) =>
        Console.WriteLine($"[{kind}] {title}: {message}{(actionUri is null ? string.Empty : " (" + actionUri + ")")}");
}

/// <summary>
/// Headless entry points. A WinExe has no console, so we attach to the parent's (the terminal that launched
/// us) before printing. Output after the process exits may interleave with the shell prompt; that is normal.
/// </summary>
public static class ConsoleRunner
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static int Run(CommandLineOptions options)
    {
        AttachConsole(AttachParentProcess);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        // A WinExe launched from a tool or a pipe may have no console at all; --output keeps the report.
        StreamWriter? file = options.OutputFile is { } path ? new StreamWriter(path, append: false) { AutoFlush = true } : null;
        Console.SetOut(new TeeWriter(stdout, file));
        Console.SetError(Console.Out);
        try
        {
            return RunAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
        finally
        {
            file?.Dispose();
        }
    }

    private sealed class TeeWriter(TextWriter primary, TextWriter? secondary) : TextWriter
    {
        public override System.Text.Encoding Encoding => primary.Encoding;

        public override void Write(char value)
        {
            primary.Write(value);
            secondary?.Write(value);
        }

        public override void Write(string? value)
        {
            primary.Write(value);
            secondary?.Write(value);
        }

        public override void WriteLine(string? value)
        {
            primary.WriteLine(value);
            secondary?.WriteLine(value);
        }

        public override void Flush()
        {
            primary.Flush();
            secondary?.Flush();
        }
    }

    private static async Task<int> RunAsync(CommandLineOptions options)
    {
        using var host = HostFactory.Build(options, ui: false);
        await host.StartAsync();
        try
        {
            var orchestrator = host.Services.GetRequiredService<DictationOrchestrator>();
            if (options.StreamTest is { } wav)
            {
                return await StreamTestAsync(orchestrator, wav);
            }

            if (options.Simulate is { } simulate)
            {
                return await SimulateAsync(host, orchestrator, simulate, options.DelaySeconds);
            }

            return 2;
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<int> StreamTestAsync(DictationOrchestrator orchestrator, string wav)
    {
        if (!File.Exists(wav))
        {
            Console.Error.WriteLine($"File not found: {wav}");
            return 1;
        }

        Console.WriteLine($"Streaming {wav} at 1x ...");
        var result = await orchestrator.TranscribeWavAsync(wav, 1.0, turn =>
        {
            var flag = turn.EndOfTurn ? (turn.TurnIsFormatted ? "FINAL*" : "FINAL ") : "partial";
            Console.WriteLine($"  turn {turn.TurnOrder,3} {flag} conf={turn.EndOfTurnConfidence:0.00} | {turn.BestText}");
        }, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"Connect latency : {result.ConnectLatency?.TotalMilliseconds ?? -1:0} ms");
        Console.WriteLine($"Audio duration  : {result.AudioDuration.TotalSeconds:0.0} s");
        Console.WriteLine($"Wall clock      : {result.WallClock.TotalSeconds:0.0} s");
        Console.WriteLine($"Termination     : audio={result.Termination?.AudioDurationSeconds:0.0}s session={result.Termination?.SessionDurationSeconds:0.0}s");
        Console.WriteLine($"Final text      : {result.Text}");
        return string.IsNullOrWhiteSpace(result.Text) ? 3 : 0;
    }

    private static async Task<int> SimulateAsync(IHost host, DictationOrchestrator orchestrator, string wav, int delaySeconds)
    {
        if (!File.Exists(wav))
        {
            Console.Error.WriteLine($"File not found: {wav}");
            return 1;
        }

        if (delaySeconds > 0)
        {
            Console.WriteLine($"Focus the target text box now; dictation starts in {delaySeconds} s ...");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        var hub = host.Services.GetRequiredService<DictationStatusHub>();
        hub.Changed += s => Console.WriteLine($"  [{s.State}] {s.Badge ?? string.Empty} {Truncate(s.LiveText, 80)}");
        orchestrator.SimulateDictationFromWav(wav);
        await Task.Delay(500);
        var task = orchestrator.CurrentSessionTask;
        if (task is not null)
        {
            await task.WaitAsync(TimeSpan.FromMinutes(5));
        }

        var history = host.Services.GetRequiredService<IHistoryRepository>();
        var latest = await history.GetLatestAsync();
        if (latest is null)
        {
            Console.WriteLine("No history record written (nothing heard or discarded).");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine($"Record #{latest.Id}: status={latest.Status} app={latest.ProcessName} tone={latest.Tone} level={latest.Level} llm={latest.LlmModel ?? "none"} reason={latest.FailureReason ?? "-"}");
        Console.WriteLine($"Raw     : {latest.RawTranscript}");
        Console.WriteLine($"Inserted: {latest.InsertedText}");
        Console.WriteLine($"Audio   : {latest.AudioPath ?? "-"}  cost=${latest.CostEstimate:0.0000}");
        return latest.Status == RecordStatus.Failed ? 4 : 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}

/// <summary>Keeps the hotkey hook's debug switch in sync with settings and the command line.</summary>
public static class HotkeyDebug
{
    public static void Apply(HotkeyService hotkeys, ISettingsStore settings, CommandLineOptions options) =>
        hotkeys.AcceptInjectedKeys = options.AcceptInjectedKeys || settings.Current.AcceptInjectedKeys;
}
