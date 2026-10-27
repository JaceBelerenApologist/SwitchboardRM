using System.Windows.Forms;

namespace SwitchboardRM;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
        {
            Log.Write("Unhandled error: " + e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log folder (File > Open log folder).",
                "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Migration.Run(); // 1.x (AWS RDP Launcher) -> 2.0 settings and passwords, once

        // SSH terminals: tell remote programs they're on an xterm-compatible, 256-colour terminal
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Application.Run(new StartupContext());
    }
}

/// <summary>Shows the splash, gets the main window ready behind it, then swaps one for the other.</summary>
internal sealed class StartupContext : ApplicationContext
{
    private readonly SplashForm _splash;
    private readonly MainForm _main;

    public StartupContext()
    {
        _splash = new SplashForm(AppSettings.PeekDarkMode());
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

        if (result != StartupResult.NeedsSignIn) return;
        // "Keep me signed in" off: don't open the sign-in by ourselves; connecting to an AWS server will
        if (_main.KeepSignedIn) await _main.SignInAsync();
        else _main.ShowSignedOutNotice();
    }
}
