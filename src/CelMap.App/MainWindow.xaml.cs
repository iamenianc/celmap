using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CelMap.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. The window is a thin view; all orchestration
/// lives in <see cref="MainViewModel"/>, which drives CelMap.Core. Only genuinely
/// view-level concerns live here: the overwrite confirmation dialog, file drag-drop,
/// and translating the Excel-grid click gestures into VM calls.
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

    private static string? FirstDroppedExcelFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(MainViewModel.IsExcelFile);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = FirstDroppedExcelFile(e) is not null;
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
        if (FirstDroppedExcelFile(e) is { } path) ViewModel.LoadSourceFile(path);
    }

    private void TargetDropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (FirstDroppedExcelFile(e) is { } path) ViewModel.LoadTargetFile(path);
    }

    private void SourceDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.BrowseSourceCommand.CanExecute(null))
            ViewModel.BrowseSourceCommand.Execute(null);
    }

    private void TargetDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.BrowseTargetCommand.CanExecute(null))
            ViewModel.BrowseTargetCommand.Execute(null);
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

    private void TargetHeader_MouseDown(object sender, MouseButtonEventArgs e) => HandleColumnClick(sender, e);
    private void TargetBody_MouseDown(object sender, MouseButtonEventArgs e) => HandleColumnClick(sender, e);

    private void HandleColumnClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var row = Row(sender);

        if (e.ClickCount == 2)
        {
            _clickTimer?.Stop();                // cancel the pending single-click open
            _pendingOpenRow = null;
            ViewModel.ClearSlot(row);           // double-click clears to blank
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
        if (_pendingOpenRow is { } row) ViewModel.OpenPicker(row);
        _pendingOpenRow = null;
    }

    // The blank body keeps a plain single-click → open (no clear semantics needed when empty).
    private void TargetBody_Click(object sender, MouseButtonEventArgs e)
    {
        ViewModel.OpenPicker(Row(sender));
        e.Handled = true;
    }

    // Right-click menu → hide this column.
    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // The MenuItem's DataContext is the row VM (inherited from the header it hangs off).
        ViewModel.SetHidden((sender as FrameworkElement)?.DataContext as MappingRowViewModel, true);
    }

    // Type a value in the slot's constant box → commit on Enter OR when focus leaves the box
    // (clicking away registers like Enter).
    private void ConstantBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            CommitConstant(tb);
            e.Handled = true;
        }
    }

    private void ConstantBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitConstant(tb);
    }

    private void CommitConstant(TextBox tb)
    {
        // Guard against re-entrancy: committing changes BodyState, which can collapse the box
        // and fire LostFocus again. Only act when there's text and the slot isn't already this.
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        ViewModel.SetConstantValue(tb.DataContext as MappingRowViewModel, tb.Text);
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

    // ---- Write-mode radios → VM.AppendMode ---------------------------------

    private void OverwriteRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.AppendMode = false;
    }

    private void AppendRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.AppendMode = true;
    }
}
