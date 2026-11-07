using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace IDMLite.Windows;

public partial class SupportDialog : Window
{
    public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/vibeabi";

    public SupportDialog()
    {
        InitializeComponent();
    }

    private void LnkBuyMeACoffee_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
