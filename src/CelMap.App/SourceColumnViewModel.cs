using System.Collections.Generic;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

/// <summary>
/// One source column in the Excel-like mapping screen. Carries its header label and a
/// short strip of sample cell values (the first ~10 rows below the header) so the top
/// grid can render the column exactly as it looks in Excel, and so a target slot mapped
/// to this source can mirror the same preview.
/// </summary>
public sealed partial class SourceColumnViewModel : ObservableObject
{
    public HeaderColumn Column { get; }

    public string Label => string.IsNullOrWhiteSpace(Column.Label)
        ? $"(column {Column.ColumnIndex + 1})"
        : Column.Label;

    /// <summary>First ~10 data values below this column's header, for the preview grid.</summary>
    public IReadOnlyList<string> SampleCells { get; }

    /// <summary>True once this source is linked to at least one target — shown as a check.</summary>
    [ObservableProperty]
    private bool _isLinked;
    
    /// <summary>The labels of target columns this source is mapped to (joined if multiple).</summary>
    [ObservableProperty]
    private string? _mappedTargetLabel;

    /// <summary>True while this is the source the user has clicked and is about to link.</summary>
    [ObservableProperty]
    private bool _isPicked;

    /// <summary>True while the user is hovering the partner column on the other grid — used to
    /// flash the link so you can see, by content, what's mapped to what.</summary>
    [ObservableProperty]
    private bool _isHoverHighlighted;

    /// <summary>True if this source column has no data below the header (PRD §2.3 warning).</summary>
    public bool IsEmpty { get; }

    public SourceColumnViewModel(HeaderColumn column, bool isEmpty, IReadOnlyList<string> sampleCells)
    {
        Column = column;
        IsEmpty = isEmpty;
        SampleCells = sampleCells;
    }
}
