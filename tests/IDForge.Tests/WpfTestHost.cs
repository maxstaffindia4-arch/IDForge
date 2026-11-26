using System.Windows;
using IDForge;

namespace IDForge.Tests;

/// <summary>
/// WPF allows exactly one Application per AppDomain. All window tests share
/// this host so App resources are initialized exactly once.
/// </summary>
public static class WpfTestHost
{
    private static readonly object Gate = new();
    private static bool _ready;

    public static void EnsureApp()
    {
        lock (Gate)
        {
            if (_ready) return;
            if (Application.Current is not App)
            {
                var app = new App();
                app.InitializeComponent();   // merges all theme dictionaries
                // Tests share one Application on the WpfSta dispatcher; closing
                // the last window must not shut it down between tests.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            // Software-only rendering: hardware-accelerated composition can
            // wedge the shared render thread on RDP/virtual displays, starving
            // every later test on the WpfSta dispatcher.
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
            MainWindow.SkipResumePrompt = true;
            MainWindow.SkipDraftPhotoRestore = true;
            _ready = true;
        }
    }
}
