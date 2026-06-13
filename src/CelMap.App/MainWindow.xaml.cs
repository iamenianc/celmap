using System;
using System.Windows;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. The window is a thin shell view; all coordination
/// and navigation states live in MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();

        // Prompting for an encrypted source's password is a view concern (the masked dialog).
        vm.Setup.PasswordPrompt = message => PasswordDialog.Prompt(this, message);

        // Maximise the window for the full-screen mapping grid; restore to the compact
        // size on the way back to the Setup screen.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsOnMapping))
                WindowState = vm.IsOnMapping ? WindowState.Maximized : WindowState.Normal;
        };

        DataContext = vm;
    }
}
