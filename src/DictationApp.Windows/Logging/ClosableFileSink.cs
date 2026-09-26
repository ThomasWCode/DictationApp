using DictationApp.Core.Abstractions;
using Serilog.Core;
using Serilog.Events;

namespace DictationApp.Windows.Logging;

/// <summary>
/// The rolling log file behind a switch: <see cref="WhileClosed{T}"/> closes it, runs the action (deleting the log
/// files) and opens a fresh one. Serilog's shared file sink appends to a file it opened in append mode and cannot seek
/// below the length the file had then, so emptying the open file from outside made every later event fail until the
/// next start. Events written while the file is closed are dropped.
/// </summary>
public sealed class ClosableFileSink(Func<Logger> open) : ILogEventSink, ILogFileControl, IDisposable
{
    private readonly object _gate = new();
    private Logger? _file = open();

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _file?.Write(logEvent);
        }
    }

    public T WhileClosed<T>(Func<T> action)
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
            try
            {
                return action();
            }
            finally
            {
                _file = open();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
