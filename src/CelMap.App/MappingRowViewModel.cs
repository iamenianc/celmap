using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

/// <summary>
/// One target column in the interactive grid. Wraps the engine's
/// <see cref="TargetColumnMapping"/> but is mutable so the user can override the
/// linked source by clicking (Tracer 4). The engine's original outcome is kept so
/// we can show the auto tier/score/status alongside any manual override.
/// </summary>
public sealed partial class MappingRowViewModel : ObservableObject
{
    /// <summary>The engine's original (auto) outcome for this target column.</summary>
    public TargetColumnMapping Original { get; }

    public HeaderColumn TargetColumn => Original.TargetColumn;
    public string TargetLabel => string.IsNullOrWhiteSpace(TargetColumn.Label)
        ? $"(column {TargetColumn.ColumnIndex + 1})"
        : TargetColumn.Label;

    /// <summary>The source column currently linked to this target — null if unmapped.
    /// Set by auto-match initially, then overridable by clicking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkedSourceLabel))]
    [NotifyPropertyChangedFor(nameof(IsLinked))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsManualOverride))]
    [NotifyPropertyChangedFor(nameof(GroupKey))]
    [NotifyPropertyChangedFor(nameof(GroupSort))]
    private HeaderColumn? _linkedSource;

    /// <summary>True when the user has changed the link away from the engine's auto pick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsAutoLinked))]
    [NotifyPropertyChangedFor(nameof(GroupKey))]
    [NotifyPropertyChangedFor(nameof(GroupSort))]
    private bool _isManualOverride;

    /// <summary>Hidden rows are excluded from the write (PRD §2.3 hide/show).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupKey))]
    [NotifyPropertyChangedFor(nameof(GroupSort))]
    private bool _isHidden;

    /// <summary>True when the linked source column has no data below its header (PRD §2.3 warning).</summary>
    [ObservableProperty]
    private bool _linkedSourceIsEmpty;

    public MappingRowViewModel(TargetColumnMapping original)
    {
        Original = original;
        _linkedSource = original.Status == MatchStatus.Auto ? original.MatchedSource : null;
        _linkedSourceIsEmpty = _linkedSource is not null && original.SourceColumnIsEmpty;
    }

    public bool IsLinked => LinkedSource is not null;

    /// <summary>A still-linked row that came from the engine's auto pick (not yet rejected
    /// or overridden). These sit in the "confirm" section — click to reject.</summary>
    public bool IsAutoLinked => IsLinked && !IsManualOverride;

    /// <summary>Section a row sorts into. Order reflects the workflow:
    /// 1) Needs manual mapping (the focus) → 2) Auto-matched, confirm/reject →
    /// 3) Manually mapped → 4) Hidden. Mapped rows move out of the way of the
    /// remaining work, exactly as asked.</summary>
    public string GroupKey =>
        IsHidden ? "Hidden — not written"
        : !IsLinked ? "Needs mapping — click a source, then this row"
        : IsManualOverride ? "Manually mapped"
        : "Auto-matched — click a row to reject it";

    /// <summary>Sort key driving group order: needs-mapping (0) → auto (1) → manual (2) → hidden (3).</summary>
    public int GroupSort =>
        IsHidden ? 3
        : !IsLinked ? 0
        : IsManualOverride ? 2
        : 1;

    public string LinkedSourceLabel => LinkedSource is { } s
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
            // A linked override reads as "Manual"; anything unlinked (including a
            // rejected auto-match) falls through to its engine status so the
            // Needs-mapping section reads consistently.
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

    /// <summary>Strict targets are deliberately left unmatched (fuzzy suppressed) — flag, don't hide.</summary>
    public bool IsStrict => Original is { Status: MatchStatus.Unmatched, Score: 0, Candidates.Count: 0 };

    /// <summary>A qualified-rule concept that came back ambiguous (e.g. bare CategoryNo).</summary>
    public bool IsQualifiedConcept =>
        Original.Status == MatchStatus.NeedsReview && Original.Candidates.Count > 1;

    /// <summary>Candidate sources the engine surfaced (for ambiguous/qualified rows) — the obvious picks.</summary>
    public IReadOnlyList<MatchCandidate> Candidates => Original.Candidates;

    /// <summary>Apply a user click: link this target to the given source (or clear with null).</summary>
    public void SetLink(HeaderColumn? source, Func<HeaderColumn, bool> sourceIsEmpty)
    {
        // Decide override-ness against the engine's auto pick.
        var autoPick = Original.Status == MatchStatus.Auto ? Original.MatchedSource : null;
        IsManualOverride = source?.ColumnIndex != autoPick?.ColumnIndex;
        LinkedSource = source;
        LinkedSourceIsEmpty = source is not null && sourceIsEmpty(source);
    }
}
