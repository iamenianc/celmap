using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    [ObservableProperty]
    private bool _fuzzyEnabled = true;

    public int ConfidenceThreshold => 90;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Drop a source and a target file, check the header rows, then Map.";

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
        IsBusy = true;
        Status = "Matching…";
        try
        {
            string sourcePath = Setup.SourceFilePath!;
            string targetPath = Setup.TargetFilePath!;
            string sourceSheet = Setup.SelectedSourceSheet!;
            string targetSheet = Setup.SelectedTargetSheet!;
            string? sourcePassword = Setup.SourcePassword;
            int srcHeaderRow = Setup.SourceHeaderRow - 1;
            int tgtHeaderRow = Setup.TargetHeaderRow - 1;
            bool isFuzzyEnabled = FuzzyEnabled;

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

            var (sourceData, targetData, sourceHeaders, targetHeaders, result) = await Task.Run(() =>
            {
                var src = _reader.ReadSheet(sourcePath, sourceSheet, sourcePassword);
                var tgt = _reader.ReadSheet(targetPath, targetSheet);
                var srcH = HeaderExtractor.Extract(src, srcHeaderRow);
                var tgtH = HeaderExtractor.Extract(tgt, tgtHeaderRow);
                var res = _matcher.Match(
                    srcH, tgtH,
                    new MatcherOptions(ConfidenceThreshold: 90, FuzzyEnabled: isFuzzyEnabled, ActiveCovers: activeCovers),
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

            Mapping.Populate(result, sourceHeaders, sourceData, srcHeaderRow, _sourceSamples, Parameters, _aliases);

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
        var cells = new List<string>(SampleRowCount);
        for (int r = headerRow0 + 1; r <= headerRow0 + SampleRowCount; r++)
            cells.Add(r < data.RowCount ? data.GetCell(r, colIndex).ToString() : string.Empty);
        return cells;
    }

    partial void OnFuzzyEnabledChanged(bool value)
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
            new MatcherOptions(ConfidenceThreshold: 90, FuzzyEnabled: value, ActiveCovers: activeCovers),
            s => _sourceData.ColumnIsEmpty(s.ColumnIndex, _matchedSrcHeaderRow));

        Mapping.RepopulateAfterMatchRuleChange(result, manual, hidden, Parameters, _aliases);
        Status = $"Re-applied match rules (Fuzzy: {(value ? "On (90%)" : "Off")}). {Mapping.LinkedCount} column(s) linked.";
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

        int filledCount = columnMap.Count + constantColumns.Count;
        if (filledCount == 0)
        {
            Status = "Nothing mapped — pick a source or type a value for at least one target column before writing.";
            return;
        }

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
