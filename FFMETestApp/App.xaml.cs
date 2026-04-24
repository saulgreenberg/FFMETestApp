using System.Diagnostics;
using System.IO;
using System.Windows;
using Unosquare.FFME;
using IOPath = System.IO.Path;

namespace FFMETestApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Debug.Print($"[UnobservedTaskException] {e.Exception?.Message}");
            e.SetObserved();
        };

        // Must be set before any MediaElement is created (i.e. before base.OnStartup opens the window)
        string ffmpegPath = IOPath.Combine(AppContext.BaseDirectory, "ffmpegbin");
        Library.FFmpegDirectory = ffmpegPath;

        base.OnStartup(e);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogException(e.ExceptionObject as Exception, "Unhandled Domain Exception");
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception, "Unhandled UI Exception");
        e.Handled = true;
    }

    private void LogException(Exception? ex, string context)
    {
        if (ex == null) return;
        string msg = $"{context}:\n\nMessage: {ex.Message}\nType: {ex.GetType().Name}\nStack: {ex.StackTrace}";
        if (ex.InnerException != null)
            msg += $"\n\nInner Exception: {ex.InnerException.Message}";
        MessageBox.Show(msg, "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
