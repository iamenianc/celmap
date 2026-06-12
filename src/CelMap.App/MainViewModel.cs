using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CelMap.App;

/// <summary>
/// Orchestrates <see cref="CelMap.Core"/> for the WPF shell. Holds no mapping
/// logic of its own — it drives the same read → match → write pipeline the CLI
/// runs (see CelMap.Cli/Program.cs), proving the UI/core split (PRD §9).
///
/// Tracer 4 splits the old one-shot Run into an interactive workflow:
/// <b>Match</b> populates a click-to-link grid (left = source columns, right =
/// target columns); the user steers the links by clicking; <b>Write</b> consumes
/// the edited links and honours the chosen write mode (PRD §2.3, §2.5).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IWorkbookReader _reader;
    private readonly IColumnMatcher _matcher;
    private readonly ITargetWriter _writer;
    private readonly AliasRules _aliases;
    private readonly QualifiedRules _qualified;

    // Cached sheet data + headers from the last Match, so re-applying the
    // threshold and the Write step don't re-read the files.
    private SheetData? _sourceData;
    private IReadOnlyList<HeaderColumn> _sourceHeaders = Array.Empty<HeaderColumn>();
    private IReadOnlyList<HeaderColumn> _targetHeaders = Array.Empty<HeaderColumn>();
    private int _matchedSrcHeaderRow;
    private int _matchedTgtHeaderRow;
    private string _matchedSourceSheet = "";
    private string _matchedTargetSheet = "";

    public MainViewModel()
        : this(new WorkbookReader()) { }

    public MainViewModel(IWorkbookReader reader)
    {
        _reader = reader;
        _aliases = AliasRules.LoadDefault();
        _qualified = QualifiedRules.LoadDefault();
        _matcher = new ColumnMatcher(_aliases, _qualified);
        _writer = new TargetWriter(_reader);

        OutputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CelMap");

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MappingRowViewModel.GroupKey)));
        // Sort by section first (needs-mapping → mapped → hidden), then keep the
        // original target-column order within each section.
        RowsView.SortDescriptions.Add(new SortDescription(nameof(MappingRowViewModel.GroupSort), ListSortDirection.Ascending));

        TryPreloadDefaultTemplate();
    }

    /// <summary>Re-sort/regroup the rows view so freshly-mapped rows move into their
    /// section. Property-change alone doesn't reposition items in a CollectionView.</summary>
    private void RefreshRowsView()
    {
        RefreshSourceLinkFlags();
        OnPropertyChanged(nameof(LinkedCount));
        RowsView.Refresh();
    }

    // ---- Source file + sheet ----------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _sourceFilePath;

    public ObservableCollection<string> SourceSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _selectedSourceSheet;

    [ObservableProperty]
    private int _sourceHeaderRow = 1;   // 1-based for the user; →0-based for Core

    // ---- Target file + sheet ----------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _targetFilePath;

    public ObservableCollection<string> TargetSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _selectedTargetSheet;

    [ObservableProperty]
    private int _targetHeaderRow = 1;

    // ---- Matching / output options ----------------------------------------

    [ObservableProperty]
    private int _confidenceThreshold = 80;

    [ObservableProperty]
    private string _outputDirectory;

    /// <summary>Append vs Overwrite write mode (PRD §2.5). Bound to a visible toggle.</summary>
    [ObservableProperty]
    private bool _appendMode;

    public WriteMode WriteMode => AppendMode ? WriteMode.Append : WriteMode.Overwrite;

    [ObservableProperty]
    private bool _hideEmptyMappedColumns;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Pick a source and target file, choose sheets, then Match.";

    // ---- Interactive grid state -------------------------------------------

    /// <summary>Left pane: every source column the user can click to select.</summary>
    public ObservableCollection<SourceColumnViewModel> SourceColumns { get; } = new();

    /// <summary>Right pane: every target column, each showing its linked source.</summary>
    public ObservableCollection<MappingRowViewModel> Rows { get; } = new();

    /// <summary>Grouped/sorted view of <see cref="Rows"/>: unmapped rows float to the
    /// top under "Needs mapping" so the user focuses there; mapped rows drop into a
    /// "Mapped" section and hidden rows sink to "Hidden". Re-sorted after every edit.</summary>
    public ICollectionView RowsView { get; }

    /// <summary>The source column the user has clicked and is about to link (left pane).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    [NotifyPropertyChangedFor(nameof(LinkHintText))]
    private SourceColumnViewModel? _selectedSource;

    partial void OnSelectedSourceChanged(SourceColumnViewModel? oldValue, SourceColumnViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsPicked = false;
        if (newValue is not null) newValue.IsPicked = true;
    }

    public bool HasSelectedSource => SelectedSource is not null;

    public string LinkHintText
    {
        get
        {
            if (SelectedSource is { } s)
                return $"Now click a target row to link “{s.Label}”.  "
                     + "(Click the same source again to put it back.)";
            if (!HasMatched)
                return "Step 1 — click “Auto-map” to let the engine fill in the matches it's confident about.";
            return "Step 2 — review the auto-matches (click one to reject it).   "
                 + "Step 3 — map the rest: click a source on the left, then its target row.   "
                 + "Step 4 — click “Confirm & execute”.";
        }
    }

    /// <summary>True once a Match has produced rows — Write only makes sense then.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(LinkHintText))]
    private bool _hasMatched;

    public int LinkedCount => Rows.Count(r => r.IsLinked && !r.IsHidden);

    // ---- File pickers ------------------------------------------------------

    [RelayCommand]
    private void BrowseSource()
    {
        if (PickExcelFile() is { } path)
        {
            SourceFilePath = path;
            LoadSheets(path, SourceSheets, s => SelectedSourceSheet = s);
            ResetGrid();
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (PickExcelFile() is { } path)
        {
            TargetFilePath = path;
            LoadSheets(path, TargetSheets, s => SelectedTargetSheet = s);
            ResetGrid();
        }
    }

    private static string? PickExcelFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void LoadSheets(string path, ObservableCollection<string> sheets, Action<string?> select)
    {
        sheets.Clear();
        try
        {
            foreach (var name in _reader.GetSheetNames(path))
                sheets.Add(name);
            select(sheets.FirstOrDefault());
        }
        catch (Exception ex)
        {
            select(null);
            Status = $"Could not read sheets from '{Path.GetFileName(path)}': {ex.Message}";
        }
    }

    // ---- Match: read + score, then populate the click-to-map panes --------

    private bool CanMatch =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(SourceFilePath)
        && !string.IsNullOrWhiteSpace(TargetFilePath)
        && !string.IsNullOrWhiteSpace(SelectedSourceSheet)
        && !string.IsNullOrWhiteSpace(SelectedTargetSheet);

    [RelayCommand(CanExecute = nameof(CanMatch))]
    private async Task MatchAsync()
    {
        IsBusy = true;
        Status = "Matching…";
        try
        {
            string sourcePath = SourceFilePath!;
            string targetPath = TargetFilePath!;
            string sourceSheet = SelectedSourceSheet!;
            string targetSheet = SelectedTargetSheet!;
            int srcHeaderRow = SourceHeaderRow - 1;
            int tgtHeaderRow = TargetHeaderRow - 1;
            int threshold = ConfidenceThreshold;

            var (sourceData, sourceHeaders, targetHeaders, result) = await Task.Run(() =>
            {
                var src = _reader.ReadSheet(sourcePath, sourceSheet);
                var tgt = _reader.ReadSheet(targetPath, targetSheet);
                var srcH = HeaderExtractor.Extract(src, srcHeaderRow);
                var tgtH = HeaderExtractor.Extract(tgt, tgtHeaderRow);
                var res = _matcher.Match(
                    srcH, tgtH,
                    new MatcherOptions(ConfidenceThreshold: threshold),
                    s => src.ColumnIsEmpty(s.ColumnIndex, srcHeaderRow));
                return (src, srcH, tgtH, res);
            });

            // Cache for the Write step and threshold re-applies.
            _sourceData = sourceData;
            _sourceHeaders = sourceHeaders;
            _targetHeaders = targetHeaders;
            _matchedSrcHeaderRow = srcHeaderRow;
            _matchedTgtHeaderRow = tgtHeaderRow;
            _matchedSourceSheet = sourceSheet;
            _matchedTargetSheet = targetSheet;

            PopulateGrid(result);

            int auto = Rows.Count(r => r.IsLinked);
            int needsAttention = Rows.Count(r => !r.IsLinked
                && r.Original.Status is MatchStatus.Ambiguous or MatchStatus.NeedsReview or MatchStatus.Unmatched
                && (r.Original.Score > 0 || r.IsStrict));
            Status = $"Auto-mapped {Rows.Count} target column(s): {auto} auto-linked, "
                   + $"{needsAttention} need a look. Reject/adjust above, map the rest, then Confirm & execute.";
            HasMatched = true;
        }
        catch (Exception ex)
        {
            Status = FriendlyError(ex);
            HasMatched = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PopulateGrid(MappingResult result)
    {
        SelectedSource = null;

        SourceColumns.Clear();
        foreach (var h in _sourceHeaders)
        {
            bool empty = _sourceData!.ColumnIsEmpty(h.ColumnIndex, _matchedSrcHeaderRow);
            SourceColumns.Add(new SourceColumnViewModel(h, empty));
        }

        Rows.Clear();
        foreach (var m in result.Mappings)
            Rows.Add(new MappingRowViewModel(m));

        RefreshRowsView();
    }

    // ---- Click-to-map interaction -----------------------------------------

    /// <summary>Left-pane click: select a source column to link next.</summary>
    [RelayCommand]
    private void SelectSource(SourceColumnViewModel? source)
    {
        // Click the already-selected source again to deselect.
        SelectedSource = ReferenceEquals(SelectedSource, source) ? null : source;
    }

    /// <summary>Right-pane click on a target row. Links the pending source, or
    /// clears the row if no source is selected (or the same source is re-clicked).</summary>
    [RelayCommand]
    private void LinkTarget(MappingRowViewModel? row)
    {
        if (row is null) return;

        if (SelectedSource is { } src)
        {
            // Re-clicking the source it already holds clears the link (toggle).
            bool sameAsCurrent = row.LinkedSource?.ColumnIndex == src.Column.ColumnIndex;
            row.SetLink(sameAsCurrent ? null : src.Column, SourceIsEmpty);
            SelectedSource = null;
        }
        else
        {
            // No pending source → a click clears whatever the row had.
            row.SetLink(null, SourceIsEmpty);
        }

        RefreshRowsView();
    }

    /// <summary>Called by the View when a row's Hide checkbox is toggled — regroup so
    /// the hidden row sinks out of the way immediately.</summary>
    public void OnRowHiddenChanged() => RefreshRowsView();

    private bool SourceIsEmpty(HeaderColumn source) =>
        _sourceData?.ColumnIsEmpty(source.ColumnIndex, _matchedSrcHeaderRow) ?? false;

    private void RefreshSourceLinkFlags()
    {
        var linked = Rows.Where(r => r.IsLinked && !r.IsHidden)
                         .Select(r => r.LinkedSource!.ColumnIndex)
                         .ToHashSet();
        foreach (var s in SourceColumns)
            s.IsLinked = linked.Contains(s.Column.ColumnIndex);
    }

    // ---- Threshold re-apply (fuzzy only — tiers are score 100) -------------

    partial void OnConfidenceThresholdChanged(int value)
    {
        if (!HasMatched || _sourceData is null) return;

        // Re-run the engine at the new threshold and re-seed auto links, but
        // preserve the user's manual overrides so the slider doesn't undo clicks.
        var manual = Rows.Where(r => r.IsManualOverride)
                         .ToDictionary(r => r.TargetColumn.ColumnIndex,
                                       r => r.LinkedSource);

        var result = _matcher.Match(
            _sourceHeaders, _targetHeaders,
            new MatcherOptions(ConfidenceThreshold: value),
            s => _sourceData.ColumnIsEmpty(s.ColumnIndex, _matchedSrcHeaderRow));

        Rows.Clear();
        foreach (var m in result.Mappings)
        {
            var row = new MappingRowViewModel(m);
            if (manual.TryGetValue(m.TargetColumn.ColumnIndex, out var overridden))
                row.SetLink(overridden, SourceIsEmpty);
            Rows.Add(row);
        }
        RefreshRowsView();
        Status = $"Re-applied threshold {value}. {LinkedCount} column(s) linked.";
    }

    // ---- Write: consume the edited links ----------------------------------

    private bool CanWrite => HasMatched && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        // Build the column map from the current (possibly hand-edited) links,
        // skipping hidden rows (PRD §2.3 hide/show).
        var columnMap = new Dictionary<int, int>();
        foreach (var row in Rows)
        {
            if (row.IsHidden || row.LinkedSource is not { } src) continue;
            columnMap[src.ColumnIndex] = row.TargetColumn.ColumnIndex;
        }

        if (columnMap.Count == 0)
        {
            Status = "Nothing linked — click a source then a target to map at least one column before writing.";
            return;
        }

        // Overwrite is destructive to the copy's data rows: confirm first (PRD §2.5).
        if (WriteMode == WriteMode.Overwrite && !ConfirmOverwrite(columnMap.Count))
        {
            Status = "Overwrite cancelled.";
            return;
        }

        IsBusy = true;
        Status = AppendMode ? "Appending…" : "Writing…";
        try
        {
            string sourcePath = SourceFilePath!;
            string targetPath = TargetFilePath!;
            string outputDir = OutputDirectory;
            var mode = WriteMode;

            var writeResult = await Task.Run(() => _writer.Write(new WriteRequest(
                sourcePath, _matchedSourceSheet, _matchedSrcHeaderRow,
                targetPath, _matchedTargetSheet, _matchedTgtHeaderRow,
                columnMap, outputDir, mode)));

            string warnings = writeResult.Warnings.Count > 0
                ? "\nWarnings:\n  • " + string.Join("\n  • ", writeResult.Warnings)
                : string.Empty;

            Status = $"Done ({mode}). {columnMap.Count} column(s) mapped, "
                   + $"{writeResult.RowsWritten} row(s) written.{warnings}\n"
                   + $"Output: {writeResult.OutputFilePath}";
        }
        catch (Exception ex)
        {
            Status = FriendlyError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Overridable confirmation hook (the View shows the real dialog).</summary>
    public Func<int, bool>? OverwriteConfirm { get; set; }

    private bool ConfirmOverwrite(int columnCount) =>
        OverwriteConfirm?.Invoke(columnCount) ?? true;

    // ---- Helpers -----------------------------------------------------------

    private void ResetGrid()
    {
        HasMatched = false;
        SelectedSource = null;
        Rows.Clear();
        SourceColumns.Clear();
        OnPropertyChanged(nameof(LinkedCount));
    }

    private static string FriendlyError(Exception ex)
    {
        // The known Tracer 1/2 gap: ClosedXML throws a raw IOException when the
        // file is open in Excel. Surface it as guidance, never a crash (PRD §10).
        if (ex is IOException)
            return "Could not open a file — it may be open in Excel. "
                 + "Close it there and try again.\n(" + ex.Message + ")";
        return $"Error: {ex.Message}";
    }

    /// <summary>Placeholder for the Tracer 5 settings-driven default template.</summary>
    private void TryPreloadDefaultTemplate()
    {
        // Intentionally empty until settings persistence lands (Tracer 5).
    }
}
