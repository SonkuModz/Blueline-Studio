using System.Windows;
using System.Windows.Threading;
using Blueline.App.Shell;
using Blueline.Core;

namespace Blueline.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        var services = new AppServices(new Workspace(new SessionLog()), settings);
        var options = StartOptions.Parse(e.Args);

        // The screenshot tour needs every page to be there at once, so it starts with movement off.
        Motion.Reduce = settings.ReduceMotion;
        Motion.ForceInstant = options.CaptureFolder != null;
        ThemeManager.Apply(UiPalette.Named(settings.Theme));

        DispatcherUnhandledException += (_, args) =>
        {
            services.Log.Error("Unhandled error", args.Exception);
            services.ReportError("Something went wrong", args.Exception);
            args.Handled = true;
        };

        var window = new MainWindow(services);
        MainWindow = window;
        if (options.CaptureFolder != null)
        {
            // Offscreen so a screenshot tour neither needs nor steals the user's screen.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Width = 1600;
            window.Height = 950;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        window.Show();
        window.Begin(options);
    }
}

public sealed record StartOptions(string? Game, string? Project, string? CaptureFolder, string? Emulator, bool CaptureLaunch)
{
    public static StartOptions Parse(string[] args)
    {
        string? game = null, project = null, capture = null, emulator = null;
        var launch = false;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--game": game = args[i + 1]; break;
                case "--project": project = args[i + 1]; break;
                case "--capture": capture = args[i + 1]; break;
                case "--emulator": emulator = args[i + 1]; break;
                // Also presses Play during the tour, which opens the game window for a short time.
                case "--capture-launch": launch = args[i + 1] == "true"; break;
            }
        }
        return new StartOptions(game, project, capture, emulator, launch);
    }
}
