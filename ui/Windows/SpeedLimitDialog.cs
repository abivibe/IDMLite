using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IDMLite.Services;

namespace IDMLite.Windows;

internal static class SpeedLimitDialog
{
    public static long? Prompt(Window? owner, long currentLimit)
    {
        var loc = LocalizationManager.Instance;
        var dialog = new Window
        {
            Title = loc.Get("Torrent_Rate"), Owner = owner,
            Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "WindowBgBrush");
        var input = new TextBox { Text = (currentLimit / 1024).ToString(CultureInfo.CurrentCulture), Margin = new Thickness(0, 0, 0, 12) };
        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(new Label { Content = loc.Get("Torrent_RatePrompt"), Target = input });
        form.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = loc.Get("Str_Save"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        actions.Children.Add(save);
        actions.Children.Add(new Button { Content = loc.Get("Str_Cancel"), IsCancel = true });
        form.Children.Add(actions);
        dialog.Content = form;
        long limit = 0;
        save.Click += (_, _) =>
        {
            if (long.TryParse(input.Text.Trim(), out var kib) && kib is >= 0 and <= int.MaxValue / 1024)
            {
                limit = kib * 1024;
                dialog.DialogResult = true;
            }
            else MessageBox.Show(dialog, loc.Get("Torrent_RateInvalid"), dialog.Title);
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? limit : null;
    }
}
