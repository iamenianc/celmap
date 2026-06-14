using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace CelMap.App;

/// <summary>
/// Coordinator ViewModel that drives the application screen flow and coordinates the sub-ViewModels.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int SampleRowCount = 100;

    private readonly IWorkbookReader _reader;
    private readonly IColumnMatcher _matcher;
    private readonly ITargetWriter _writer;
    private readonly AliasRules _aliases;
    private readonly QualifiedRules _qualified;

    // Cached sheet data + headers from the last Match
    private SheetData? _sourceData;
    private SheetData? _targetData;
    private IReadOnlyList<HeaderColumn> _sourceHeaders = Array.Empty<HeaderColumn>();
    private IReadOnlyList<HeaderColumn> _targetHeaders = Array.Empty<HeaderColumn>();
    private int _matchedSrcHeaderRow;
    private int _matchedTgtHeaderRow;
    private string _matchedSourceSheet = "";
    private string _matchedTargetSheet = "";
    private Dictionary<int, IReadOnlyList<string>> _sourceSamples = new();

    public ParametersViewModel Parameters { get; }
    public SetupViewModel Setup { get; }
    public MappingViewModel Mapping { get; }

    public MainViewModel()
        : this(new WorkbookReader()) { }

    public MainViewModel(IWorkbookReader reader)
    {
        _reader = reader;
        _aliases = AliasRules.LoadDefault();
        _qualified = QualifiedRules.LoadDefault();
        _matcher = new ColumnMatcher(_aliases, _qualified);
        _writer = new TargetWriter(_reader);

        Parameters = new ParametersViewModel();
        Mapping = new MappingViewModel();
        Mapping.SourceSheetChanged = OnMappingSourceSheetChanged;
        Setup = new SetupViewModel(_reader, msg => Status = msg, ResetGrid);

        OutputDirectory = @"C:\temp";

        // Listen to child PropertyChanged events to forward notifications
        Parameters.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ParametersViewModel.IsParametersValid))
            {
                MatchCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsParametersValid));
            }
        };

        Setup.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SetupViewModel.SourceFilePath) ||
                e.PropertyName == nameof(SetupViewModel.SelectedSourceSheet) ||
                e.PropertyName == nameof(SetupViewModel.TargetFilePath) ||
                e.PropertyName == nameof(SetupViewModel.SelectedTargetSheet))
            {
                MatchCommand.NotifyCanExecuteChanged();
            }
        };
    }

    // ====================================================================== //
    //  Display density (all screens)                                         //
    // ====================================================================== //

    public IReadOnlyList<string> DensityOptions { get; } = new[] { "Large", "Comfortable", "Compact" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiScale))]
    private string _uiDensity = "Comfortable";

    public double UiScale => UiDensity switch
    {
        "Large" => 1.2,
        "Compact" => 0.85,
        _ => 1.0
    };

    // ====================================================================== //
    //  Screen state                                                          //
    // ====================================================================== //

    [ObservableProperty]
    private bool _isOnParameters;

    [ObservableProperty]
    private bool _isOnSetup = true;

    [ObservableProperty]
    private bool _isOnMapping;

    public bool IsParametersValid => Parameters.IsParametersValid;

    [RelayCommand]
    private void BackToSetup()
    {
        IsOnParameters = false;
        IsOnSetup = true;
        IsOnMapping = false;
    }

    // ====================================================================== //
    //  Output / write options                                                //
    // ====================================================================== //

    /// <summary>Auto-apply strong fuzzy matches at the 90% floor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableWeakFuzzy))]
    private bool _fuzzyEnabled = true;

    /// <summary>Also auto-apply weaker fuzzy matches (70–89%). Only meaningful while
    /// <see cref="FuzzyEnabled"/> is on — weak fuzzy relaxes the strong floor, it isn't
    /// an independent mode.</summary>
    [ObservableProperty]
    private bool _weakFuzzyEnabled;

    /// <summary>Weak fuzzy can only be toggled while strong fuzzy is on.</summary>
    public bool CanEnableWeakFuzzy => FuzzyEnabled;

    public int ConfidenceThreshold => 90;
    public int WeakFuzzyThreshold => 70;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Drop a source and a target file, check the header rows, then Map.";

    /// <summary>Queue driving the success toast shown after a write (see MappingView's Snackbar).</summary>
    public SnackbarMessageQueue StatusMessages { get; } = new(TimeSpan.FromSeconds(5));

    /// <summary>Pop a transient toast announcing a successful write, with an action to open the file.</summary>
    private void FlashStatus()
    {
        StatusMessages.Enqueue(
            content: $"Done — {Mapping.LinkedCount} column(s) written.",
            actionContent: "OPEN",
            actionHandler: _ => OpenOutputFile(),
            actionArgument: null,
            promote: false,
            neverConsiderToBeDuplicate: true,
            durationOverride: TimeSpan.FromSeconds(6));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputFile))]
    private string? _outputFilePath;

    public bool HasOutputFile => !string.IsNullOrEmpty(OutputFilePath);

    [RelayCommand]
    private void OpenOutputFile()
    {
        if (string.IsNullOrEmpty(OutputFilePath) || !File.Exists(OutputFilePath))
        {
            Status = "The output file is no longer available.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OutputFilePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open the file: {ex.Message}";
        }
    }

    // ====================================================================== //
    //  Operations                                                           //
    // ====================================================================== //

    private bool CanMatch =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Setup.SourceFilePath)
        && !string.IsNullOrWhiteSpace(Setup.TargetFilePath)
        && !string.IsNullOrWhiteSpace(Setup.SelectedSourceSheet)
        && !string.IsNullOrWhiteSpace(Setup.SelectedTargetSheet)
        && Parameters.IsParametersValid;

    [RelayCommand(CanExecute = nameof(CanMatch))]
    private async Task MatchAsync()
    {
        await RunMatchAsync(Setup.SelectedSourceSheet!, Setup.SourceHeaderRow - 1, detectHeader: false);
    }

    /// <summary>
    /// Re-runs the match against a different source sheet (chosen from the mapper dropdown),
    /// auto-detecting the new sheet's header row. Existing mappings are dropped since the
    /// source columns change.
    /// </summary>
    private async void OnMappingSourceSheetChanged(string sourceSheet)
    {
        if (IsBusy) return;
        await RunMatchAsync(sourceSheet, srcHeaderRow0: null, detectHeader: true);
    }

    private async Task RunMatchAsync(string sourceSheet, int? srcHeaderRow0, bool detectHeader)
    {
        IsBusy = true;
        Status = "Matching…";
        try
        {
            string sourcePath = Setup.SourceFilePath!;
            string targetPath = Setup.TargetFilePath!;
            string targetSheet = Setup.SelectedTargetSheet!;
            string? sourcePassword = Setup.SourcePassword;
            int tgtHeaderRow = Setup.TargetHeaderRow - 1;
            bool isFuzzyEnabled = FuzzyEnabled;
            bool isWeakFuzzyEnabled = WeakFuzzyEnabled;

            var activeCovers = new HashSet<string>();
            if (Parameters.DefaultCoverGSC) activeCovers.Add("GSC");
            if (Parameters.DefaultCoverGL) activeCovers.Add("GL");
            if (Parameters.DefaultCoverTPD) activeCovers.Add("TPD");
            if (Parameters.DefaultCoverTrauma) activeCovers.Add("Trauma");
            if (Parameters.DefaultCoverGLTPD) activeCovers.Add("GLTPD");
            foreach (var cov in Parameters.CategoryOverrides)
            {
                if (cov.IsEnabled && cov.IsCategoryNameValid)
                {
                    if (cov.Gsc) activeCovers.Add("GSC");
                    if (cov.Gl) activeCovers.Add("GL");
                    if (cov.Tpd) activeCovers.Add("TPD");
                    if (cov.Trauma) activeCovers.Add("Trauma");
                    if (cov.GlTpd) activeCovers.Add("GLTPD");
                }
            }

            var (sourceData, targetData, sourceHeaders, targetHeaders, result, srcHeaderRow) = await Task.Run(() =>
            {
                var src = _reader.ReadSheet(sourcePath, sourceSheet, sourcePassword);
                var tgt = _reader.ReadSheet(targetPath, targetSheet);
                int srcHdr = detectHeader ? HeaderRowDetector.Detect(src) : srcHeaderRow0!.Value;
                var srcH = HeaderExtractor.Extract(src, srcHdr);
                var tgtH = HeaderExtractor.Extract(tgt, tgtHeaderRow);
                var res = _matcher.Match(
                    srcH, tgtH,
                    new MatcherOptions(ConfidenceThreshold: 90, FuzzyEnabled: isFuzzyEnabled,
                                       ActiveCovers: activeCovers,
                                       WeakFuzzyEnabled: isWeakFuzzyEnabled, WeakFuzzyThreshold: 70),
                    s => src.ColumnIsEmpty(s.ColumnIndex, srcHdr));
                return (src, tgt, srcH, tgtH, res, srcHdr);
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

            Mapping.Populate(result, sourceHeaders, sourceData, srcHeaderRow, _sourceSamples, Parameters, _aliases);

            // Keep Setup's header row in step so a later re-Match from Setup uses the detected row,
            // and feed the mapper's sheet dropdown with this workbook's sheets.
            if (detectHeader) Setup.SourceHeaderRow = srcHeaderRow + 1;
            Mapping.SetSourceSheets(await Task.Run(() =>
            {
                var allNames = _reader.GetSheetNames(sourcePath, sourcePassword);
                var validNames = new System.Collections.Generic.List<string>();
                foreach (var name in allNames)
                {
                    var sheet = _reader.ReadSheet(sourcePath, name, sourcePassword);
                    if (sheet.RowCount > 0)
                    {
                        int headerRow0 = CelMap.Core.HeaderRowDetector.Detect(sheet);
                        if (sheet.RowCount - (headerRow0 + 1) > 0)
                        {
                            validNames.Add(name);
                        }
                    }
                }
                return (System.Collections.Generic.IReadOnlyList<string>)validNames;
            }), sourceSheet);

            int auto = Mapping.Rows.Count(r => r.IsLinked);
            int paramFilled = Mapping.Rows.Count(r => r.IsFilled && !r.IsLinked);
            Status = $"Filled {auto + paramFilled} of {Mapping.Rows.Count} target columns — "
                   + $"{auto} auto-matched, {paramFilled} from parameters "
                   + $"({_aliases.Groups.Count} synonym groups loaded). "
                   + "Review the amber fuzzy matches, map the rest, then Execute.";

            IsOnParameters = false;
            IsOnSetup = false;
            IsOnMapping = true;
            WriteCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Status = FriendlyError(ex);
            Mapping.ClearGrid();
        }
        finally
        {
            IsBusy = false;
        }
    }

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
        // Only emit cells for rows that actually exist in the sheet below the header,
        // capped at SampleRowCount. Padding out to a fixed count made the grid render
        // empty rows you could scroll into past the real data.
        int rows = ActualDataRowCount(data, headerRow0);
        var cells = new List<string>(rows);
        for (int r = headerRow0 + 1; r <= headerRow0 + rows; r++)
            cells.Add(data.GetCell(r, colIndex).ToString());
        return cells;
    }

    /// <summary>Number of data rows below the header that actually exist in the sheet,
    /// capped at the preview limit. Drives how many rows the mapper grids render.</summary>
    private static int ActualDataRowCount(SheetData data, int headerRow0) =>
        Math.Clamp(data.RowCount - (headerRow0 + 1), 0, SampleRowCount);

    partial void OnFuzzyEnabledChanged(bool value)
    {
        // Weak fuzzy is a relaxation of strong fuzzy; it can't stand on its own. Turning
        // strong off clears it (which re-enters this path via WeakFuzzyEnabled's setter,
        // but ReapplyFuzzyRules is idempotent so a second re-match is harmless).
        if (!value && WeakFuzzyEnabled)
        {
            WeakFuzzyEnabled = false;
            return;
        }
        ReapplyFuzzyRules();
    }

    partial void OnWeakFuzzyEnabledChanged(bool value) => ReapplyFuzzyRules();

    /// <summary>Re-run the matcher against the loaded sheets with the current fuzzy toggles,
    /// preserving manual overrides and hidden columns. Shared by both fuzzy toggles.</summary>
    private void ReapplyFuzzyRules()
    {
        if (Mapping.Rows.Count == 0 || _sourceData is null) return;

        var manual = Mapping.Rows.Where(r => r.IsManualOverride)
                         .ToDictionary(r => r.TargetColumn.ColumnIndex, r => r.LinkedSource);
        var hidden = Mapping.Rows.Where(r => r.IsHidden).Select(r => r.TargetColumn.ColumnIndex).ToHashSet();

        var activeCovers = new HashSet<string>();
        if (Parameters.DefaultCoverGSC) activeCovers.Add("GSC");
        if (Parameters.DefaultCoverGL) activeCovers.Add("GL");
        if (Parameters.DefaultCoverTPD) activeCovers.Add("TPD");
        if (Parameters.DefaultCoverTrauma) activeCovers.Add("Trauma");
        if (Parameters.DefaultCoverGLTPD) activeCovers.Add("GLTPD");
        foreach (var cov in Parameters.CategoryOverrides)
        {
            if (cov.IsEnabled && cov.IsCategoryNameValid)
            {
                if (cov.Gsc) activeCovers.Add("GSC");
                if (cov.Gl) activeCovers.Add("GL");
                if (cov.Tpd) activeCovers.Add("TPD");
                if (cov.Trauma) activeCovers.Add("Trauma");
                if (cov.GlTpd) activeCovers.Add("GLTPD");
            }
        }

        var result = _matcher.Match(
            _sourceHeaders, _targetHeaders,
            new MatcherOptions(ConfidenceThreshold: 90, FuzzyEnabled: FuzzyEnabled,
                               ActiveCovers: activeCovers,
                               WeakFuzzyEnabled: WeakFuzzyEnabled, WeakFuzzyThreshold: 70),
            s => _sourceData.ColumnIsEmpty(s.ColumnIndex, _matchedSrcHeaderRow));

        Mapping.RepopulateAfterMatchRuleChange(result, manual, hidden, Parameters, _aliases);
        string fuzzyState = !FuzzyEnabled ? "Off"
            : WeakFuzzyEnabled ? "On (70%+)"
            : "On (90%+)";
        Status = $"Re-applied match rules (Fuzzy: {fuzzyState}). {Mapping.LinkedCount} column(s) linked.";
    }

    private bool CanWrite => Mapping.Rows.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var columnMap = new Dictionary<int, int>();
        var constantColumns = new Dictionary<int, string>();
        foreach (var row in Mapping.Rows)
        {
            if (row.IsHidden) continue;
            if (row.ConstantValue is { } constant)
                constantColumns[row.TargetColumn.ColumnIndex] = constant;
            else if (row.LinkedSource is { } src)
                columnMap[src.ColumnIndex] = row.TargetColumn.ColumnIndex;
        }

        // An empty mapping is allowed: it produces a clean copy of the target with no
        // source columns filled (useful for exporting the target template as-is).
        if (!GroupIdRowCountMatchesData(out string mismatch))
        {
            Status = mismatch;
            return;
        }

        IsBusy = true;
        Status = "Writing…";
        OutputFilePath = null;
        try
        {
            string sourcePath = Setup.SourceFilePath!;
            string targetPath = Setup.TargetFilePath!;
            string outputDir = OutputDirectory;
            string? sourcePassword = Setup.SourcePassword;

            var defaultCovers = new HashSet<string>();
            if (Parameters.DefaultCoverGSC) defaultCovers.Add("GSC");
            if (Parameters.DefaultCoverGL) defaultCovers.Add("GL");
            if (Parameters.DefaultCoverTPD) defaultCovers.Add("TPD");
            if (Parameters.DefaultCoverTrauma) defaultCovers.Add("Trauma");
            if (Parameters.DefaultCoverGLTPD) defaultCovers.Add("GLTPD");

            var categoryCovers = new Dictionary<string, IReadOnlySet<string>>();
            foreach (var cov in Parameters.CategoryOverrides)
            {
                if (cov.IsEnabled)
                {
                    var set = new HashSet<string>();
                    if (cov.Gsc) set.Add("GSC");
                    if (cov.Gl) set.Add("GL");
                    if (cov.Tpd) set.Add("TPD");
                    if (cov.Trauma) set.Add("Trauma");
                    if (cov.GlTpd) set.Add("GLTPD");
                    categoryCovers[cov.CategoryName] = set;
                }
            }

            var insParams = new InsuranceParams(defaultCovers, categoryCovers);

            var writeResult = await Task.Run(() => _writer.Write(new WriteRequest(
                sourcePath, _matchedSourceSheet, _matchedSrcHeaderRow,
                targetPath, _matchedTargetSheet, _matchedTgtHeaderRow,
                columnMap, outputDir, constantColumns, insParams, sourcePassword, Parameters.GroupIdText)));

            string warnings = writeResult.Warnings.Count > 0
                ? "\nWarnings:\n  • " + string.Join("\n  • ", writeResult.Warnings)
                : string.Empty;

            Status = $"Done. {columnMap.Count} column(s) mapped, "
                   + $"{writeResult.RowsWritten} row(s) written.{warnings}\n"
                   + "Output:";
            OutputFilePath = writeResult.OutputFilePath;
            FlashStatus();
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

    private bool GroupIdRowCountMatchesData(out string reason)
    {
        reason = "";
        if (_sourceData is null) return true;

        bool groupIdWritten = Mapping.Rows.Any(r =>
            !r.IsHidden && r.IsConstant && _aliases.AreAliases(r.TargetLabel, "GroupID"));
        if (!groupIdWritten) return true;

        int groupIdSpan = Math.Max(0, _sourceData.RowCount - (_matchedSrcHeaderRow + 1));

        int maxDataSpan = 0;
        foreach (var row in Mapping.Rows)
        {
            if (row.IsHidden || row.LinkedSource is not { } src) continue;
            int span = _sourceData.PopulatedRowSpan(src.ColumnIndex, _matchedSrcHeaderRow);
            if (span > maxDataSpan) maxDataSpan = span;
        }

        if (groupIdSpan > maxDataSpan)
        {
            reason = $"Export blocked — GroupID would fill {groupIdSpan} row(s) but the longest "
                   + $"mapped data column only reaches {maxDataSpan} row(s). GroupID cannot be the "
                   + "longest column. Check the source header row and that your data columns are fully mapped.";
            return false;
        }
        return true;
    }

    private void ResetGrid()
    {
        Mapping.ClearGrid();
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is IOException)
            return "Could not open a file — it may be open in Excel. "
                 + "Close it there and try again.\n(" + ex.Message + ")";
        return $"Error: {ex.Message}";
    }
}
