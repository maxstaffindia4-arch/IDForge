using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace IDForge;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    internal static void LogCrash(Exception ex)
    {
        try
        {
            var path = IDForge.Core.AppPaths.CrashLogPath;
            File.AppendAllText(path,
                $"{DateTime.Now:O}\n{ex}\n\n");
            MessageBox.Show($"Error: {ex.Message}\n\nDetails in crash.log", "IDForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
