using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

/// <summary>One source column in the left pane of the click-to-map UI.</summary>
public sealed partial class SourceColumnViewModel : ObservableObject
{
    public HeaderColumn Column { get; }

    public string Label => string.IsNullOrWhiteSpace(Column.Label)
        ? $"(column {Column.ColumnIndex + 1})"
        : Column.Label;

    /// <summary>True once this source is linked to at least one target — shown as a check.</summary>
    [ObservableProperty]
    private bool _isLinked;

    /// <summary>True while this is the source the user has clicked and is about to link.</summary>
    [ObservableProperty]
    private bool _isPicked;

    /// <summary>True if this source column has no data below the header (PRD §2.3 warning).</summary>
    public bool IsEmpty { get; }

    public SourceColumnViewModel(HeaderColumn column, bool isEmpty)
    {
        Column = column;
        IsEmpty = isEmpty;
    }
}
