using Copit.Services;

namespace Copit.Views;

public class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? System { get; set; }
    public DataTemplate? Error { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        MessageVM { IsUser: true } => User!,
        MessageVM { IsAssistant: true } => Assistant!,
        MessageVM { IsTool: true } => Tool!,
        MessageVM { IsErrorMessage: true } => Error!,
        _ => System!
    };
}
