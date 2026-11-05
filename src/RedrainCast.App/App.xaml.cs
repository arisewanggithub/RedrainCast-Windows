using Microsoft.UI.Xaml;

namespace RedrainCast.App;

public partial class App : Application
{
    private MainWindow? window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var testIndex = Array.IndexOf(arguments, "--self-test");
        var mediaIndex = Array.IndexOf(arguments, "--media-self-test");
        var colorIndex = Array.IndexOf(arguments, "--color-self-test");
        if (colorIndex >= 0)
        {
            if (colorIndex + 1 >= arguments.Length) throw new ArgumentException("--color-self-test requires a report path.");
            await MainWindow.RunColorSelfTestAsync(arguments[colorIndex + 1]);
            Exit();
            return;
        }
        window = new MainWindow(testIndex >= 0 || mediaIndex >= 0);
        window.Activate();
        await window.InitializeAsync();
        if (mediaIndex >= 0)
        {
            if (mediaIndex + 2 >= arguments.Length) throw new ArgumentException("--media-self-test requires a synthetic fixture and a report path.");
            await window.RunMediaSelfTestAsync(arguments[mediaIndex + 1], arguments[mediaIndex + 2]);
            window.Close();
        }
        else if (testIndex >= 0)
        {
            if (testIndex + 1 >= arguments.Length) throw new ArgumentException("--self-test requires a report path.");
            await window.RunSelfTestAsync(arguments[testIndex + 1]);
            window.Close();
        }
    }
}
