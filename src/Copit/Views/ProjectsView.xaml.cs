using Copit.ViewModels;

namespace Copit.Views;

public partial class ProjectsView : ContentView
{
    public ProjectsView(ProjectsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}

public partial class ProjectEditorOverlay : ContentView
{
    public ProjectEditorOverlay() => InitializeComponent();

    private void OnEpicUnfocused(object? sender, FocusEventArgs e)
    {
        if (BindingContext is ProjectEditor ed && ed.CheckEpicCommand.CanExecute(null)) ed.CheckEpicCommand.Execute(null);
    }
}
