using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MappingView.xaml
/// </summary>
public partial class MappingView : UserControl
{
    private MappingViewModel ViewModel => (MappingViewModel)DataContext;

    private DispatcherTimer? _clickTimer;
    private MappingRowViewModel? _pendingOpenRow;

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

    private void TargetHeader_MouseDown(object sender, MouseButtonEventArgs e) => HandleColumnClick(sender, e, isHeader: true);
    private void TargetBody_MouseDown(object sender, MouseButtonEventArgs e) => HandleColumnClick(sender, e, isHeader: false);

    private void HandleColumnClick(object sender, MouseButtonEventArgs e, bool isHeader)
    {
        e.Handled = true;
        var row = Row(sender);
        if (row is null || row.IsLocked) return;

        if (e.ClickCount == 2)
        {
            _clickTimer?.Stop();                // cancel the pending single-click open
            _pendingOpenRow = null;
            ViewModel.ClearSlot(row);           // double-click clears to blank
            return;
        }

        // A single click on the header of the row whose picker is already open cancels it
        // (closes the dropdown) rather than re-opening — the explicit "cancel" gesture.
        if (isHeader && row.IsPickerOpen)
        {
            _clickTimer?.Stop();
            _pendingOpenRow = null;
            ViewModel.ClosePicker(row);
            return;
        }

        _pendingOpenRow = row;
        _clickTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _clickTimer.Tick -= ClickTimer_Tick;
        _clickTimer.Tick += ClickTimer_Tick;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void ClickTimer_Tick(object? sender, EventArgs e)
    {
        _clickTimer?.Stop();
        if (_pendingOpenRow is { } row && !row.IsLocked) ViewModel.OpenPicker(row);
        _pendingOpenRow = null;
    }

    // The blank body keeps a plain single-click → open (no clear semantics needed when empty).
    private void TargetBody_Click(object sender, MouseButtonEventArgs e)
    {
        var row = Row(sender);
        if (row is not null && !row.IsLocked)
            ViewModel.OpenPicker(row);
        e.Handled = true;
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

    // Type-ahead filter above the picker's source list. Each picker filters only its own
    // list (the sibling ItemsControl), so two open pickers never fight over one view.
    private void PickerFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || VisualTreeHelper.GetParent(tb) is not DependencyObject panel) return;
        if (FindDescendant<ItemsControl>(panel) is not { } list) return;

        string query = tb.Text.Trim();
        list.ItemsSource = string.IsNullOrEmpty(query)
            ? ViewModel.PickableSourceColumns.ToList()
            : ViewModel.PickableSourceColumns
                .Where(s => s.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // Click a source option in the inline picker → map this slot to that source.
    private void PickerOption_Click(object sender, MouseButtonEventArgs e)
    {
        var source = (sender as FrameworkElement)?.DataContext as SourceColumnViewModel;
        var row = FindRow(sender as DependencyObject);
        ViewModel.MapSlot(row, source);
        e.Handled = true;
    }

    // Click the collapsed strip → show the column again.
    private void HiddenStrip_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.SetHidden(Row(sender), false, SetStatus);
        e.Handled = true;
    }

    /// <summary>Walk up the visual tree to the MappingRowViewModel for the target column
    /// that owns a picker option (whose own DataContext is the source, not the row).</summary>
    private static MappingRowViewModel? FindRow(DependencyObject? start)
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && fe.DataContext is MappingRowViewModel row)
                return row;
        return null;
    }
}
