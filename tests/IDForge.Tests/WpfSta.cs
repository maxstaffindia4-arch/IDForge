using System.Windows.Threading;

namespace IDForge.Tests;

/// <summary>Single long-lived STA dispatcher for rendered WPF tests. Windows
/// only perform layout on the dispatcher that owns the Application, so every
/// rendered test must share ONE thread — otherwise whichever test runs first
/// wins and later windows stay at 0×0.</summary>
internal static class WpfSta
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher Disp
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher != null) return _dispatcher;
                var ready = new ManualResetEventSlim();
                Exception? startError = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        ready.Set();
                        Dispatcher.Run();
                    }
                    catch (Exception ex) { startError = ex; ready.Set(); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                if (!ready.Wait(TimeSpan.FromSeconds(30)) || startError != null)
                    throw new InvalidOperationException("WpfSta dispatcher did not start", startError);
                return _dispatcher;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the shared STA dispatcher and
    /// rethrows any exception on the calling test thread.</summary>
    public static void Run(Action work)
    {
        Exception? error = null;
        var done = new ManualResetEventSlim();
        Disp.BeginInvoke(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(180)), "WPF test work timed out");
        Assert.Null(error);
    }

    public static T Run<T>(Func<T> work)
    {
        T? result = default;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        Disp.BeginInvoke(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(180)), "WPF test work timed out");
        Assert.Null(error);
        return result!;
    }

    public static async Task RunAsync(Func<Task> work)
    {
        Exception? error = null;
        var done = new ManualResetEventSlim();
        Disp.BeginInvoke(async () =>
        {
            try { await work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        await Task.Run(() => Assert.True(done.Wait(TimeSpan.FromSeconds(180)),
            "WPF test work timed out"));
        Assert.Null(error);
    }
}
