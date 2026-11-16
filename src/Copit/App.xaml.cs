using Copit.Core.Services;

namespace Copit;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
        if (Environment.GetEnvironmentVariable("COPIT_DEBUG") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "copit-first.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n"); } catch { }
            };
    }

    public static void LogCrash(Exception? e)
    {
        if (e == null) return;
        try { File.AppendAllText(Core.Storage.AppPaths.File("crash.log"), $"[{DateTime.Now:O}] {e}\n\n"); } catch { }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = _services.GetRequiredService<MainPage>();
        var window = new Window(page)
        {
            Title = "Copit",
            TitleBar = new TitleBar
            {
                Title = "COPIT",
                BackgroundColor = Color.FromArgb("#14110F"),
                ForegroundColor = Color.FromArgb("#7A6F65"),
                HeightRequest = 32
            }
        };
        window.Destroying += (_, _) =>
        {
            _services.GetRequiredService<AutomationService>().Stop();
            _services.GetRequiredService<Services.SessionManager>().SaveAll();
        };
        return window;
    }
}
