using System.Diagnostics;
using System.IO;
using DictationApp.Core.Insertion;
using DictationApp.Windows.Focus;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace DictationApp.Windows.Tests;

/// <summary>
/// The text before the caret as the app reads it from Chromium, for the editors that matter: TipTap (the Claude apps),
/// ChatGPT's placeholder paragraph, Quill, Slate, textareas and inputs. Each state is set up by the page and read with the
/// app's own code; what counts is the spacing decision it leads to. Interactive (not run in CI): it needs Chrome or Edge
/// and opens a browser window, off screen, for a few seconds.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class ChromiumCaretReadTests : IDisposable
{
    private const string TitlePrefix = "CARET-TEST ";

    /// <summary>State, the field it focuses (its accessible name), and whether a dictation there starts with a space.</summary>
    private static readonly (string State, string Field, bool Space)[] Cases =
    [
        ("tiptap-empty", "tiptap", false),
        ("tiptap-text", "tiptap", true),
        ("tiptap-fresh-line", "tiptap", false),
        ("fresh-line-unmarked", "tiptap", false),
        ("placeholder-paragraph", "tiptap", false),
        ("widget-placeholder", "tiptap", false),
        ("hard-break-empty-line", "tiptap", false),
        ("text-after-hard-break", "tiptap", true),
        ("caret-starts-line-two", "tiptap", false),
        ("two-lines-caret-at-end", "tiptap", true),
        ("tiptap-empty-no-caret", "tiptap", false),
        ("quill-blank", "quill", false),
        ("quill-text", "quill", true),
        ("textarea-empty", "ta", false),
        ("textarea-caret-at-start", "ta", false),
        ("textarea-caret-at-end", "ta", true),
        ("input-after-label", "inp2", false),
        ("inline-editor-caret-at-start", "inl", false),
        ("textarea-after-label", "ta2", false),
        ("slate-placeholder", "slate", false),
        ("textarea-with-placeholder-class", "ta3", true),
        ("editor-root-with-placeholder-class", "ce", true),
    ];

    private const string Page = """
        <!doctype html>
        <meta charset="utf-8">
        <title>CARET-TEST loading</title>
        <style>
          body { font: 16px sans-serif; margin: 12px; }
          .ed, textarea, input { border: 1px solid #888; padding: 8px; width: 600px; margin: 6px 0; display: block; position: relative; }
          .inline { display: inline-block; width: 300px; }
          .tiptap p.is-editor-empty:first-child::before { content: attr(data-placeholder); float: left; color: #adb5bd; pointer-events: none; height: 0; }
          .tiptap p.placeholder::before { content: attr(data-placeholder); float: left; color: #adb5bd; pointer-events: none; height: 0; }
          .ql-editor.ql-blank::before { content: attr(data-placeholder); position: absolute; color: #aaa; pointer-events: none; }
        </style>
        <button id="next" aria-label="next step">Next</button>
        <div id="tiptap" class="tiptap ProseMirror ProseMirror-focused ed" contenteditable="true" role="textbox" aria-label="tiptap"></div>
        <div id="quill" class="ql-editor ql-blank ed" data-placeholder="Compose your message" contenteditable="true" role="textbox" aria-label="quill"><p><br></p></div>
        <textarea id="ta" aria-label="ta" placeholder="Type a message"></textarea>
        <p>Your name:<input id="inp2" class="inline" aria-label="inp2"></p>
        <p>Note:<span id="inl" class="inline" contenteditable="true" role="textbox" aria-label="inl">later text</span></p>
        <p><label>Message:<textarea id="ta2" class="inline" aria-label="ta2"></textarea></label></p>
        <div id="slate" class="ed" contenteditable="true" role="textbox" aria-label="slate"></div>
        <textarea id="ta3" class="placeholder" aria-label="ta3"></textarea>
        <div id="ce" class="placeholder ed" contenteditable="true" role="textbox" aria-label="ce">Existing text</div>
        <script>
          const $ = id => document.getElementById(id);
          const ed = $("tiptap"), q = $("quill"), ta = $("ta"), slate = $("slate");
          const EMPTY = '<p data-placeholder="Reply to Claude" class="is-empty is-editor-empty"><br class="ProseMirror-trailingBreak"></p>';
          function caretAt(node, offset) {
            const r = document.createRange(); r.setStart(node, offset); r.collapse(true);
            const s = getSelection(); s.removeAllRanges(); s.addRange(r);
          }
          function tiptap(html, place) { ed.innerHTML = html; ed.focus(); place(); }
          const steps = {
            "tiptap-empty": () => tiptap(EMPTY, () => caretAt(ed.firstChild, 0)),
            "tiptap-text": () => tiptap("<p>Hello there.</p>", () => caretAt(ed.firstChild.firstChild, 12)),
            "tiptap-fresh-line": () => tiptap('<p>First line.</p><p data-placeholder="Reply to Claude" class="is-empty"><br class="ProseMirror-trailingBreak"></p>', () => caretAt(ed.lastChild, 0)),
            "fresh-line-unmarked": () => tiptap('<p>First line.</p><p><br class="ProseMirror-trailingBreak"></p>', () => caretAt(ed.lastChild, 0)),
            "placeholder-paragraph": () => tiptap('<p data-placeholder="Ask anything" class="placeholder"><br class="ProseMirror-trailingBreak"></p>', () => caretAt(ed.firstChild, 0)),
            "widget-placeholder": () => tiptap('<p><span contenteditable="false">Reply to Claude</span><br class="ProseMirror-trailingBreak"></p>', () => caretAt(ed.firstChild, 1)),
            "hard-break-empty-line": () => tiptap('<p>First line.<br><br class="ProseMirror-trailingBreak"></p>', () => caretAt(ed.firstChild, 2)),
            "text-after-hard-break": () => tiptap("<p>First line.<br>Second</p>", () => caretAt(ed.firstChild.lastChild, 6)),
            "caret-starts-line-two": () => tiptap("<p>Line one.</p><p>Line two</p>", () => caretAt(ed.lastChild.firstChild, 0)),
            "two-lines-caret-at-end": () => tiptap("<p>Line one.</p><p>Line two</p>", () => caretAt(ed.lastChild.firstChild, 8)),
            "tiptap-empty-no-caret": () => tiptap(EMPTY, () => getSelection().removeAllRanges()),
            "quill-blank": () => { q.className = "ql-editor ql-blank ed"; q.innerHTML = "<p><br></p>"; q.focus(); caretAt(q.firstChild, 0); },
            "quill-text": () => { q.className = "ql-editor ed"; q.innerHTML = "<p>Some words</p>"; q.focus(); caretAt(q.firstChild.firstChild, 10); },
            "textarea-empty": () => { ta.value = ""; ta.focus(); },
            "textarea-caret-at-start": () => { ta.value = "Hello world"; ta.focus(); ta.setSelectionRange(0, 0); },
            "textarea-caret-at-end": () => { ta.value = "Hello world"; ta.focus(); ta.setSelectionRange(11, 11); },
            "input-after-label": () => { $("inp2").value = ""; $("inp2").focus(); },
            "inline-editor-caret-at-start": () => { $("inl").focus(); caretAt($("inl").firstChild, 0); },
            "textarea-after-label": () => { $("ta2").value = ""; $("ta2").focus(); },
            "slate-placeholder": () => {
              slate.innerHTML = '<p><span><span contenteditable="false" style="position:absolute;pointer-events:none;opacity:.4">Type a message</span><span>﻿<br></span></span></p>';
              slate.focus(); caretAt(slate.firstChild.firstChild.lastChild.firstChild, 0);
            },
            "textarea-with-placeholder-class": () => { $("ta3").value = "Hello world"; $("ta3").focus(); $("ta3").setSelectionRange(11, 11); },
            "editor-root-with-placeholder-class": () => { const c = $("ce"); c.focus(); caretAt(c.firstChild, c.firstChild.length); },
          };
          const order = ORDER;
          let i = 0;
          $("next").addEventListener("click", () => {
            const name = order[i++];
            steps[name]();
            document.title = "CARET-TEST " + name;
          });
          document.title = "CARET-TEST ready";
        </script>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));
    private Process? _browser;

    public void Dispose()
    {
        try
        {
            if (_browser is { HasExited: false })
            {
                _browser.Kill(entireProcessTree: true);
                _browser.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
        }

        _browser?.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // The browser may hold its profile open a moment longer; it is a temp folder.
        }
    }

    /// <summary>Chrome or Edge, or null.</summary>
    internal static string? BrowserPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [ChromiumFact]
    public void Empty_boxes_and_lines_get_no_space_and_text_before_the_caret_does()
    {
        Directory.CreateDirectory(_dir);
        var page = Path.Combine(_dir, "caret.html");
        File.WriteAllText(page, Page.Replace("ORDER", "[" + string.Join(", ", Cases.Select(c => "\"" + c.State + "\"")) + "]", StringComparison.Ordinal));

        var start = new ProcessStartInfo(BrowserPath()!) { UseShellExecute = false };
        foreach (var arg in new[]
        {
            "--user-data-dir=" + Path.Combine(_dir, "profile"), "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            "--disable-sync", "--window-position=-3000,-3000", "--window-size=900,900", "--force-renderer-accessibility",
            "--disable-features=CalculateNativeWinOcclusion", "--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding",
            "--app=" + new Uri(page).AbsoluteUri,
        })
        {
            start.ArgumentList.Add(arg);
        }

        _browser = Process.Start(start);
        using var automation = new UIA3Automation();
        var cf = new ConditionFactory(automation.PropertyLibrary);
        var window = Poll(() => automation.GetDesktop().FindAllChildren(cf.ByControlType(ControlType.Window)).FirstOrDefault(w => w.Name == TitlePrefix + "ready"), TimeSpan.FromSeconds(30))
            ?? throw new TimeoutException("The browser window did not open");
        var next = Poll(() => window.FindFirstDescendant(cf.ByControlType(ControlType.Button).And(cf.ByName("next step"))), TimeSpan.FromSeconds(10))
            ?? throw new TimeoutException("The page did not load");

        var fields = window.FindAllDescendants(cf.ByControlType(ControlType.Edit)).ToDictionary(e => e.Name ?? string.Empty, e => e);
        var failures = new List<string>();
        foreach (var (state, field, space) in Cases)
        {
            next.Patterns.Invoke.Pattern.Invoke();
            if (Poll(() => window.Name == TitlePrefix + state ? window : null, TimeSpan.FromSeconds(5)) is null)
            {
                throw new TimeoutException("The page did not reach " + state);
            }

            Thread.Sleep(300); // the accessibility tree follows the page a moment later
            var read = FocusedEditableDetector.TextBeforeCaretOf(fields[field]);
            var spaced = read is not null && InsertionTextFormatter.Apply("hello", InsertionTextFormatter.VisibleTail(read)).StartsWith(' ');
            if (read is null || spaced != space)
            {
                failures.Add($"{state}: read {Describe(read)}, {(spaced ? "space" : "no space")}; expected {(space ? "a space" : "no space")}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Describe(string? read) =>
        read is null ? "nothing (the memory would decide)" : read.Length == 0 ? "\"\"" : string.Join(' ', read.Select(c => char.IsLetterOrDigit(c) ? c.ToString() : $"U+{(int)c:X4}"));

    private static T? Poll<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (probe() is { } found)
                {
                    return found;
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // The page is still loading: UI Automation may fail until it has.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(50);
        }
    }
}

/// <summary>A fact that needs Chrome or Edge: skipped where neither is installed.</summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
    public ChromiumFactAttribute()
    {
        if (ChromiumCaretReadTests.BrowserPath() is null)
        {
            Skip = "Needs Chrome or Edge";
        }
    }
}
