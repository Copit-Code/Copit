using Microsoft.Maui.Handlers;

namespace Copit.Controls;

/// <summary>Editor, der bei Enter sendet (Shift+Enter = neue Zeile).</summary>
public class ChatEditor : Editor
{
    public event EventHandler? SendRequested;
    public void RaiseSend() => SendRequested?.Invoke(this, EventArgs.Empty);
}

public static class ChatEditorHandler
{
    public static void Register()
    {
        EditorHandler.Mapper.AppendToMapping("CopitSendOnEnter", (handler, view) =>
        {
#if WINDOWS
            if (view is not ChatEditor chat) return;
            var tb = handler.PlatformView;
            tb.PreviewKeyDown -= OnKey;
            tb.PreviewKeyDown += OnKey;

            void OnKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (shift) return;
                e.Handled = true;
                chat.RaiseSend();
            }
#endif
        });
    }
}
