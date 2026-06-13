using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CelMap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CelMap.App;

/// <summary>
/// Manages Screen 1: File Setup, drag-drop handling, worksheet loading, and header detection.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    public const long MaxSourceFileBytes = 10L * 1024 * 1024;   // 10 MB

    private readonly IWorkbookReader _reader;
    private readonly Action<string> _updateStatus;
    private readonly Action _resetGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceFileName))]
    [NotifyPropertyChangedFor(nameof(SourceRowCountDisplay))]
    private string? _sourceFilePath;

    public string? SourceFileName =>
        string.IsNullOrEmpty(SourceFilePath) ? null : Path.GetFileName(SourceFilePath);

    private string? _sourcePassword;

    public string? SourcePassword => _sourcePassword;

    /// <summary>View hook that prompts the user for a password (returns null if they cancel).</summary>
    public Func<string, string?>? PasswordPrompt { get; set; }

    public ObservableCollection<string> SourceSheets { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceRowCountDisplay))]
    private string? _selectedSourceSheet;

    [ObservableProperty]
    private int _sourceHeaderRow = 1;   // 1-based for the user; →0-based for Core

    public ObservableCollection<string> SourceHeaderPreview { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFileName))]
    [NotifyPropertyChangedFor(nameof(TargetRowCountDisplay))]
    private string? _targetFilePath;

    public string? TargetFileName =>
        string.IsNullOrEmpty(TargetFilePath) ? null : Path.GetFileName(TargetFilePath);

    public ObservableCollection<string> TargetSheets { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRowCountDisplay))]
    private string? _selectedTargetSheet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceRowCountDisplay))]
    private int? _sourceRowCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRowCountDisplay))]
    private int? _targetRowCount;

    public string SourceRowCountDisplay => SourceRowCount.HasValue && !string.IsNullOrEmpty(SourceFilePath) && !string.IsNullOrEmpty(SelectedSourceSheet)
        ? $"({SourceRowCount} rows)"
        : string.Empty;

    public string TargetRowCountDisplay => TargetRowCount.HasValue && !string.IsNullOrEmpty(TargetFilePath) && !string.IsNullOrEmpty(SelectedTargetSheet)
        ? $"({TargetRowCount} rows)"
        : string.Empty;

    [ObservableProperty]
    private int _targetHeaderRow = 1;

    public ObservableCollection<string> TargetHeaderPreview { get; } = new();

    public string TargetTemplateDirectory { get; } =
        @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_targets";

    public ObservableCollection<TargetChoice> TargetChoices { get; } = new();

    [ObservableProperty]
    private TargetChoice? _selectedTargetChoice;

    // ---- Source-load progress -------------------------------------------------
    // ClosedXML loads the whole workbook into an in-memory DOM in one opaque,
    // synchronous call, so there is no real progress to report. Instead we estimate
    // the duration from the file size (load time scales roughly with bytes), animate
    // the bar to ~95% over that estimate, then snap to 100% when the load returns.

    [ObservableProperty]
    private bool _isLoadingSource;

    [ObservableProperty]
    private double _loadProgress;   // 0–100

    [ObservableProperty]
    private string _loadProgressText = "";

    // Roughly: ~250 ms fixed overhead + ~0.69 ms per KB. The per-KB factor was raised ~25%
    // after testing showed the bar ran ~20% too fast. The estimate only drives the
    // animation, never the data.
    private const double EstimateBaseMs = 250;
    private const double EstimateMsPerKilobyte = 0.69;
    private const double EstimateCeilingPercent = 95;
    // The bar advances in chunky steps (20, 40, 60, 80) rather than smoothly.
    private const double ProgressStepPercent = 20;

    private DispatcherTimer? _progressTimer;
    private Stopwatch? _progressStopwatch;
    private double _estimatedLoadMs;
    private bool _suppressSourceRefresh;

    public SetupViewModel(IWorkbookReader reader, Action<string> updateStatus, Action resetGrid)
    {
        _reader = reader;
        _updateStatus = updateStatus;
        _resetGrid = resetGrid;

        LoadTargetChoices();
        SelectedTargetChoice = TargetChoices.FirstOrDefault();
    }

    partial void OnSelectedTargetChoiceChanged(TargetChoice? value)
    {
        if (value is not null && File.Exists(value.FullPath))
            LoadTargetFile(value.FullPath);
    }

    [RelayCommand]
    private async Task BrowseSource()
    {
        if (PickExcelFile() is { } path) await LoadSourceFileAsync(path);
    }

    [RelayCommand]
    private void BrowseCustomTarget()
    {
        if (PickExcelFile() is not { } path) return;

        var existing = TargetChoices.FirstOrDefault(
            c => string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new TargetChoice($"{Path.GetFileName(path)}  (custom)", path);
            TargetChoices.Add(existing);
        }
        SelectedTargetChoice = existing;
    }

    private void LoadTargetChoices()
    {
        TargetChoices.Clear();
        try
        {
            if (!Directory.Exists(TargetTemplateDirectory)) return;
            var files = Directory.EnumerateFiles(TargetTemplateDirectory)
                .Where(IsExcelFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                TargetChoices.Add(new TargetChoice(Path.GetFileName(file), file));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task LoadSourceFileAsync(string path)
    {
        if (!IsValidSourceFile(path, out string rejection))
        {
            _updateStatus(rejection);
            return;
        }

        _sourcePassword = null;

        try
        {
            // Cheap signature check + optional password prompt stay on the UI thread:
            // the prompt is modal, and IsEncrypted only reads 8 bytes.
            if (_reader.IsEncrypted(path) && !TryUnlockSource(path))
            {
                _updateStatus("Source is password-protected — load cancelled.");
                return;
            }
        }
        catch (Exception ex)
        {
            _updateStatus(FriendlyError(ex));
            return;
        }

        long fileBytes = SafeFileLength(path);
        StartProgress(fileBytes);
        try
        {
            // The expensive part — building the ClosedXML DOM and reading the sheet for
            // header detection — runs on a background thread so the bar can animate and
            // the window stays responsive.
            var (sheetNames, sheetData) = await Task.Run(() =>
            {
                var names = _reader.GetSheetNames(path, _sourcePassword);
                string first = names.FirstOrDefault() ?? "";
                SheetData? data = first.Length == 0
                    ? null
                    : _reader.ReadSheet(path, first, _sourcePassword);
                return (names, data);
            });

            SourceFilePath = path;

            _suppressSourceRefresh = true;
            try
            {
                SourceSheets.Clear();
                foreach (var name in sheetNames) SourceSheets.Add(name);
                SelectedSourceSheet = SourceSheets.FirstOrDefault();
            }
            finally
            {
                _suppressSourceRefresh = false;
            }

            _resetGrid();
            ApplyHeaderPreview(sheetData, detect: true,
                row => SourceHeaderRow = row, SourceHeaderRow - 1, SourceHeaderPreview);
            SourceRowCount = sheetData?.RowCount ?? 0;
        }
        catch (Exception ex)
        {
            _updateStatus(FriendlyError(ex));
        }
        finally
        {
            FinishProgress();
        }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    // ---- Estimated progress animation ----------------------------------------

    private void StartProgress(long fileBytes)
    {
        _estimatedLoadMs = EstimateBaseMs + (fileBytes / 1024.0) * EstimateMsPerKilobyte;
        LoadProgress = 0;
        LoadProgressText = "Reading workbook…";
        IsLoadingSource = true;

        _progressStopwatch = Stopwatch.StartNew();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _progressTimer.Tick += OnProgressTick;
        _progressTimer.Start();
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (_progressStopwatch is null || _estimatedLoadMs <= 0) return;
        double fraction = _progressStopwatch.Elapsed.TotalMilliseconds / _estimatedLoadMs;
        double estimated = fraction * EstimateCeilingPercent;
        // Quantise to 20% steps (20, 40, 60, 80) so the bar jumps in chunks. We never
        // reach the ceiling from the estimate alone — the real completion snaps us to
        // 100%, so we never claim "done" prematurely.
        double stepped = Math.Floor(estimated / ProgressStepPercent) * ProgressStepPercent;
        LoadProgress = Math.Min(EstimateCeilingPercent, stepped);
    }

    private void FinishProgress()
    {
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= OnProgressTick;
            _progressTimer = null;
        }
        _progressStopwatch = null;
        LoadProgress = 100;
        IsLoadingSource = false;
    }

    private bool TryUnlockSource(string path)
    {
        if (PasswordPrompt is null) return false;

        string fileName = Path.GetFileName(path);
        string promptMessage = $"“{fileName}” is password-protected.\nEnter the password to open it:";

        while (true)
        {
            string? entered = PasswordPrompt(promptMessage);
            if (entered is null) return false;

            try
            {
                _ = _reader.GetSheetNames(path, entered);
                _sourcePassword = entered;
                return true;
            }
            catch (InvalidPasswordException)
            {
                promptMessage = $"That password didn't work for “{fileName}”.\nTry again:";
            }
        }
    }

    public void LoadTargetFile(string path)
    {
        TargetFilePath = path;
        LoadSheets(path, TargetSheets, s => SelectedTargetSheet = s);
        _resetGrid();
        RefreshTargetSetup(detect: true);
    }

    public static bool IsExcelFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidSourceFile(string path, out string reason)
    {
        string fileName = Path.GetFileName(path);

        if (!IsExcelFile(path))
        {
            reason = $"Can't import “{fileName}” — only Excel files (.xlsx or .xlsm) are supported.";
            return false;
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            reason = $"Can't import “{fileName}” — {ex.Message}";
            return false;
        }

        if (size > MaxSourceFileBytes)
        {
            reason = $"Can't import “{fileName}” — it's {FormatMegabytes(size)} MB, "
                   + $"over the {FormatMegabytes(MaxSourceFileBytes)} MB limit.";
            return false;
        }

        reason = "";
        return true;
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#");

    private static string? PickExcelFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void LoadSheets(string path, ObservableCollection<string> sheets, Action<string?> select,
                            string? password = null)
    {
        sheets.Clear();
        try
        {
            foreach (var name in _reader.GetSheetNames(path, password))
                sheets.Add(name);
            select(sheets.FirstOrDefault());
        }
        catch (Exception ex)
        {
            select(null);
            _updateStatus($"Could not read sheets from '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    public void RefreshSourceSetup(bool detect)
    {
        if (string.IsNullOrWhiteSpace(SourceFilePath) || string.IsNullOrWhiteSpace(SelectedSourceSheet))
        {
            SourceHeaderPreview.Clear();
            SourceRowCount = 0;
            return;
        }
        var data = TryReadHeaderPreview(SourceFilePath!, SelectedSourceSheet!, detect,
            row => SourceHeaderRow = row, SourceHeaderRow - 1, SourceHeaderPreview, _sourcePassword);
        SourceRowCount = data?.RowCount ?? 0;
    }

    public void RefreshTargetSetup(bool detect)
    {
        if (string.IsNullOrWhiteSpace(TargetFilePath) || string.IsNullOrWhiteSpace(SelectedTargetSheet))
        {
            TargetHeaderPreview.Clear();
            TargetRowCount = 0;
            return;
        }
        var data = TryReadHeaderPreview(TargetFilePath!, SelectedTargetSheet!, detect,
            row => TargetHeaderRow = row, TargetHeaderRow - 1, TargetHeaderPreview);
        TargetRowCount = data?.RowCount ?? 0;
    }

    private SheetData? TryReadHeaderPreview(string path, string sheet, bool detect,
        Action<int> setHeaderRow1Based, int currentHeaderRow0, ObservableCollection<string> preview,
        string? password = null)
    {
        try
        {
            var data = _reader.ReadSheet(path, sheet, password);
            ApplyHeaderPreview(data, detect, setHeaderRow1Based, currentHeaderRow0, preview);
            return data;
        }
        catch (Exception ex)
        {
            preview.Clear();
            _updateStatus(FriendlyError(ex));
            return null;
        }
    }

    /// <summary>Render the header preview from already-loaded sheet data, avoiding a second
    /// (slow) workbook read when the data is already in hand from the initial load.</summary>
    private void ApplyHeaderPreview(SheetData? data, bool detect,
        Action<int> setHeaderRow1Based, int currentHeaderRow0, ObservableCollection<string> preview)
    {
        preview.Clear();
        if (data is null) return;

        int headerRow0 = detect ? HeaderRowDetector.Detect(data) : currentHeaderRow0;
        if (detect) setHeaderRow1Based(headerRow0 + 1);
        else headerRow0 = Math.Clamp(headerRow0, 0, Math.Max(0, data.RowCount - 1));

        var headers = HeaderExtractor.Extract(data, headerRow0);
        foreach (var h in headers.Take(10))
            preview.Add(string.IsNullOrWhiteSpace(h.Label) ? "(blank)" : h.Label);
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is IOException)
            return "Could not open a file — it may be open in Excel. "
                 + "Close it there and try again.\n(" + ex.Message + ")";
        return $"Error: {ex.Message}";
    }

    // Callbacks triggered when worksheet or header rows change to refresh previews.
    // In WPF, we want these property changes to trigger refreshing setup preview.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // During the async source load we set SelectedSourceSheet ourselves and render the
        // preview from data already in hand — suppress the auto-refresh so we don't kick off
        // a second synchronous ReadSheet on the UI thread (the very freeze we're removing).
        if (_suppressSourceRefresh && e.PropertyName == nameof(SelectedSourceSheet))
            return;

        if (e.PropertyName == nameof(SelectedSourceSheet))
            RefreshSourceSetup(detect: true);
        else if (e.PropertyName == nameof(SelectedTargetSheet))
            RefreshTargetSetup(detect: true);
        else if (e.PropertyName == nameof(SourceHeaderRow))
            RefreshSourceSetup(detect: false);
        else if (e.PropertyName == nameof(TargetHeaderRow))
            RefreshTargetSetup(detect: false);
    }
}
