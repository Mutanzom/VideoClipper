using Microsoft.UI.Xaml;

namespace VideoClipper.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the app alive and show the error instead of vanishing.
            e.Handled = true;
            MainWindow?.ShowError("Unexpected error", e.Exception?.Message ?? e.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(Environment.GetCommandLineArgs().Skip(1).ToArray());
        MainWindow.Activate();
    }
}
