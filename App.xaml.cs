using Microsoft.UI.Xaml;

namespace GameSavePreview;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.Directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Directory, "errors.log"),
                    DateTimeOffset.Now.ToString("O") + " " + e.Exception + Environment.NewLine);
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs();
        if (command.Length == 3 && command[1] == "--self-test")
        {
            Environment.Exit(Verification.Run(command[2]));
            return;
        }
        LocalFiles.RemoveLegacyAutostart();
        window = new MainWindow();
        window.Activate();
        ((MainWindow)window).SetSize();
    }
}
