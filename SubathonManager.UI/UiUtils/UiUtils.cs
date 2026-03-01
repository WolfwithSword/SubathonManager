using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Controls;
namespace SubathonManager.UI.UiUtils;

public static class UiUtils
{
    public static async Task<bool> TrySetClipboardTextAsync(string text)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return false;
        
        const int retries = 5;
        const int delayMs = 50;
        for (int i = 0; i < retries; i++)
        {
            try
            {
                if (dispatcher.CheckAccess())
                {
                    Clipboard.SetText(text);
                }
                else
                {
                    dispatcher.Invoke(() => Clipboard.SetText(text));
                }
                return true;
            }
            catch (COMException ex) // when ((uint)ex.ErrorCode == 0x800401D0)
            {
                await Task.Delay(delayMs);
            }
        }
        
        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Copy Dialogue"
        };
        var textBlock = new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Width = 320,
            Margin = new Thickness(4,4,4,12)
        };
        textBlock.Inlines.Add("There was an issue copying to your clipboard. Please copy this manually.");
        
        msgBox.Owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive);
        msgBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var textbx = new Wpf.Ui.Controls.TextBox
        {
            Text = text,
            Width = 340,
            MaxHeight = 300,
            Margin = new Thickness(2, 8, 4, 4),
            IsReadOnly = true
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(textBlock);
        panel.Children.Add(textbx);
        
        msgBox.Content = panel;
        
        await msgBox.ShowDialogAsync();
        return false;
    }
}