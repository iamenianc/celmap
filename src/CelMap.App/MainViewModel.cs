using System.Collections.ObjectModel;
using System.IO;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CelMap.App;

/// <summary>
/// Orchestrates <see cref="CelMap.Core"/> for the WPF shell. Holds no mapping
/// logic of its own — it drives the same read → match → write pipeline the CLI
/// runs (see CelMap.Cli/Program.cs), proving the UI/core split (PRD §9).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IWorkbookReader _reader;
    private readonly IColumnMatcher _matcher;
    private readonly ITargetWriter _writer;
    private readonly AliasRules _aliases;
    private readonly QualifiedRules _qualified;

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

        // Optional default-template preload (PRD §2.1 / §5). Fully wired from
        // settings in Tracer 5; stubbed here so the hook exists.
        TryPreloadDefaultTemplate();
    }

    // ---- Source file + sheet ----------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _sourceFilePath;

    public ObservableCollection<string> SourceSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _selectedSourceSheet;

    [ObservableProperty]
    private int _sourceHeaderRow = 1;   // 1-based for the user; →0-based for Core

    // ---- Target file + sheet ----------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _targetFilePath;

    public ObservableCollection<string> TargetSheets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _selectedTargetSheet;

    [ObservableProperty]
    private int _targetHeaderRow = 1;

    // ---- Matching / output -------------------------------------------------

    [ObservableProperty]
    private int _confidenceThreshold = 80;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Pick a source and target file, choose sheets, then Run.";

    // ---- File pickers ------------------------------------------------------

    [RelayCommand]
    private void BrowseSource()
    {
        if (PickExcelFile() is { } path)
        {
            SourceFilePath = path;
            LoadSheets(path, SourceSheets, s => SelectedSourceSheet = s);
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (PickExcelFile() is { } path)
        {
            TargetFilePath = path;
            LoadSheets(path, TargetSheets, s => SelectedTargetSheet = s);
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

    // ---- Run: the full read → match → write pipeline -----------------------

    private bool CanRun =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(SourceFilePath)
        && !string.IsNullOrWhiteSpace(TargetFilePath)
        && !string.IsNullOrWhiteSpace(SelectedSourceSheet)
        && !string.IsNullOrWhiteSpace(SelectedTargetSheet);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        Status = "Running…";
        try
        {
            // Snapshot bound state, then do the work off the UI thread.
            string sourcePath = SourceFilePath!;
            string targetPath = TargetFilePath!;
            string sourceSheet = SelectedSourceSheet!;
            string targetSheet = SelectedTargetSheet!;
            int srcHeaderRow = SourceHeaderRow - 1;   // 1-based UI → 0-based Core
            int tgtHeaderRow = TargetHeaderRow - 1;
            int threshold = ConfidenceThreshold;
            string outputDir = OutputDirectory;

            var result = await Task.Run(() => RunPipeline(
                sourcePath, sourceSheet, srcHeaderRow,
                targetPath, targetSheet, tgtHeaderRow,
                threshold, outputDir));

            Status = result;
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The headless pipeline — identical sequence to CelMap.Cli/Program.cs.
    /// Returns a human-readable summary line.
    /// </summary>
    private string RunPipeline(
        string sourcePath, string sourceSheet, int srcHeaderRow,
        string targetPath, string targetSheet, int tgtHeaderRow,
        int threshold, string outputDir)
    {
        var sourceData = _reader.ReadSheet(sourcePath, sourceSheet);
        var targetData = _reader.ReadSheet(targetPath, targetSheet);

        var sourceHeaders = HeaderExtractor.Extract(sourceData, srcHeaderRow);
        var targetHeaders = HeaderExtractor.Extract(targetData, tgtHeaderRow);

        var result = _matcher.Match(
            sourceHeaders, targetHeaders,
            new MatcherOptions(ConfidenceThreshold: threshold),
            src => sourceData.ColumnIsEmpty(src.ColumnIndex, srcHeaderRow));

        var columnMap = result.ToColumnMap();
        if (columnMap.Count == 0)
            return "No columns matched above the threshold — nothing written. "
                 + "Lower the threshold or check the header rows.";

        var writeResult = _writer.Write(new WriteRequest(
            sourcePath, sourceSheet, srcHeaderRow,
            targetPath, targetSheet, tgtHeaderRow,
            columnMap, outputDir));

        string warnings = writeResult.Warnings.Count > 0
            ? $"  ({writeResult.Warnings.Count} warning(s))"
            : string.Empty;

        return $"Done. {columnMap.Count} column(s) mapped, {writeResult.RowsWritten} row(s) written.{warnings}\n"
             + $"Output: {writeResult.OutputFilePath}";
    }

    // ---- Stubs filled in by later tracers ----------------------------------

    /// <summary>Placeholder for the Tracer 5 settings-driven default template.</summary>
    private void TryPreloadDefaultTemplate()
    {
        // Intentionally empty until settings persistence lands (Tracer 5).
    }
}
