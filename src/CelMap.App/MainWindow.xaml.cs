using System.Windows;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. The window is a thin view; all
/// orchestration lives in <see cref="MainViewModel"/>, which drives CelMap.Core.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
