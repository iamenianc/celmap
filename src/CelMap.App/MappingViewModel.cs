using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CelMap.App;

public sealed partial class MappingViewModel : ObservableObject
{
    private const int SampleRowCount = 100;

    private SheetData? _sourceData;
    private int _matchedSrcHeaderRow;
    private Dictionary<int, IReadOnlyList<string>> _sourceSamples = new();
    private IReadOnlyList<HeaderColumn> _sourceHeaders = Array.Empty<HeaderColumn>();
    private AliasRules _aliases = AliasRules.Empty;

    public ObservableCollection<SourceColumnViewModel> SourceColumns { get; } = new();
    public ObservableCollection<MappingRowViewModel> Rows { get; } = new();

    // ---- Source sheet switching ---------------------------------------------
    // The list of sheets in the source workbook and the one currently mapped.
    // Picking a different sheet asks MainViewModel (which owns the reader/matcher)
    // to re-read that sheet and re-run the whole match against the same target.
    public ObservableCollection<string> SourceSheets { get; } = new();

    /// <summary>Set by MainViewModel: invoked with the newly chosen source sheet name.</summary>
    public Action<string>? SourceSheetChanged { get; set; }

    private bool _suppressSheetChange;

    [ObservableProperty]
    private string? _selectedSourceSheet;

    partial void OnSelectedSourceSheetChanged(string? value)
    {
        if (_suppressSheetChange || string.IsNullOrEmpty(value)) return;
        SourceSheetChanged?.Invoke(value);
    }

    /// <summary>Replace the sheet list + selection without triggering a re-match (used while populating).</summary>
    public void SetSourceSheets(IEnumerable<string> sheets, string? selected)
    {
        _suppressSheetChange = true;
        try
        {
            SourceSheets.Clear();
            foreach (var s in sheets) SourceSheets.Add(s);
            SelectedSourceSheet = selected;
        }
        finally
        {
            _suppressSheetChange = false;
        }
    }

    /// <summary>True once a match has populated the grid; gates the match-quality footer.</summary>
    public bool HasMatched => Rows.Count > 0;

    public int LinkedCount => Rows.Count(r => r.IsLinked && !r.IsHidden);
    public int SourceMappedCount => SourceColumns.Count(s => s.IsLinked);
    public int SourceUnmappedCount => SourceColumns.Count(s => !s.IsLinked);
    public int TargetMappedCount => Rows.Count(r => r.IsFilled && !r.IsHidden);
    public int TargetUnmappedCount => Rows.Count(r => !r.IsFilled && !r.IsHidden);

