using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CelMap.App;

/// <summary>
/// Interaction logic for SetupView.xaml
/// </summary>
public partial class SetupView : UserControl
{
    private SetupViewModel ViewModel => (SetupViewModel)DataContext;

    public SetupView()
    {
        InitializeComponent();
    }

    private static string? FirstDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault();
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = FirstDroppedFile(e) is { } path && SetupViewModel.IsExcelFile(path);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && sender is Border b) b.BorderBrush = (Brush)FindResource("Accent");
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = (Brush)FindResource("Hairline");
    }

    private void SourceDropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (FirstDroppedFile(e) is { } path) ViewModel.LoadSourceFile(path);
    }

    private void SourceDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.BrowseSourceCommand.CanExecute(null))
            ViewModel.BrowseSourceCommand.Execute(null);
    }
}
