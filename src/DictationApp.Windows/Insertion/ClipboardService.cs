using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Insertion;

/// <summary>What we put back after a paste. Only formats we can round-trip losslessly are captured.</summary>
public sealed class ClipboardSnapshot
{
    public Dictionary<string, object> Data { get; } = [];

    public bool IsEmpty => Data.Count == 0;
}

/// <summary>
/// All clipboard access runs on a fresh STA thread (WPF's Clipboard requires STA) with retries, because
/// another process may briefly hold the clipboard open and the Win32 call then fails with
/// CLIPBRD_E_CANT_OPEN.
/// </summary>
public sealed class ClipboardService : IClipboard
{
    private static readonly string[] RoundTripFormats =
    [
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Html, DataFormats.Rtf, DataFormats.Dib, DataFormats.FileDrop,
    ];

    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(ILogger<ClipboardService> logger)
    {
        _logger = logger;
    }

    public Task SetTextAsync(string text, CancellationToken ct = default) =>
        RunStaAsync(() =>
        {
            Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));
            return true;
        }, ct);

    public Task<string?> GetTextAsync(CancellationToken ct = default) =>
        RunStaAsync(() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null), ct);

    public Task<ClipboardSnapshot> SnapshotAsync(CancellationToken ct = default) =>
        RunStaAsync(() =>
        {
            var snapshot = new ClipboardSnapshot();
            try
            {
                var data = Retry(Clipboard.GetDataObject);
                if (data is null)
                {
                    return snapshot;
                }

                foreach (var format in RoundTripFormats)
                {
                    if (!data.GetDataPresent(format))
                    {
                        continue;
                    }

                    try
                    {
                        var value = data.GetData(format);
                        if (value is MemoryStream ms)
                        {
                            value = ms.ToArray();
                        }

                        if (value is not null)
                        {
                            snapshot.Data[format] = value;
                        }
                    }
                    catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
                    {
                        _logger.LogDebug(ex, "Could not snapshot clipboard format {Format}", format);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                _logger.LogDebug(ex, "Clipboard snapshot failed");
            }

            return snapshot;
        }, ct);

    public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken ct = default) =>
        RunStaAsync(() =>
        {
            if (snapshot.IsEmpty)
            {
                Retry(Clipboard.Clear);
                return true;
            }

            var data = new DataObject();
            foreach (var (format, value) in snapshot.Data)
            {
                data.SetData(format, value is byte[] bytes ? new MemoryStream(bytes) : value);
            }

            Retry(() => Clipboard.SetDataObject(data, copy: true));
            return true;
        }, ct);

    private static T Retry<T>(Func<T> action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                last = ex;
                Thread.Sleep(20 + attempt * 10);
            }
        }

        throw last ?? new InvalidOperationException("Clipboard unavailable");
    }

    private static void Retry(Action action) => Retry(() =>
    {
        action();
        return true;
    });

    private static Task<T> RunStaAsync<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "ClipboardSTA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
