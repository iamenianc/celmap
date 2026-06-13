using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. The window is a thin view; all orchestration
/// lives in <see cref="MainViewModel"/>, which drives CelMap.Core. Only genuinely
/// view-level concerns live here: file drag-drop and translating the Excel-grid
/// click gestures into VM calls.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();

        // Prompting for an encrypted source's password is a view concern (the masked dialog).
        vm.PasswordPrompt = message => PasswordDialog.Prompt(this, message);

        // Maximise the window for the full-screen mapping grid; restore to the compact
        // size on the way back to the Setup screen.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsOnMapping))
                WindowState = vm.IsOnMapping ? WindowState.Maximized : WindowState.Normal;
        };

        DataContext = vm;
    }

    // ---- Setup screen: drag-drop + click-to-browse -------------------------

    private static string? FirstDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault();
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        // Show the "copy" cue for files that look like a workbook; size/validity is
        // enforced (with feedback) on the actual drop in SourceDropZone_Drop.
        bool ok = FirstDroppedFile(e) is { } path && MainViewModel.IsExcelFile(path);
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
        // Route the first dropped file through LoadSourceFile so an invalid type or an
        // oversized file produces feedback rather than silently doing nothing.
        if (FirstDroppedFile(e) is { } path) ViewModel.LoadSourceFile(path);
    }

    private void SourceDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.BrowseSourceCommand.CanExecute(null))
            ViewModel.BrowseSourceCommand.Execute(null);
    }

    // ---- Mapping screen: target-column click gestures ----------------------
    //
    // Each handler pulls the MappingRowViewModel off the clicked element's DataContext
    // and routes the gesture to the VM. e.Handled guards stop a click on an inner
    // element (the ✕, a picker option) from also firing the header/body handler.

    private static MappingRowViewModel? Row(object sender) =>
        (sender as FrameworkElement)?.DataContext as MappingRowViewModel;

    // Target column gestures (both the header and the preview body use this):
    //   single-click → open the source/constant picker
    //   double-click → clear the column back to blank
    // The single-click "open" is deferred by the double-click window so a double-click only
    // clears (it cancels the pending open). Hide is a right-click menu item, not a gesture.
    private DispatcherTimer? _clickTimer;
    private MappingRowViewModel? _pendingOpenRow;

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

    // Right-click menu → hide this column.
    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // The MenuItem's DataContext is the row VM (inherited from the header it hangs off).
        ViewModel.SetHidden((sender as FrameworkElement)?.DataContext as MappingRowViewModel, true);
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
    // The option's own DataContext is the SourceColumnViewModel; the owning target row
    // is found by walking up the visual tree (the row VM is the column's DataContext).
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
        ViewModel.SetHidden(Row(sender), false);
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
