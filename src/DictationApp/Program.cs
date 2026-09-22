using System.Threading;
using DictationApp.Cli;
using Velopack;

namespace DictationApp;

public static class Program
{
    private const string MutexName = @"Local\DictationApp.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build().Run();

        var options = CommandLineOptions.Parse(args);
        if (options.IsCliMode)
        {
            return ConsoleRunner.Run(options);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: the tray icon of the first instance is the UI. Nothing else to do.
            return 0;
        }

        var app = new App(options);
        app.InitializeComponent();
        return app.Run();
    }
}
