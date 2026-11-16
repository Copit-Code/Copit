using Copit.ViewModels;

namespace Copit.Views;

public partial class SettingsView : ContentView
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
