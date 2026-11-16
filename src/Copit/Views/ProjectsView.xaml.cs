using Copit.ViewModels;

namespace Copit.Views;

public partial class ProjectsView : ContentView
{
    private readonly ProjectsViewModel _vm;

    public ProjectsView(ProjectsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    // ------------------------------------------------------------ Drag & Drop: Projekt → Ordner
    private void OnProjectDragStarting(object? sender, DragStartingEventArgs e)
    {
        if (Ctx(sender) is ProjectListRow { Card: { } c })
        {
            e.Data.Properties["project"] = c.Project.Id;
            e.Data.Text = c.Project.Name;
        }
    }

    // Gesten-Erkenner erben den BindingContext ihrer Zeile; Parent ist nicht auf allen Plattformen gesetzt
    private static object? Ctx(object? sender) => (sender as BindableObject)?.BindingContext ?? ((sender as Element)?.Parent as BindableObject)?.BindingContext;
    private static VisualElement? Target(object? sender) => (sender as Element)?.Parent as VisualElement;

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (Target(sender) is { } t) t.Opacity = 0.65;
    }

    private void OnFolderDragLeave(object? sender, DragEventArgs e)
    {
        if (Target(sender) is { } t) t.Opacity = 1;
    }

    private async void OnFolderDrop(object? sender, DropEventArgs e)
    {
        var target = Target(sender);
        if (target != null) target.Opacity = 1;
        if (!e.Data.Properties.TryGetValue("project", out var id) || id is not string pid) return;
        var project = _vm.FindProject(pid);
        if (project == null) return;
        var folder = Ctx(sender) is ProjectListRow { IsFolder: true } row ? row.FolderPath : "";
        if (project.Group == folder) return;
        await _vm.MoveToAsync(project, folder);
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
