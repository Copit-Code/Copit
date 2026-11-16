using Copit.ViewModels;

namespace Copit.Views;

public partial class ToolsView : ContentView
{
    public ToolsView(ToolsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
