using System.Globalization;
using Copit.Services;
using Copit.ViewModels;

namespace Copit.Views;

public class SameConverter : IMultiValueConverter
{
    public static readonly SameConverter Instance = new();
    public object Convert(object[] values, Type t, object? p, CultureInfo c) => values.Length == 2 && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public partial class SessionView : ContentView, IActivatable
{
    private readonly SessionViewModel _vm;
    private bool _stick = true;

    public SessionView(SessionViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.ScrollToEndRequested += () => { _stick = true; ScrollToEnd(); };
        InputEditor.SendRequested += (_, _) => { if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null); };
        ChatScroll.Scrolled += (_, e) =>
        {
            var max = ChatScroll.ContentSize.Height - ChatScroll.Height;
            _stick = e.ScrollY >= max - 80;
        };
        ChatStack.SizeChanged += (_, _) => { if (_stick) ScrollToEnd(); };
    }

    private void ScrollToEnd()
    {
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(30);
            try { await ChatScroll.ScrollToAsync(0, Math.Max(0, ChatStack.Height), false); } catch { }
        });
    }

    public void OnActivated(object? parameter)
    {
        _vm.OnActivated(parameter);
        Dispatcher.Dispatch(() => InputEditor.Focus());
    }
}

public partial class GitPanel : ContentView
{
    public GitPanel() => InitializeComponent();
}

public partial class GitOverlays : ContentView
{
    public GitOverlays() => InitializeComponent();
}
