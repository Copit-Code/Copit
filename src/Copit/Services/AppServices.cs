using System.Security.Cryptography;
using Copit.Core.Services;

namespace Copit.Services;

/// <summary>Windows DPAPI: Tokens werden nur für den aktuellen Windows-Benutzer lesbar gespeichert.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

public interface IActivatable
{
    void OnActivated(object? parameter);
}

public static class Routes
{
    public const string Home = "home";
    public const string Time = "time";
    public const string Projects = "projects";
    public const string Project = "project";
    public const string Session = "session";
    public const string Tools = "tools";
    public const string Settings = "settings";
    public const string Board = "board";
    public const string Activity = "activity";
    public const string Budgets = "budgets";
    public const string Automations = "automations";
    public const string Customize = "customize";
    public const string Docs = "docs";
    public const string Extensions = "extensions";
    public const string Changes = "changes";
    public const string Knowledge = "knowledge";
}

/// <summary>Einfache View-Navigation innerhalb des Hauptfensters (Views bleiben im Speicher → Zustand bleibt erhalten).</summary>
public class NavigationService
{
    public event Action<string, object?>? Navigated;
    public string Current { get; private set; } = Routes.Home;

    public void Go(string route, object? parameter = null)
    {
        Current = route;
        MainThread.BeginInvokeOnMainThread(() => Navigated?.Invoke(route, parameter));
    }
}

public record Toast(string Title, string Message, NoticeLevel Level, string? Action, string? ActionArg);

public class ToastService
{
    public event Action<Toast>? Shown;

    public void Show(string title, string message = "", NoticeLevel level = NoticeLevel.Info, string? action = null, string? arg = null)
        => MainThread.BeginInvokeOnMainThread(() => Shown?.Invoke(new Toast(title, message, level, action, arg)));

    public void Error(Exception e, string title = "Fehler") => Show(title, e.Message, NoticeLevel.Error);
    public void Success(string title, string message = "") => Show(title, message, NoticeLevel.Success);
}

/// <summary>Dialoge (Alert, Prompt, Auswahl) auf dem Hauptfenster.</summary>
public static class Dialogs
{
    private static Page Page => Application.Current!.Windows[0].Page!;

    // Eigene Dialoge im Copit-Stil (Fallback: Windows-Standard, solange die Oberfläche noch nicht steht)
    public static Task Alert(string title, string message) => Copit.Controls.Overlay.Host != null ? Copit.Controls.Overlay.Alert(title, message) : Page.DisplayAlert(title, message, "OK");
    public static Task<bool> Confirm(string title, string message, string ok = "Ja", string cancel = "Abbrechen") =>
        Copit.Controls.Overlay.Host != null ? Copit.Controls.Overlay.Confirm(title, message, ok, cancel) : Page.DisplayAlert(title, message, ok, cancel);
    public static Task<string?> Prompt(string title, string message, string initial = "", string placeholder = "", Keyboard? keyboard = null)
        => Copit.Controls.Overlay.Host != null ? Copit.Controls.Overlay.Prompt(title, message, initial, placeholder)
            : Page.DisplayPromptAsync(title, message, "OK", "Abbrechen", placeholder, -1, keyboard ?? Keyboard.Text, initial);
    public static Task<string?> Choose(string title, params string[] options) =>
        Copit.Controls.Overlay.Host != null ? Copit.Controls.Overlay.Choose(title, options) : Page.DisplayActionSheet(title, "Abbrechen", null, options);
    /// <summary>Dropdown direkt unter einem Element.</summary>
    public static Task<string?> ChooseAt(VisualElement anchor, string? selected, params string[] options) => Copit.Controls.Overlay.Choose("", options, anchor, selected);

    public static async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
#else
        return await Prompt("Ordner", "Pfad zum Quellcode");
#endif
    }

    public static async Task OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { await Launcher.OpenAsync(new Uri(url)); } catch { }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    /// <summary>Datei im Editor an einer Zeile öffnen (VS Code/VSCodium: code -g datei:zeile), sonst mit dem Standardprogramm.</summary>
    public static void OpenFileAt(string file, int line)
    {
        try
        {
            var code = CliLocator.Resolve("code");
            if (code != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(code.FileName) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in code.PrefixArgs) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add($"{file}:{Math.Max(1, line)}");
                System.Diagnostics.Process.Start(psi);
                return;
            }
            if (File.Exists(file)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch { }
    }

    public static void OpenInEditor(string folder)
    {
        try
        {
            var code = CliLocator.Resolve("code");
            if (code != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(code.FileName) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in code.PrefixArgs) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(folder);
                System.Diagnostics.Process.Start(psi);
                return;
            }
        }
        catch { }
        OpenFolder(folder);
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }
}
