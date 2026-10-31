using System.Windows;
using System.Windows.Threading;
using Talktrace.Core.Logging;

namespace Talktrace.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Start("app");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Talktrace stopped because of an unexpected error.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("An unexpected error in a background task.", args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Talktrace closed.");
        base.OnExit(e);
    }

    /// <summary>Shows errors nothing else caught in the window, which stays open, rather than closing without a word.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.ShowUnexpectedError(e.Exception);
            e.Handled = true;
        }
        else
        {
            Log.Error("Talktrace couldn't start because of an unexpected error.", e.Exception);
        }
    }
}