    public int ExactCount => Rows.Count(r => r.IsLinked && !r.IsManualOverride && r.Kind == MatchKind.Exact);
    public int AliasCount => Rows.Count(r => r.IsLinked && !r.IsManualOverride && r.Kind == MatchKind.Alias);
    public int QualifiedCount => Rows.Count(r => r.IsLinked && !r.IsManualOverride && r.Kind == MatchKind.Qualified);
    public int FuzzyCount => Rows.Count(r => r.IsLinked && !r.IsManualOverride && r.Kind == MatchKind.Fuzzy);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleSourceColumns))]
    private bool _hideEmptySources = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleSourceColumns))]
    private bool _hideMappedSources;

    public IEnumerable<SourceColumnViewModel> VisibleSourceColumns =>
        SourceColumns.Where(s =>
            (!HideEmptySources || !s.IsEmpty) &&
            (!HideMappedSources || !s.IsLinked));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleRows))]
    private bool _hideMappedTargets;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleRows))]
    private bool _hideUnmappedTargets;

    partial void OnHideMappedTargetsChanged(bool value)
    {
        if (value && HideUnmappedTargets) HideUnmappedTargets = false;
    }

    partial void OnHideUnmappedTargetsChanged(bool value)
    {
        if (value && HideMappedTargets) HideMappedTargets = false;
    }

    public IEnumerable<MappingRowViewModel> VisibleRows =>
        HideMappedTargets ? Rows.Where(r => !r.IsFilled)
        : HideUnmappedTargets ? Rows.Where(r => r.IsFilled)
        : Rows;

    public IEnumerable<MappingRowViewModel> MappedRows => Rows.Where(r => r.IsFilled && !r.IsHidden);


    // ====================================================================== //
    //  History (Undo / Redo)                                                //
    // ====================================================================== //

    private readonly record struct MapSnapshot(
        IReadOnlyDictionary<int, int?> Links,
        IReadOnlyDictionary<int, string> Constants,
        IReadOnlySet<int> Hidden);

    private readonly Stack<MapSnapshot> _undo = new();
    private readonly Stack<MapSnapshot> _redo = new();

    private bool CanUndo => _undo.Count > 0;
    private bool CanRedo => _redo.Count > 0;

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

    public void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void PushHistory()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

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
    //  Operations                                                           //
    // ====================================================================== //

    public void Populate(
        MappingResult result,
        IReadOnlyList<HeaderColumn> sourceHeaders,
        SheetData sourceData,
        int matchedSrcHeaderRow,
        Dictionary<int, IReadOnlyList<string>> sourceSamples,
        ParametersViewModel parameters,
        AliasRules aliases)
    {
        _sourceHeaders = sourceHeaders;
        _sourceData = sourceData;
        _matchedSrcHeaderRow = matchedSrcHeaderRow;
        _sourceSamples = sourceSamples;
        _aliases = aliases;

        SourceColumns.Clear();
        foreach (var h in _sourceHeaders)
        {
            bool empty = _sourceData.ColumnIsEmpty(h.ColumnIndex, _matchedSrcHeaderRow);
            SourceColumns.Add(new SourceColumnViewModel(h, empty, SamplesFor(h)));
        }

        Rows.Clear();
        foreach (var m in result.Mappings)
        {
            var row = new MappingRowViewModel(m);
            if (row.LinkedSource is { } src)
            {
                row.SetLink(src, SourceIsEmpty, SamplesFor);
            }
            else
            {
                ParameterAutoFiller.AutoFill(row, parameters, _aliases);
            }
            Rows.Add(row);
        }

        ResetHistory();
        AfterMappingEdit();
    }

    public void RepopulateAfterMatchRuleChange(
        MappingResult result,
        Dictionary<int, HeaderColumn?> manualOverrides,
        HashSet<int> hiddenTargetCols,
        ParametersViewModel parameters,
        AliasRules aliases)
    {
        Rows.Clear();
        foreach (var m in result.Mappings)
        {
            var row = new MappingRowViewModel(m);
            int tgtIdx = m.TargetColumn.ColumnIndex;
            if (manualOverrides.TryGetValue(tgtIdx, out var overridden))
            {
                row.SetLink(overridden, SourceIsEmpty, SamplesFor);
            }
            else if (row.LinkedSource is { } src)
            {
                row.SetLink(src, SourceIsEmpty, SamplesFor);
            }
            else
            {
                ParameterAutoFiller.AutoFill(row, parameters, aliases);
            }
            row.IsHidden = hiddenTargetCols.Contains(tgtIdx);
            Rows.Add(row);
        }
        ResetHistory();
        AfterMappingEdit();
    }

    public void MapSlot(MappingRowViewModel? row, SourceColumnViewModel? source)
    {
        if (row is null || source is null || row.IsLocked) return;
        PushHistory();
        row.SetLink(source.Column, SourceIsEmpty, SamplesFor);
        AfterMappingEdit();
    }

    // ---- Click-to-link --------------------------------------------------------
    // When source headers are meaningless (random numbers), the right-click "Map"
    // submenu is useless because you can't recognise a source by its label. Instead
    // the user clicks a source header to "arm" it (both grids show sample data, so
    // they pick by content), then clicks the target header to link it. One armed
    // source at a time.

    /// <summary>The source the user has clicked and is about to link, if any.</summary>
    public SourceColumnViewModel? PickedSource { get; private set; }

    public bool HasPickedSource => PickedSource is not null;

    /// <summary>Toggle the armed source. Clicking the armed one again cancels.</summary>
    public void PickSource(SourceColumnViewModel? source)
    {
        if (PickedSource is { } prev) prev.IsPicked = false;
        PickedSource = ReferenceEquals(PickedSource, source) ? null : source;
        if (PickedSource is { } now) now.IsPicked = true;
        OnPropertyChanged(nameof(HasPickedSource));
    }

    public void ClearPick() => PickSource(null);

    /// <summary>Link the target to whatever source is currently armed, then disarm.</summary>
    public void LinkPickedTo(MappingRowViewModel? row)
    {
        if (PickedSource is not { } source || row is null || row.IsLocked) return;
        MapSlot(row, source);
        ClearPick();
    }

    // ---- Hover-sync -----------------------------------------------------------
    // Hovering a column flashes its mapped partner(s) on the other grid, so the link
    // is visible by content without drawing connector lines.

    /// <summary>Highlight the target(s) this source is mapped to (or clear if hovering=false).</summary>
    public void HoverSource(SourceColumnViewModel? source, bool hovering)
    {
        if (source is null) return;
        foreach (var row in Rows)
        {
            row.IsHoverHighlighted = hovering && row.IsLinked
                && row.LinkedSource?.ColumnIndex == source.Column.ColumnIndex;
        }
    }

    /// <summary>Highlight the source this target is mapped to (or clear if hovering=false).</summary>
    public void HoverTarget(MappingRowViewModel? row, bool hovering)
    {
        int? srcIdx = row?.LinkedSource?.ColumnIndex;
        foreach (var s in SourceColumns)
        {
            s.IsHoverHighlighted = hovering && srcIdx is int i && s.Column.ColumnIndex == i;
        }
    }

    public void ClearSlot(MappingRowViewModel? row)
    {
        if (row is null || !row.IsFilled || row.IsLocked) return;
        PushHistory();
        row.SetLink(null, SourceIsEmpty, SamplesFor);
        AfterMappingEdit();
    }

    public void SetHidden(MappingRowViewModel? row, bool hidden, Action<string> showStatus)
    {
        if (row is null || row.IsHidden == hidden || row.IsLocked) return;
        if (hidden && row.IsFilled)
        {
            showStatus($"“{row.TargetLabel}” is mapped — clear it before hiding.");
            return;
        }
        PushHistory();
        row.IsHidden = hidden;
        AfterMappingEdit();
    }

    public void ClearGrid()
    {
        Rows.Clear();
        SourceColumns.Clear();
        ResetHistory();
        AfterMappingEdit();
    }

    private void AfterMappingEdit()
    {
        RefreshSourceLinkFlags();
        OnPropertyChanged(nameof(HasMatched));
        OnPropertyChanged(nameof(LinkedCount));
        OnPropertyChanged(nameof(SourceMappedCount));
        OnPropertyChanged(nameof(SourceUnmappedCount));
        OnPropertyChanged(nameof(TargetMappedCount));
        OnPropertyChanged(nameof(TargetUnmappedCount));
        OnPropertyChanged(nameof(ExactCount));
        OnPropertyChanged(nameof(AliasCount));
        OnPropertyChanged(nameof(QualifiedCount));
        OnPropertyChanged(nameof(FuzzyCount));
        OnPropertyChanged(nameof(VisibleSourceColumns));
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(MappedRows));
    }

    public void CopyMapping(MappingRowViewModel? targetRow, MappingRowViewModel? sourceRow)
    {
        if (targetRow is null || targetRow.IsLocked || sourceRow is null || !sourceRow.IsFilled) return;
        PushHistory();
        if (sourceRow.IsConstant)
        {
            targetRow.SetConstant(sourceRow.ConstantValue!, SampleRowCount);
        }
        else if (sourceRow.IsLinked)
        {
            targetRow.SetLink(sourceRow.LinkedSource, SourceIsEmpty, SamplesFor);
        }
        AfterMappingEdit();
    }

    public void UnmapSource(SourceColumnViewModel? source)
    {
        if (source is null || !source.IsLinked) return;
        PushHistory();
        var targetsToClear = Rows.Where(r => r.IsLinked && r.LinkedSource?.ColumnIndex == source.Column.ColumnIndex).ToList();
        foreach (var target in targetsToClear)
        {
            target.SetLink(null, SourceIsEmpty, SamplesFor);
        }
        AfterMappingEdit();
    }

    private bool SourceIsEmpty(HeaderColumn source) =>
        _sourceData?.ColumnIsEmpty(source.ColumnIndex, _matchedSrcHeaderRow) ?? false;

    private IReadOnlyList<string> SamplesFor(HeaderColumn source) =>
        _sourceSamples.TryGetValue(source.ColumnIndex, out var s) ? s : Array.Empty<string>();

    private void RefreshSourceLinkFlags()
    {
        var linked = Rows.Where(r => r.IsLinked && !r.IsHidden)
                         .GroupBy(r => r.LinkedSource!.ColumnIndex)
                         .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => r.TargetLabel)));
        foreach (var s in SourceColumns)
        {
            s.IsLinked = linked.ContainsKey(s.Column.ColumnIndex);
            s.MappedTargetLabel = s.IsLinked ? linked[s.Column.ColumnIndex] : null;
        }
    }
}
