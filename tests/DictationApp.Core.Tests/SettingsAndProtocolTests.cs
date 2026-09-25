using System.Text;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.History;
using DictationApp.Core.Settings;
using DictationApp.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DictationApp.Core.Tests;

public class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Win", "Ctrl+Win")]
    [InlineData("win + ctrl", "Ctrl+Win")]
    [InlineData("Control+Alt", "Ctrl+Alt")]
    [InlineData("F8", "F8")]
    [InlineData("CapsLock", "CapsLock")]
    [InlineData("Ctrl+Shift+D", "Ctrl+Shift+D")]
    public void Parses_and_normalises(string input, string expected)
    {
        Assert.Equal(expected, HotkeyChord.Parse(input).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Bogus")]
    [InlineData("Ctrl+Shift+Alt+Win")]
    public void Rejects_invalid(string input)
    {
        Assert.False(HotkeyChord.TryParse(input, out _));
    }

    [Fact]
    public void Left_right_variants_normalise_to_generic()
    {
        Assert.Equal(HotkeyChord.VkControl, HotkeyChord.Normalise(HotkeyChord.VkRControl));
        Assert.Equal(HotkeyChord.VkLWin, HotkeyChord.Normalise(HotkeyChord.VkRWin));
        Assert.Equal(HotkeyChord.Default, new HotkeyChord([HotkeyChord.VkRWin, HotkeyChord.VkLControl]));
        Assert.True(HotkeyChord.Default.ContainsWin);
        Assert.False(HotkeyChord.Alternative.ContainsWin);
    }
}

public class SessionOptionsTests
{
    [Fact]
    public void Builds_query_string_with_all_parameters()
    {
        var q = new SessionOptions
        {
            SpeechModel = "universal-3-5-pro",
            Keyterms = ["LSHTM", "isoniazid"],
            Prompt = "Medical dictation",
            LanguageCodes = "en",
            MinTurnSilenceMs = 400,
            MaxTurnSilenceMs = 1536,
            VadThreshold = 0.2,
            InactivityTimeoutSeconds = 30,
        }.BuildQueryString();

        Assert.StartsWith("?sample_rate=16000&speech_model=universal-3-5-pro&encoding=pcm_s16le&format_turns=true", q);
        Assert.Contains("prompt=Medical%20dictation", q);
        Assert.Contains("keyterms_prompt=%5B%22LSHTM%22%2C%22isoniazid%22%5D", q);
        Assert.Contains("language_codes=en", q);
        Assert.Contains("min_turn_silence=400", q);
        Assert.Contains("max_turn_silence=1536", q);
        Assert.Contains("vad_threshold=0.2", q);
        Assert.Contains("inactivity_timeout=30", q);
    }

    [Fact]
    public void Prompt_is_only_sent_to_pro_and_truncated()
    {
        var std = new SessionOptions { SpeechModel = "universal-streaming", Prompt = "x" }.BuildQueryString();
        Assert.DoesNotContain("prompt=", std);

        var longPrompt = new string('p', 2000);
        var pro = new SessionOptions { Prompt = longPrompt }.BuildQueryString();
        var value = pro.Split("prompt=")[1].Split('&')[0];
        Assert.Equal(SessionOptions.MaxPromptLength, value.Length);
    }
}

public class StreamingMessageParserTests
{
    [Fact]
    public void Parses_begin_turn_termination_and_error()
    {
        var begin = StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Begin\",\"id\":\"abc\",\"expires_at\":1700000000}"));
        Assert.IsType<BeginMessage>(begin);
        Assert.Equal("abc", ((BeginMessage)begin!).Id);

        var turn = StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Turn\",\"turn_order\":3,\"turn_is_formatted\":true,\"end_of_turn\":true,\"transcript\":\"hi there\",\"utterance\":\"Hi there.\",\"end_of_turn_confidence\":0.93,\"words\":[{\"start\":0,\"end\":100,\"text\":\"hi\",\"confidence\":0.9,\"word_is_final\":true}]}"));
        var t = Assert.IsType<TurnMessage>(turn);
        Assert.Equal(3, t.TurnOrder);
        Assert.True(t.TurnIsFormatted);
        Assert.Equal("Hi there.", t.BestText);
        Assert.Single(t.Words!);

        var term = StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Termination\",\"audio_duration_seconds\":10.5,\"session_duration_seconds\":12}"));
        Assert.Equal(10.5, ((TerminationMessage)term!).AudioDurationSeconds);

        var error = StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"error\":\"Invalid API key\"}"));
        Assert.Equal("Invalid API key", ((ErrorMessage)error!).Error);

        // Typed error frames fail the session too, whatever shape the detail has.
        Assert.Equal("Invalid API key", Assert.IsType<ErrorMessage>(StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Error\",\"error\":\"Invalid API key\"}"))).Error);
        Assert.Contains("1008", Assert.IsType<ErrorMessage>(StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Error\",\"error\":{\"code\":1008}}"))).Error);
        Assert.Equal("unknown error", Assert.IsType<ErrorMessage>(StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"Error\"}"))).Error);

        Assert.Null(StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"SpeechStarted\"}")));
        Assert.Null(StreamingMessageParser.Parse(Encoding.UTF8.GetBytes("[]")));
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Round_trips_and_notifies()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
        AppSettings? changed = null;
        store.Changed += s => changed = s;

        await store.UpdateAsync(s =>
        {
            s.Hotkey = "Ctrl+Alt";
            s.DefaultTone = Tone.Casual;
            s.Dictionary.Add(new Core.Dictionary.DictionaryTerm { Term = "LSHTM", Starred = true });
            s.HistoryRetention = RetentionPolicy.Forever;
        });

        Assert.NotNull(changed);
        Assert.True(File.Exists(path));
        Assert.Contains("\"Casual\"", await File.ReadAllTextAsync(path)); // enums as strings

        var reloaded = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
        Assert.Equal("Ctrl+Alt", reloaded.Current.Hotkey);
        Assert.Equal(Tone.Casual, reloaded.Current.DefaultTone);
        Assert.Equal(RetentionPolicy.Forever, reloaded.Current.HistoryRetention);
        Assert.Single(reloaded.Current.Dictionary);
        Assert.Equal(HotkeyChord.Alternative, reloaded.Current.HotkeyChord);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(HotkeyChord.Default.ToString(), store.Current.Hotkey);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Api_key_provider_prefers_stored_key_then_environment()
    {
        var store = Substitute.For<ISettingsStore>();
        var secrets = Substitute.For<ISecretStore>();
        store.Current.Returns(new AppSettings { ApiKeyProtected = "blob" });
        secrets.Unprotect("blob").Returns(" stored ");
        Assert.Equal("stored", new SettingsApiKeyProvider(store, secrets).GetApiKey());

        secrets.Unprotect("blob").Returns((string?)null);
        var previous = Environment.GetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable, "env-key");
            Assert.Equal("env-key", new SettingsApiKeyProvider(store, secrets).GetApiKey());
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable, null);
            Assert.Null(new SettingsApiKeyProvider(store, secrets).GetApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Groq_key_is_resolved_separately_from_the_assemblyai_key()
    {
        var store = Substitute.For<ISettingsStore>();
        var secrets = Substitute.For<ISecretStore>();
        store.Current.Returns(new AppSettings { ApiKeyProtected = "a", GroqApiKeyProtected = "g" });
        secrets.Unprotect("a").Returns("assemblyai");
        secrets.Unprotect("g").Returns("gsk_groq");
        var provider = new SettingsApiKeyProvider(store, secrets);
        Assert.Equal("assemblyai", provider.GetApiKey());
        Assert.Equal("gsk_groq", provider.GetLlmApiKey());

        var previous = Environment.GetEnvironmentVariable(SettingsApiKeyProvider.LlmEnvironmentVariable);
        try
        {
            store.Current.Returns(new AppSettings());
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.LlmEnvironmentVariable, "gsk_env");
            Assert.Equal("gsk_env", provider.GetLlmApiKey());
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.LlmEnvironmentVariable, null);
            Assert.Null(provider.GetLlmApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsApiKeyProvider.LlmEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Schema_v1_settings_migrate_to_groq_models()
    {
        Directory.CreateDirectory(_migrationDir);
        var path = Path.Combine(_migrationDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"SchemaVersion\": 1, \"LlmModel\": \"gemini-2.5-flash-lite\", \"LlmFallbackModels\": [\"gemini-2.5-flash\"], \"HotkeyMode\": \"Hold\" }");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(3, store.Current.SchemaVersion);
        Assert.Equal(AppSettings.DefaultLlmModel, store.Current.LlmModel);
        Assert.Equal(AppSettings.DefaultLlmBaseUrl, store.Current.LlmBaseUrl);
        Assert.Equal("openai/gpt-oss-120b", store.Current.LlmModel);
        Assert.Contains("qwen/qwen3.8-27b", store.Current.LlmFallbackModels);
        Assert.Equal(FlowBarMode.Full, store.Current.FlowBarMode);
        Directory.Delete(_migrationDir, recursive: true);
    }

    [Theory]
    [InlineData("\"ShowFlowBar\": false", FlowBarMode.Hidden)]
    [InlineData("\"ShowFlowBar\": true", FlowBarMode.Full)]
    public async Task Schema_v1_show_flow_bar_maps_to_flow_bar_mode(string legacy, FlowBarMode expected)
    {
        Directory.CreateDirectory(_migrationDir);
        var path = Path.Combine(_migrationDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"SchemaVersion\": 1, " + legacy + " }");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(expected, store.Current.FlowBarMode);
        Directory.Delete(_migrationDir, recursive: true);
    }

    [Fact]
    public async Task Schema_v2_settings_are_not_migrated_again()
    {
        Directory.CreateDirectory(_migrationDir);
        var path = Path.Combine(_migrationDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"SchemaVersion\": 2, \"LlmModel\": \"custom/model\", \"FlowBarMode\": \"Minimal\", \"ShowFlowBar\": false }");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal("custom/model", store.Current.LlmModel);
        Assert.Equal(FlowBarMode.Minimal, store.Current.FlowBarMode);
        Directory.Delete(_migrationDir, recursive: true);
    }

    [Fact]
    public async Task Schema_v2_seeded_app_rules_are_removed_and_user_rules_kept()
    {
        Directory.CreateDirectory(_migrationDir);
        var path = Path.Combine(_migrationDir, "settings.json");
        // As 0.2 wrote it: the seeded rules (one altered by "remember style"), plus one the user added.
        await File.WriteAllTextAsync(path, """
            { "SchemaVersion": 2, "AppRules": [
              { "ProcessGlob": "OUTLOOK", "Tone": "Casual", "Level": "High", "Enabled": true },
              { "UrlHost": "mail.google.com", "Tone": "Formal", "Level": "Medium", "Enabled": true },
              { "ProcessGlob": "ms-teams", "Tone": "Casual", "Level": "Light", "Enabled": true },
              { "ProcessGlob": "notepad", "Tone": "Formal", "Enabled": true }
            ] }
            """);
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(3, store.Current.SchemaVersion);
        var kept = Assert.Single(store.Current.AppRules);
        Assert.Equal("notepad", kept.ProcessGlob);
        Assert.Equal(Tone.Formal, kept.Tone);
        Directory.Delete(_migrationDir, recursive: true);
    }

    [Fact]
    public async Task Schema_v3_rules_are_not_migrated_again()
    {
        Directory.CreateDirectory(_migrationDir);
        var path = Path.Combine(_migrationDir, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"SchemaVersion\": 3, \"AppRules\": [ { \"ProcessGlob\": \"OUTLOOK\", \"Tone\": \"Formal\", \"Enabled\": true } ] }");
        var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);

        // Added back by the user after the migration: it stays.
        Assert.Equal("OUTLOOK", Assert.Single(store.Current.AppRules).ProcessGlob);
        Directory.Delete(_migrationDir, recursive: true);
    }

    private readonly string _migrationDir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));
}

public class CostEstimatorTests
{
    [Fact]
    public void Estimates_streaming_and_llm_costs()
    {
        Assert.Equal(0.0075m, CostEstimator.SttCost("universal-3-5-pro", TimeSpan.FromMinutes(1)));
        Assert.Equal(0.0025m, CostEstimator.SttCost("universal-streaming", TimeSpan.FromMinutes(1)));
        Assert.Equal(0m, CostEstimator.LlmCost("qwen/qwen3.8-27b", 1000, 100)); // Groq free tier
        Assert.Equal(0m, CostEstimator.LlmCost("unknown-model", 1000, 100));
        Assert.Equal(0m, CostEstimator.LlmCost(null, 1000, 100));
    }

    [Fact]
    public void Retention_policies_map_to_spans()
    {
        Assert.Equal(TimeSpan.FromHours(24), RetentionPolicy.Hours24.ToTimeSpan());
        Assert.Equal(TimeSpan.FromDays(14), RetentionPolicy.Days14.ToTimeSpan());
        Assert.Null(RetentionPolicy.Forever.ToTimeSpan());
    }
}
