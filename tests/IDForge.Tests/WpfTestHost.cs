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
            }
            MainWindow.SkipResumePrompt = true;
            MainWindow.SkipDraftPhotoRestore = true;
            _ready = true;
        }
    }
}
