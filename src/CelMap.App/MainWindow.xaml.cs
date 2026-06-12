using System.Windows;
using System.Windows.Controls;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. The window is a thin view; all
/// orchestration lives in <see cref="MainViewModel"/>, which drives CelMap.Core.
/// Only genuinely view-level concerns live here: the overwrite confirmation
/// dialog and translating a couple of UI gestures into VM calls.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();

        // The destructive-Overwrite confirmation is a view concern (PRD §2.5).
        vm.OverwriteConfirm = columnCount =>
            MessageBox.Show(
                this,
                $"Overwrite mode will replace the data rows in the output copy, "
                + $"starting just below the target header, with {columnCount} mapped column(s).\n\n"
                + "(Your original target file is never touched — only the copy in the output folder.)\n\n"
                + "Proceed?",
                "Confirm overwrite",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;

        DataContext = vm;
    }

    // Write-mode radios → VM.AppendMode. (Two-way on RadioButton.IsChecked is
    // awkward with a single bool, so we drive it explicitly from Checked.)
    private void OverwriteRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.AppendMode = false;
    }

    private void AppendRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.AppendMode = true;
    }

    // Hide checkbox toggled → regroup so the row moves out of the way at once.
    private void HideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OnRowHiddenChanged();
        e.Handled = true;   // don't let the click bubble to the row's link Button
    }
}
