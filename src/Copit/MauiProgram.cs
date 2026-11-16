using Copit.Core.Ai;
using Copit.Core.Services;
using Copit.Services;
using Copit.ViewModels;
using Copit.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace Copit;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.ConfigureMauiHandlers(_ =>
        {
            Controls.ChatEditorHandler.Register();
#if WINDOWS
            // WinUI-CheckBox hat standardmäßig MinWidth=120 → Labels rutschen weit nach rechts
            Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("CopitCompact", (h, _) => h.PlatformView.MinWidth = 0);
            // Eckige Eingabefelder mit 1px-Rahmen und Akzent beim Fokus
            static void Square(Microsoft.UI.Xaml.Controls.Control c)
            {
                static Microsoft.UI.Xaml.Media.SolidColorBrush B(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
                void Set(string key, object value)
                {
                    try { if (c.Resources.ContainsKey(key)) c.Resources.Remove(key); c.Resources.Add(key, value); } catch { }
                }
                c.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                Set("TextControlBorderBrush", B(0x3A, 0x32, 0x2B));
                Set("TextControlBorderBrushPointerOver", B(0x4A, 0x40, 0x37));
                Set("TextControlBorderBrushFocused", B(0xE8, 0x74, 0x3B));
                Set("TextControlBackgroundFocused", B(0x18, 0x14, 0x12));
                Set("TextControlBackgroundPointerOver", B(0x18, 0x14, 0x12));
                c.BorderThickness = new Microsoft.UI.Xaml.Thickness(1);
            }
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("CopitSquare", (h, _) => Square(h.PlatformView));
            Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("CopitSquare", (h, v) =>
            {
                Square(h.PlatformView);
                if (v is Controls.ChatEditor)
                {
                    // Eingabe im Chat: Rahmen kommt vom umgebenden Feld
                    var none = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    foreach (var k in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" })
                    {
                        try { h.PlatformView.Resources.Remove(k); h.PlatformView.Resources.Add(k, none); } catch { }
                    }
                    h.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                }
            });
            Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("CopitSquare", (h, _) => h.PlatformView.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0));
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("CopitSquare", (h, _) => h.PlatformView.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0));
#endif
        });

#if WINDOWS
        builder.ConfigureLifecycleEvents(events => events.AddWindows(w => w.OnWindowCreated(window =>
        {
            window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            var appWindow = window.AppWindow;
            appWindow.Resize(new Windows.Graphics.SizeInt32(1560, 960));
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Maximize();
        })));
#endif

        var s = builder.Services;
        // Core
        s.AddSingleton<ISecretProtector, DpapiProtector>();
        s.AddSingleton<SettingsService>();
        s.AddSingleton<DataService>();
        s.AddSingleton<ConnectionHub>();
        s.AddSingleton<GitService>();
        s.AddSingleton<ClaudeCliRunner>();
        s.AddSingleton<CopilotCliRunner>();
        s.AddSingleton<AgentRunnerFactory>();
        s.AddSingleton<AiService>();
        s.AddSingleton<TimeBookingService>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton<AssistService>();
        s.AddSingleton<AutomationService>();
        s.AddSingleton<PermissionService>();
        s.AddSingleton<UpdateService>();
        s.AddSingleton<ExtensionService>();
        s.AddSingleton<DocsService>();
        s.AddSingleton<TicketChangesService>();
        s.AddSingleton<ActivityService>(_ => new ActivityService());
        // App
        s.AddSingleton<NavigationService>();
        s.AddSingleton<ToastService>();
        s.AddSingleton<SessionManager>();
        s.AddTransient<GitPanelViewModel>();
        // ViewModels
        s.AddSingleton<ShellViewModel>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<TimeViewModel>();
        s.AddSingleton<ProjectsViewModel>();
        s.AddSingleton<ProjectDetailViewModel>();
        s.AddSingleton<SessionViewModel>();
        s.AddSingleton<ToolsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        // Views
        s.AddSingleton<MainPage>();
        s.AddSingleton<DashboardView>();
        s.AddSingleton<TimeView>();
        s.AddSingleton<ProjectsView>();
        s.AddSingleton<ProjectDetailView>();
        s.AddSingleton<SessionView>();
        s.AddSingleton<ToolsView>();
        s.AddSingleton<SettingsView>();
        s.AddSingleton<BoardView>();
        s.AddSingleton<ActivityView>();
        s.AddSingleton<BudgetsView>();
        s.AddSingleton<AutomationsView>();
        s.AddSingleton<CustomizeView>();
        s.AddSingleton<DocsView>();
        s.AddSingleton<ChangesView>();
        s.AddSingleton<ExtensionsView>();

        return builder.Build();
    }
}
