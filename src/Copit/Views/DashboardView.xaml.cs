using Copit.ViewModels;

namespace Copit.Views;

public partial class DashboardView : ContentView
{
    public DashboardView(DashboardViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
