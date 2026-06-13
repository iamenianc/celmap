using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MappingView.xaml
/// </summary>
public partial class MappingView : UserControl
{
    private MappingViewModel ViewModel => (MappingViewModel)DataContext;

    public MappingView()
    {
        InitializeComponent();
    }

    private void SetStatus(string message)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel mainVm)
        {
            mainVm.Status = message;
        }
    }

    private static MappingRowViewModel? Row(object sender) =>
        (sender as FrameworkElement)?.DataContext as MappingRowViewModel;

    // Double-click a header clears the column back to blank. (Mapping is now done via the
    // right-click "Map" submenu — see MapMenuItem_Click.)
    private void TargetHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        var row = Row(sender);
        if (row is null || row.IsLocked) return;
        ViewModel.ClearSlot(row);
    }

    // Right-click "Map" submenu item → link the chosen source column to this target.
    private void MapMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SourceColumnViewModel source)
        {
            var contextMenu = FindParentContextMenu(menuItem);
            if (contextMenu?.PlacementTarget is FrameworkElement targetElement &&
                targetElement.DataContext is MappingRowViewModel targetRow)
            {
                ViewModel.MapSlot(targetRow, source);
            }
        }
    }

    // Right-click menu → clear this column back to blank.
    private void ClearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSlot((sender as FrameworkElement)?.DataContext as MappingRowViewModel);
    }

    // Right-click menu → hide this column.
    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // The MenuItem's DataContext is the row VM (inherited from the header it hangs off).
        ViewModel.SetHidden((sender as FrameworkElement)?.DataContext as MappingRowViewModel, true, SetStatus);
    }

    // Click the collapsed strip → show the column again.
    private void HiddenStrip_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.SetHidden(Row(sender), false, SetStatus);
        e.Handled = true;
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is MappingRowViewModel sourceRow)
        {
            var contextMenu = FindParentContextMenu(menuItem);
            if (contextMenu?.PlacementTarget is FrameworkElement targetElement &&
                targetElement.DataContext is MappingRowViewModel targetRow)
            {
                ViewModel.CopyMapping(targetRow, sourceRow);
            }
        }
    }

    private void SourceColumn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is SourceColumnViewModel source)
        {
            e.Handled = true;
            ViewModel.UnmapSource(source);
        }
    }

    private void SourceClearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SourceColumnViewModel source)
        {
            ViewModel.UnmapSource(source);
        }
    }

    private static ContextMenu? FindParentContextMenu(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is ContextMenu cm) return cm;
            child = VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
