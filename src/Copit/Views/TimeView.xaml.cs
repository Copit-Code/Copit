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

    private void OnQuestionOption(object? sender, EventArgs e)
    {
        if (sender is not Button b || BindingContext is not ViewModels.TimeViewModel vm) return;
        var q = b.Parent?.BindingContext as ViewModels.TimeQuestionVM ?? FindQuestion(b);
        if (q != null) vm.AnswerQuestionCommand.Execute(new object[] { q, b.Text });
    }

    private static ViewModels.TimeQuestionVM? FindQuestion(Element? e)
    {
        for (; e != null; e = e.Parent) if (e.BindingContext is ViewModels.TimeQuestionVM q) return q;
        return null;
    }
}
