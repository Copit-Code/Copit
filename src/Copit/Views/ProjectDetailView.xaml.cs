using System.Globalization;
using Copit.ViewModels;

namespace Copit.Views;

public class HoursConverter : IValueConverter
{
    public static readonly HoursConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is long s && s > 0 ? $"{s / 3600.0:0.#}h" : "–";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public partial class ProjectDetailView : ContentView
{
    public ProjectDetailView(ProjectDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
