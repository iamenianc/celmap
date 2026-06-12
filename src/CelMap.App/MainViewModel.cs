using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CelMap.App;

/// <summary>
/// Orchestrates <see cref="CelMap.Core"/> for the WPF shell. Holds no mapping logic of
/// its own — it drives the same read → match → write pipeline the CLI runs, proving the
/// UI/core split (PRD §9).
///
/// Tracer 4 (UI/UX overhaul) presents <b>two screens in one window</b>:
/// <list type="number">
/// <item><b>Setup</b> — drag-drop source + target files; sheet (defaults to first) and an
/// auto-detected header row, validated against a header preview strip.</item>
/// <item><b>Mapping</b> — a full-window, Excel-like preview. Top grid = source header +
/// ~10 sample rows; bottom grid = each target column showing the sample rows of whichever
/// source is mapped into it. Target column order is fixed and never reorders.</item>
/// </list>
/// Auto-map runs on the way into the mapping screen and pre-fills confident slots.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How many sample data rows the Excel-like grids preview.</summary>
    public const int SampleRowCount = 10;

    private readonly IWorkbookReader _reader;
    private readonly IColumnMatcher _matcher;
    private readonly ITargetWriter _writer;
    private readonly AliasRules _aliases;
    private readonly QualifiedRules _qualified;

    // Cached sheet data + headers from the last Match, so re-applying the threshold and
    // the Write step don't re-read the files.
    private SheetData? _sourceData;
    private SheetData? _targetData;
    private IReadOnlyList<HeaderColumn> _sourceHeaders = Array.Empty<HeaderColumn>();
    private IReadOnlyList<HeaderColumn> _targetHeaders = Array.Empty<HeaderColumn>();
    private int _matchedSrcHeaderRow;
    private int _matchedTgtHeaderRow;
    private string _matchedSourceSheet = "";
    private string _matchedTargetSheet = "";

    // Sample cells keyed by source column index, computed once per Match for fast reuse.
    private Dictionary<int, IReadOnlyList<string>> _sourceSamples = new();

    public MainViewModel()
        : this(new WorkbookReader()) { }

    public MainViewModel(IWorkbookReader reader)
    {
        _reader = reader;
        _aliases = AliasRules.LoadDefault();
        _qualified = QualifiedRules.LoadDefault();
        _matcher = new ColumnMatcher(_aliases, _qualified);
        _writer = new TargetWriter(_reader);

        OutputDirectory = @"C:\temp";
    }

    // ====================================================================== //
    //  Screen state                                                          //
    // ====================================================================== //

    /// <summary>False = Setup screen, true = full-window Mapping screen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _isOnMapping;

    [RelayCommand]
    private void BackToSetup() => IsOnMapping = false;

    // ====================================================================== //
    //  Setup screen: source / target files, sheets, header rows              //
    // ====================================================================== //

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _sourceFilePath;

    public string? SourceFileName =>
        string.IsNullOrEmpty(SourceFilePath) ? null : Path.GetFileName(SourceFilePath);

    public ObservableCollection<string> SourceSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _selectedSourceSheet;

    [ObservableProperty]
    private int _sourceHeaderRow = 1;   // 1-based for the user; →0-based for Core

    /// <summary>First ~10 detected source headers, shown as chips so the user can confirm
    /// the auto-detected header row is the real one.</summary>
    public ObservableCollection<string> SourceHeaderPreview { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _targetFilePath;

    public string? TargetFileName =>
        string.IsNullOrEmpty(TargetFilePath) ? null : Path.GetFileName(TargetFilePath);

    public ObservableCollection<string> TargetSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    private string? _selectedTargetSheet;

    [ObservableProperty]
    private int _targetHeaderRow = 1;

    public ObservableCollection<string> TargetHeaderPreview { get; } = new();

    // Re-detect + refresh the preview when the user changes a sheet or header row by hand.
    partial void OnSelectedSourceSheetChanged(string? value) => RefreshSourceSetup(detect: true);
    partial void OnSelectedTargetSheetChanged(string? value) => RefreshTargetSetup(detect: true);
    partial void OnSourceHeaderRowChanged(int value) => RefreshSourceSetup(detect: false);
    partial void OnTargetHeaderRowChanged(int value) => RefreshTargetSetup(detect: false);

    partial void OnSourceFilePathChanged(string? value) => OnPropertyChanged(nameof(SourceFileName));
    partial void OnTargetFilePathChanged(string? value) => OnPropertyChanged(nameof(TargetFileName));

    // ====================================================================== //
    //  Output / write options                                                //
    // ====================================================================== //

    [ObservableProperty]
    private int _confidenceThreshold = 80;

    [ObservableProperty]
    private string _outputDirectory;

    /// <summary>Append vs Overwrite write mode (PRD §2.5). Bound to a visible toggle.</summary>
    [ObservableProperty]
    private bool _appendMode;

    public WriteMode WriteMode => AppendMode ? WriteMode.Append : WriteMode.Overwrite;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Drop a source and a target file, check the header rows, then Map.";

    // ====================================================================== //
    //  Mapping screen grids                                                  //
    // ====================================================================== //

    /// <summary>Top grid: every source column, with sample rows.</summary>
    public ObservableCollection<SourceColumnViewModel> SourceColumns { get; } = new();

    /// <summary>Bottom grid: every target column, in fixed engine order. Never reordered.</summary>
    public ObservableCollection<MappingRowViewModel> Rows { get; } = new();

    /// <summary>True once a Match has produced rows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _hasMatched;

    public int LinkedCount => Rows.Count(r => r.IsLinked && !r.IsHidden);

    // ====================================================================== //
    //  File pickers + drag-drop                                              //
    // ====================================================================== //

    [RelayCommand]
    private void BrowseSource()
    {
        if (PickExcelFile() is { } path) LoadSourceFile(path);
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (PickExcelFile() is { } path) LoadTargetFile(path);
    }

    /// <summary>Load a source workbook (from Browse or a file drop): list sheets, default to
    /// the first, auto-detect its header row, and refresh the preview.</summary>
    public void LoadSourceFile(string path)
    {
        SourceFilePath = path;
        LoadSheets(path, SourceSheets, s => SelectedSourceSheet = s);
        ResetGrid();
        RefreshSourceSetup(detect: true);
    }

    public void LoadTargetFile(string path)
    {
        TargetFilePath = path;
        LoadSheets(path, TargetSheets, s => SelectedTargetSheet = s);
        ResetGrid();
        RefreshTargetSetup(detect: true);
    }

    /// <summary>True if a dropped/picked file looks like a workbook we can read.</summary>
    public static bool IsExcelFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
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
            select(sheets.FirstOrDefault());   // default to first sheet (PRD: user can change)
        }
        catch (Exception ex)
        {
            select(null);
            Status = $"Could not read sheets from '{Path.GetFileName(path)}': {ex.Message}";
        }
    }

    /// <summary>Auto-detect the header row for the chosen source sheet (when
    /// <paramref name="detect"/>) and refresh the header-preview chips.</summary>
    private void RefreshSourceSetup(bool detect)
    {
        if (string.IsNullOrWhiteSpace(SourceFilePath) || string.IsNullOrWhiteSpace(SelectedSourceSheet))
        {
            SourceHeaderPreview.Clear();
            return;
        }
        TryReadHeaderPreview(SourceFilePath!, SelectedSourceSheet!, detect,
            row => SourceHeaderRow = row, SourceHeaderRow - 1, SourceHeaderPreview);
    }

    private void RefreshTargetSetup(bool detect)
    {
        if (string.IsNullOrWhiteSpace(TargetFilePath) || string.IsNullOrWhiteSpace(SelectedTargetSheet))
        {
            TargetHeaderPreview.Clear();
            return;
        }
        TryReadHeaderPreview(TargetFilePath!, SelectedTargetSheet!, detect,
            row => TargetHeaderRow = row, TargetHeaderRow - 1, TargetHeaderPreview);
    }

    private void TryReadHeaderPreview(string path, string sheet, bool detect,
        Action<int> setHeaderRow1Based, int currentHeaderRow0, ObservableCollection<string> preview)
    {
        preview.Clear();
        try
        {
            var data = _reader.ReadSheet(path, sheet);
            int headerRow0 = detect ? HeaderRowDetector.Detect(data) : currentHeaderRow0;
            if (detect) setHeaderRow1Based(headerRow0 + 1);
            else headerRow0 = Math.Clamp(headerRow0, 0, Math.Max(0, data.RowCount - 1));

            var headers = HeaderExtractor.Extract(data, headerRow0);
            foreach (var h in headers.Take(10))
                preview.Add(string.IsNullOrWhiteSpace(h.Label) ? "(blank)" : h.Label);
        }
        catch (Exception ex)
        {
            Status = FriendlyError(ex);
        }
    }

    // ====================================================================== //
    //  Match: read + score, populate the grids, go to the mapping screen     //
    // ====================================================================== //

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

            var (sourceData, targetData, sourceHeaders, targetHeaders, result) = await Task.Run(() =>
            {
                var src = _reader.ReadSheet(sourcePath, sourceSheet);
                var tgt = _reader.ReadSheet(targetPath, targetSheet);
                var srcH = HeaderExtractor.Extract(src, srcHeaderRow);
                var tgtH = HeaderExtractor.Extract(tgt, tgtHeaderRow);
                var res = _matcher.Match(
                    srcH, tgtH,
                    new MatcherOptions(ConfidenceThreshold: threshold),
                    s => src.ColumnIsEmpty(s.ColumnIndex, srcHeaderRow));
                return (src, tgt, srcH, tgtH, res);
            });

            _sourceData = sourceData;
            _targetData = targetData;
            _sourceHeaders = sourceHeaders;
            _targetHeaders = targetHeaders;
            _matchedSrcHeaderRow = srcHeaderRow;
            _matchedTgtHeaderRow = tgtHeaderRow;
            _matchedSourceSheet = sourceSheet;
            _matchedTargetSheet = targetSheet;
            _sourceSamples = BuildSourceSamples(sourceData, sourceHeaders, srcHeaderRow);

            PopulateGrid(result);

            int auto = Rows.Count(r => r.IsLinked);
            // Tier tally of the auto-applied rows — makes it visible whether alias/exact
            // (the synonyms rules) actually fired, vs everything coming through as fuzzy.
            int exact = Rows.Count(r => r.IsLinked && r.Kind == MatchKind.Exact);
            int alias = Rows.Count(r => r.IsLinked && r.Kind == MatchKind.Alias);
            int qual  = Rows.Count(r => r.IsLinked && r.Kind == MatchKind.Qualified);
            int fuzzy = Rows.Count(r => r.IsLinked && r.Kind == MatchKind.Fuzzy);
            Status = $"Auto-mapped {auto} of {Rows.Count} target column(s) "
                   + $"[exact {exact} · alias {alias} · qualified {qual} · fuzzy {fuzzy}; "
                   + $"{_aliases.Groups.Count} synonym groups loaded]. "
                   + "Check the highlighted ones, map the rest, then Execute.";
            HasMatched = true;
            ResetHistory();
            IsOnMapping = true;   // straight into the full-window mapping screen
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

    /// <summary>Pull the first <see cref="SampleRowCount"/> data values below the header for
    /// every source column, once, so grid cells and slot previews are cheap.</summary>
    private static Dictionary<int, IReadOnlyList<string>> BuildSourceSamples(
        SheetData data, IReadOnlyList<HeaderColumn> headers, int headerRow0)
    {
        var map = new Dictionary<int, IReadOnlyList<string>>(headers.Count);
        foreach (var h in headers)
            map[h.ColumnIndex] = SampleFor(data, h.ColumnIndex, headerRow0);
        return map;
    }

    private static IReadOnlyList<string> SampleFor(SheetData data, int colIndex, int headerRow0)
    {
        var cells = new List<string>(SampleRowCount);
        for (int r = headerRow0 + 1; r <= headerRow0 + SampleRowCount; r++)
            cells.Add(r < data.RowCount ? data.GetCell(r, colIndex).ToString() : string.Empty);
        return cells;
    }

    private void PopulateGrid(MappingResult result)
    {
        SourceColumns.Clear();
        foreach (var h in _sourceHeaders)
        {
            bool empty = _sourceData!.ColumnIsEmpty(h.ColumnIndex, _matchedSrcHeaderRow);
            SourceColumns.Add(new SourceColumnViewModel(h, empty, SamplesFor(h)));
        }

        Rows.Clear();
        foreach (var m in result.Mappings)
        {
            var row = new MappingRowViewModel(m);
            // Seed the preview for auto-linked slots.
            if (row.LinkedSource is { } src)
                row.SetLink(src, SourceIsEmpty, SamplesFor);
            Rows.Add(row);
        }

        RefreshSourceLinkFlags();
        OnPropertyChanged(nameof(LinkedCount));
    }

    private IReadOnlyList<string> SamplesFor(HeaderColumn source) =>
        _sourceSamples.TryGetValue(source.ColumnIndex, out var s) ? s : Array.Empty<string>();

    // ====================================================================== //
    //  Mapping interaction (called from the view)                            //
    // ====================================================================== //

    /// <summary>Map a target slot to a source column (the user clicked a source option in the
    /// slot's inline picker). Source reuse is allowed.</summary>
    public void MapSlot(MappingRowViewModel? row, SourceColumnViewModel? source)
    {
        if (row is null || source is null) return;
        PushHistory();
        row.SetLink(source.Column, SourceIsEmpty, SamplesFor);
        AfterMappingEdit();
    }

    /// <summary>Fill a target column with a typed literal applied to every data row (the user
    /// typed a value in the slot's picker box). Replaces any mapped source.</summary>
    public void SetConstantValue(MappingRowViewModel? row, string? text)
    {
        if (row is null) return;
        text = text?.Trim() ?? "";
        if (text.Length == 0) return;
        PushHistory();
        row.SetConstant(text, SampleRowCount);
        AfterMappingEdit();
    }

    /// <summary>Clear a target slot back to empty — drops a mapped source or a typed constant
    /// (the user clicked the slot header).</summary>
    public void ClearSlot(MappingRowViewModel? row)
    {
        if (row is null || !row.IsFilled) return;
        PushHistory();
        row.SetLink(null, SourceIsEmpty, SamplesFor);   // also clears any constant
        AfterMappingEdit();
    }

    /// <summary>Open the inline source picker on a slot (the user clicked the body to change it).</summary>
    public void OpenPicker(MappingRowViewModel? row)
    {
        if (row is null) return;
        row.IsPickerOpen = true;
    }

    /// <summary>Hide or show a target column. Hidden columns collapse to a thin strip and are
    /// excluded from the write. A <b>filled</b> column (mapped source or typed constant) can
    /// never be hidden — there's content destined for it, so hiding would silently drop it.</summary>
    public void SetHidden(MappingRowViewModel? row, bool hidden)
    {
        if (row is null || row.IsHidden == hidden) return;
        if (hidden && row.IsFilled)
        {
            Status = $"“{row.TargetLabel}” is mapped — clear it before hiding.";
            return;
        }
        PushHistory();
        row.IsHidden = hidden;
        AfterMappingEdit();
    }

    private void AfterMappingEdit()
    {
        RefreshSourceLinkFlags();
        OnPropertyChanged(nameof(LinkedCount));
    }

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

    // ====================================================================== //
    //  Undo / redo — mapping edits only                                      //
    // ====================================================================== //

    /// <summary>A snapshot of the mapping state: per-target linked source (or null), typed
    /// constants, and the hidden set.</summary>
    private readonly record struct MapSnapshot(
        IReadOnlyDictionary<int, int?> Links,
        IReadOnlyDictionary<int, string> Constants,
        IReadOnlySet<int> Hidden);

    private readonly Stack<MapSnapshot> _undo = new();
    private readonly Stack<MapSnapshot> _redo = new();

    private MapSnapshot Snapshot()
    {
        var links = Rows.ToDictionary(
            r => r.TargetColumn.ColumnIndex,
            r => (int?)r.LinkedSource?.ColumnIndex);
        var constants = Rows.Where(r => r.IsConstant)
            .ToDictionary(r => r.TargetColumn.ColumnIndex, r => r.ConstantValue!);
        var hidden = Rows.Where(r => r.IsHidden).Select(r => r.TargetColumn.ColumnIndex).ToHashSet();
        return new MapSnapshot(links, constants, hidden);
    }

    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Record the current state before an edit, and invalidate the redo stack.</summary>
    private void PushHistory()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo => IsOnMapping && _undo.Count > 0;
    private bool CanRedo => IsOnMapping && _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Apply a snapshot back onto the rows (links + hidden), then refresh derived state.</summary>
    private void Restore(MapSnapshot snap)
    {
        var byCol = _sourceHeaders.ToDictionary(h => h.ColumnIndex);
        foreach (var row in Rows)
        {
            int tgt = row.TargetColumn.ColumnIndex;
            if (snap.Constants.TryGetValue(tgt, out var constant))
            {
                row.SetConstant(constant, SampleRowCount);
            }
            else
            {
                HeaderColumn? source = snap.Links.TryGetValue(tgt, out var srcIdx) && srcIdx is int i
                    && byCol.TryGetValue(i, out var h) ? h : null;
                row.SetLink(source, SourceIsEmpty, SamplesFor);
            }
            row.IsHidden = snap.Hidden.Contains(tgt);
        }
        AfterMappingEdit();
    }

    // ====================================================================== //
    //  Threshold re-apply (fuzzy only — tiers are score 100)                 //
    // ====================================================================== //

    partial void OnConfidenceThresholdChanged(int value)
    {
        if (!HasMatched || _sourceData is null) return;

        // Re-run the engine at the new threshold and re-seed auto links, but preserve the
        // user's manual overrides so the slider doesn't undo clicks.
        var manual = Rows.Where(r => r.IsManualOverride)
                         .ToDictionary(r => r.TargetColumn.ColumnIndex, r => r.LinkedSource);
        var hidden = Rows.Where(r => r.IsHidden).Select(r => r.TargetColumn.ColumnIndex).ToHashSet();

        var result = _matcher.Match(
            _sourceHeaders, _targetHeaders,
            new MatcherOptions(ConfidenceThreshold: value),
            s => _sourceData.ColumnIsEmpty(s.ColumnIndex, _matchedSrcHeaderRow));

        Rows.Clear();
        foreach (var m in result.Mappings)
        {
            var row = new MappingRowViewModel(m);
            if (manual.TryGetValue(m.TargetColumn.ColumnIndex, out var overridden))
                row.SetLink(overridden, SourceIsEmpty, SamplesFor);
            else if (row.LinkedSource is { } src)
                row.SetLink(src, SourceIsEmpty, SamplesFor);
            row.IsHidden = hidden.Contains(m.TargetColumn.ColumnIndex);
            Rows.Add(row);
        }
        ResetHistory();   // a fresh auto-apply is a new baseline; don't undo across it
        AfterMappingEdit();
        Status = $"Re-applied threshold {value}. {LinkedCount} column(s) linked.";
    }

    // ====================================================================== //
    //  Write: consume the edited links                                       //
    // ====================================================================== //

    private bool CanWrite => HasMatched && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var columnMap = new Dictionary<int, int>();
        var constantColumns = new Dictionary<int, string>();
        foreach (var row in Rows)
        {
            if (row.IsHidden) continue;
            if (row.ConstantValue is { } constant)
                constantColumns[row.TargetColumn.ColumnIndex] = constant;
            else if (row.LinkedSource is { } src)
                columnMap[src.ColumnIndex] = row.TargetColumn.ColumnIndex;
        }

        int filledCount = columnMap.Count + constantColumns.Count;
        if (filledCount == 0)
        {
            Status = "Nothing mapped — pick a source or type a value for at least one target column before writing.";
            return;
        }

        if (WriteMode == WriteMode.Overwrite && !ConfirmOverwrite(filledCount))
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
                columnMap, outputDir, mode, constantColumns)));

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

    // ====================================================================== //
    //  Helpers                                                               //
    // ====================================================================== //

    private void ResetGrid()
    {
        HasMatched = false;
        Rows.Clear();
        SourceColumns.Clear();
        ResetHistory();
        OnPropertyChanged(nameof(LinkedCount));
    }

    private static string FriendlyError(Exception ex)
    {
        // The known Tracer 1/2 gap: ClosedXML throws a raw IOException when the file is
        // open in Excel. Surface it as guidance, never a crash (PRD §10).
        if (ex is IOException)
            return "Could not open a file — it may be open in Excel. "
                 + "Close it there and try again.\n(" + ex.Message + ")";
        return $"Error: {ex.Message}";
    }
}
