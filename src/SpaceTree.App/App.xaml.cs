using System.Windows;
using System.Windows.Threading;
using SpaceTree.App.Services;
using SpaceTree.App.ViewModels;
using SpaceTree.App.Views;

namespace SpaceTree.App;

public partial class App : Application
{
    public static SettingsService Settings { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load before the first window is created so the theme and the saved
        // window placement are applied on the very first frame, with no flash.
        Settings.Load();
        ThemeManager.Apply(Settings.Current.Theme);

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception);

        var viewModel = new MainViewModel(Settings);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // A path on the command line means "scan this now", which is what makes
        // "Send to > SpaceTree" and shortcut targets useful.
        string? initialPath = e.Args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        if (!string.IsNullOrWhiteSpace(initialPath))
            viewModel.StartScan(initialPath);
        else if (Settings.Current.RescanLastPathOnStart && !string.IsNullOrWhiteSpace(Settings.Current.LastScanPath))
            viewModel.SelectedPath = Settings.Current.LastScanPath!;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);

        var result = MessageBox.Show(
            $"SpaceTree hit an unexpected error and may be in an inconsistent state.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"A log was written to:\n{CrashLog.LogPath}\n\nContinue running?",
            "SpaceTree", MessageBoxButton.YesNo, MessageBoxImage.Error);

        // Keeping the process alive after an unhandled UI exception is a judgement
        // call; a scan result the user has not exported yet is worth the risk.
        e.Handled = result == MessageBoxResult.Yes;
        if (!e.Handled)
            Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        base.OnExit(e);
    }
}
