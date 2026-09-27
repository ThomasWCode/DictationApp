using System.IO;
using DictationApp.Windows.Logging;
using Serilog;

namespace DictationApp.Windows.Tests;

/// <summary>"Delete all history" deletes the log files while the app keeps logging to a fresh one.</summary>
public sealed class ClosableFileSinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("DictationAppLogs").FullName;

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

    private ClosableFileSink Sink() => new(() => new LoggerConfiguration()
        .WriteTo.File(Path.Combine(_dir, "dictation-.log"), rollingInterval: RollingInterval.Day, shared: true, outputTemplate: "{Message}{NewLine}")
        .CreateLogger());

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void The_logs_can_be_deleted_while_logging_goes_on_into_a_new_file()
    {
        using var sink = Sink();
        using var log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        log.Information("before");

        var left = sink.WhileClosed(() =>
        {
            foreach (var file in Directory.GetFiles(_dir))
            {
                File.Delete(file); // throws if the logger still held the file
            }

            return Directory.GetFiles(_dir).Length;
        });
        log.Information("after");

        Assert.Equal(0, left);
        var text = ReadShared(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Contains("after", text);
        Assert.DoesNotContain("before", text);
    }
}
