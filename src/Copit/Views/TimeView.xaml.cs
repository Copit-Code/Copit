using Copit.ViewModels;

namespace Copit.Views;

public partial class TimeView : ContentView
{
    private readonly TimeViewModel _vm;

    public TimeView(TimeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.Celebrate += () => Confetti.Burst();
    }

    private void OnTargetUnfocused(object? sender, FocusEventArgs e)
    {
        if (_vm.SaveTargetCommand.CanExecute(null)) _vm.SaveTargetCommand.Execute(null);
    }
}
