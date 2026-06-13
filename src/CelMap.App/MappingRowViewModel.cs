using System;
using System.Collections.Generic;
using System.Linq;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

/// <summary>
/// One target column in the Excel-like mapping grid. Wraps the engine's
/// <see cref="TargetColumnMapping"/> but is mutable so the user can override the linked
/// source by clicking (Tracer 4). The engine's original outcome is kept so we can show
/// the auto tier/score/status alongside any manual override.
///
/// Rendered as a vertical strip: a header cell on top, then a body that shows the linked
/// source's sample rows (<see cref="SampleCells"/>) — a live preview of what the output
/// column will hold — or nothing when blank. Mapping is chosen via the header's right-click
/// "Map" submenu. Target column order is fixed; rows never reorder.
/// </summary>
public sealed partial class MappingRowViewModel : ObservableObject
{
    /// <summary>The engine's original (auto) outcome for this target column.</summary>
    public TargetColumnMapping Original { get; }

    public HeaderColumn TargetColumn => Original.TargetColumn;
    public string TargetLabel => string.IsNullOrWhiteSpace(TargetColumn.Label)
        ? $"(column {TargetColumn.ColumnIndex + 1})"
        : TargetColumn.Label;

    /// <summary>Label shown on the collapsed strip when the column is hidden — the first 16
    /// characters of the column name, so it stays readable in the narrow strip.</summary>
    public string HiddenLabel =>
        TargetLabel.Length > 16 ? TargetLabel[..16] : TargetLabel;

