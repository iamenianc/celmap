using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CelMap.App;

/// <summary>
/// A small modal password prompt for opening an encrypted source workbook. Built in code
/// (no XAML pair) since it's a single-purpose dialog. Returns the entered password, or null
/// if the user cancels. The text is masked via <see cref="PasswordBox"/>, which never exposes
/// the value through automation or binding.
/// </summary>
internal static class PasswordDialog
{
    public static string? Prompt(Window owner, string message)
    {
        var win = new Window
        {
            Title = "Password required",
            Owner = owner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)Application.Current.Resources["WindowBg"]
        };

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["InkText"],
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var box = new PasswordBox { FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button
        {
            Content = "Open",
            IsDefault = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["PrimaryButton"]
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Style = (Style)Application.Current.Resources["GhostButton"]
        };

        string? result = null;
        ok.Click += (_, _) => { result = box.Password; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        win.Content = root;
        win.Loaded += (_, _) => box.Focus();
        // Enter in the password box confirms.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Password; win.DialogResult = true; }
        };

        return win.ShowDialog() == true ? result : null;
    }
}
