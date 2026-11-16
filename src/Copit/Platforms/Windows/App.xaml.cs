namespace Copit.WinUI;

/// <summary>WinUI-Einstiegspunkt.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Copit.App.LogCrash(e.Exception);
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "copit-crash.log"), $"[{DateTime.Now:O}] {e.Exception}\n{e.Message}\n\n"); } catch { }
        };
    }

    protected override MauiApp CreateMauiApp()
    {
        try { return MauiProgram.CreateMauiApp(); }
        catch (Exception e)
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "copit-crash.log"), $"[{DateTime.Now:O}] CreateMauiApp: {e}\n\n");
            throw;
        }
    }
}