    /// <summary>The source column currently linked to this target — null if unmapped.
    /// Set by auto-match initially, then overridable by clicking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkedSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsLinked))]
    [NotifyPropertyChangedFor(nameof(IsFilled))]
    [NotifyPropertyChangedFor(nameof(BodyState))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsManualOverride))]
    [NotifyPropertyChangedFor(nameof(IsFuzzyAuto))]
    private HeaderColumn? _linkedSource;

    /// <summary>A typed literal that fills EVERY data row of this target column, instead of a
    /// mapped source. Mutually exclusive with <see cref="LinkedSource"/>. Null when unset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkedSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsConstant))]
    [NotifyPropertyChangedFor(nameof(IsFilled))]
    [NotifyPropertyChangedFor(nameof(BodyState))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _constantValue;

    /// <summary>The sample rows shown in the body — mirrors the linked source's values,
    /// empty when nothing is linked.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _sampleCells = Array.Empty<string>();

    /// <summary>True when the user has changed the link away from the engine's auto pick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsFuzzyAuto))]
    private bool _isManualOverride;

    /// <summary>Hidden columns collapse to a thin restore strip and are excluded from the
    /// write (PRD §2.3 hide/show).</summary>
    [ObservableProperty]
    private bool _isHidden;

    /// <summary>True if this target column has no data below its header (PRD §2.3 warning).</summary>
    [ObservableProperty]
    private bool _linkedSourceIsEmpty;

    public bool IsLocked
    {
        get
        {
            string label = TargetLabel;
            return label.Equals("GroupID", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Group ID", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("InsurerID", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Insurer ID", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Insurer", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("ReviewDate", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Review Date", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Review Start Date", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Review Start", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("ReviewStart", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Review End", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Review End Date", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Cover End Date", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("Coverage End Date", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("GSC", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("GSC Cover", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("GL", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("GL Cover", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("TPD", StringComparison.OrdinalIgnoreCase) ||
                   label.Equals("TPD Cover", StringComparison.OrdinalIgnoreCase);
        }
    }

    public MappingRowViewModel(TargetColumnMapping original)
    {
        Original = original;
        _linkedSource = original.Status == MatchStatus.Auto ? original.MatchedSource : null;
        _linkedSourceIsEmpty = _linkedSource is not null && original.SourceColumnIsEmpty;
        if (_linkedSource is not null)
            _sampleCells = Array.Empty<string>();   // filled later via SetLink with samples
    }

    /// <summary>True when a source column is mapped here (not a constant).</summary>
    public bool IsLinked => LinkedSource is not null;

    /// <summary>True when this column is filled by a typed constant.</summary>
    public bool IsConstant => ConstantValue is not null;

    /// <summary>True when the column has any content to write — a source OR a constant.</summary>
    public bool IsFilled => IsLinked || IsConstant;

    /// <summary>Which body the slot shows: "Preview" (filled, showing the data) or "Blank"
    /// (empty — right-click the header → Map to fill it).</summary>
    public string BodyState =>
        IsFilled ? "Preview"
        : "Blank";

    public string LinkedSourceLabel =>
        IsConstant ? $"“{ConstantValue}” (typed)"
        : LinkedSource is { } s
            ? (string.IsNullOrWhiteSpace(s.Label) ? $"(column {s.ColumnIndex + 1})" : s.Label)
            : "—";

    /// <summary>Score of the engine's best candidate (0 if none). Tier is from the original run.</summary>
    public int Score => Original.Score;
    public MatchKind Kind => Original.Candidates.Count > 0 ? Original.Candidates[0].Kind : MatchKind.Fuzzy;
    public string KindText => Original.MatchedSource is not null || Original.Candidates.Count > 0
        ? Kind.ToString()
        : "—";

    /// <summary>Human-readable status that folds in manual overrides and the rules-engine outcomes.</summary>
    public string StatusText
    {
        get
        {
            if (IsConstant) return "Typed value";
            if (IsManualOverride && IsLinked)
                return "Manual";

            return Original.Status switch
            {
                MatchStatus.Auto => "Auto",
                MatchStatus.Ambiguous => "Ambiguous — pick one",
                MatchStatus.NeedsReview when IsQualifiedConcept => "Needs review (qualify)",
                MatchStatus.NeedsReview => "Needs review",
                MatchStatus.Unmatched when IsStrict => "Manual map required",
                MatchStatus.Unmatched => "Unmatched",
                _ => Original.Status.ToString()
            };
        }
    }

    /// <summary>True while this slot still holds the engine's fuzzy auto-pick — the matches
    /// worth a human glance, tinted amber in the grid.</summary>
    public bool IsFuzzyAuto =>
        IsLinked && !IsManualOverride
        && Original.Status == MatchStatus.Auto && Kind == MatchKind.Fuzzy;

    /// <summary>Strict targets are deliberately left unmatched (fuzzy suppressed) — flag, don't hide.</summary>
    public bool IsStrict => Original is { Status: MatchStatus.Unmatched, Score: 0, Candidates.Count: 0 };

    /// <summary>A qualified-rule concept that came back ambiguous (e.g. bare CategoryNo).</summary>
    public bool IsQualifiedConcept =>
        Original.Status == MatchStatus.NeedsReview && Original.Candidates.Count > 1;

    /// <summary>Candidate sources the engine surfaced (for ambiguous/qualified rows) — the obvious picks.</summary>
    public IReadOnlyList<MatchCandidate> Candidates => Original.Candidates;

    /// <summary>Apply a user click: link this target to the given source (or clear with null).
    /// Pulls the source's sample rows into the body and closes the picker when linked.</summary>
    public void SetLink(HeaderColumn? source, Func<HeaderColumn, bool> sourceIsEmpty,
                        Func<HeaderColumn, IReadOnlyList<string>> sampleFor)
    {
        var autoPick = Original.Status == MatchStatus.Auto ? Original.MatchedSource : null;
        IsManualOverride = source?.ColumnIndex != autoPick?.ColumnIndex;
        ConstantValue = null;            // a source mapping replaces any typed constant
        LinkedSource = source;
        LinkedSourceIsEmpty = source is not null && sourceIsEmpty(source);
        SampleCells = source is null ? Array.Empty<string>() : sampleFor(source);
    }

    /// <summary>Fill this target with a typed literal applied to every data row. Replaces any
    /// mapped source. The preview repeats the value down <paramref name="previewRows"/> rows.</summary>
    public void SetConstant(string value, int previewRows)
    {
        LinkedSource = null;
        LinkedSourceIsEmpty = false;
        IsManualOverride = false;
        ConstantValue = value;
        SampleCells = Enumerable.Repeat(value, previewRows).ToList();
    }
}
