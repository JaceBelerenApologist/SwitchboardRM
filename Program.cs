using System.Windows.Forms;

namespace SwitchboardRM;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // per-monitor scaling: each window is drawn natively at its screen's scaling, never stretched by Windows (stretching
        // left maximized windows a pixel short of the screen edges and blurred text on screens scaled differently)
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
        {
            Log.Write("Unhandled error: " + e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log folder (File > Open log folder).",
                "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        // one copy only (Settings > Appearance > Window, on by default): a second start brings the open window forward
        bool afterUpdate = Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
        if (!SingleInstance.Claim(afterUpdate) && AppSettings.Load().SingleInstance)
        {
            SingleInstance.ActivateRunningCopy();
            return;
        }
        SingleInstance.Listen();

        Migration.Run(); // 1.x (AWS RDP Launcher) -> 2.0 settings and passwords, once
        FontLoader.LoadAll(); // fonts\ next to the .exe, for this app only

        // SSH terminals: tell remote programs they're on an xterm-compatible, 256-colour terminal
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        bool lastRunCrashed = RunMarker.Begin();
        Application.Run(new StartupContext(lastRunCrashed));
        RunMarker.End();   // only reached on a clean exit
    }
}

/// <summary>Shows the splash, gets the main window ready behind it, then swaps one for the other.</summary>
internal sealed class StartupContext : ApplicationContext
{
    private readonly SplashForm _splash;
    private readonly MainForm _main;

    private readonly bool _lastRunCrashed;

    public StartupContext(bool lastRunCrashed)
    {
        _lastRunCrashed = lastRunCrashed;
        var look = AppSettings.PeekAppearance();
        Theme.Configure(new AppSettings { AccentColor = look.Accent }); // the splash in the chosen accent colour
        _splash = new SplashForm(look.Dark);
        _splash.Show();
        _splash.Refresh(); // paint now, before the main window is built

        Log.Cleanup();     // housekeeping waits until the splash is on screen

        _main = new MainForm();
        _main.FormClosed += (_, _) => ExitThread();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        var result = StartupResult.Ready;
        if (_lastRunCrashed) await CleanUpAfterCrashAsync();
        try
        {
            result = await _main.PrepareAsync(_splash.SetStatus);
        }
        catch (Exception ex)
        {
            Log.Write("Startup failed: " + ex);   // show the window anyway; Refresh will report the problem
        }

        _main.Show();
        _main.Activate();
        _splash.Close();

        if (result == StartupResult.NeedsSignIn)
        {
            // "Keep me signed in" off: don't open the sign-in by ourselves; connecting to an AWS server will
            if (_main.KeepSignedIn) await _main.SignInAsync();
            else _main.ShowSignedOutNotice();
        }
        // the tabs from last time (AWS ones follow once signed in and the list has loaded)
        await _main.RestoreTabsAsync();
    }

    /// <summary>
    /// The last run didn't close cleanly, so its sign-out on close never happened: remove the AWS keys it left behind
    /// (when "Sign out of AWS when Switchboard closes" is on). Its tunnels and shells already ended with it.
    /// </summary>
    private async Task CleanUpAfterCrashAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            if (!settings.SignOutOnClose || string.IsNullOrWhiteSpace(settings.Profile))
            {
                Log.Write("The last run didn't close cleanly (sign-out on close is off, so nothing to clean up)");
                return;
            }
            _splash.SetStatus("Cleaning up after the last session ...");
            Log.Write("The last run didn't close cleanly: " + await Aws.SignOutAsync(settings.Profile));
        }
        catch (Exception ex) { Log.Write("Clean-up after the last run failed: " + ex.Message); }
    }
}
