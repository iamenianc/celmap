using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private string? _sourceFilePath;

    public string? SourceFileName =>
        string.IsNullOrEmpty(SourceFilePath) ? null : Path.GetFileName(SourceFilePath);

    private string? _sourcePassword;

    public string? SourcePassword => _sourcePassword;

    /// <summary>View hook that prompts the user for a password (returns null if they cancel).</summary>
    public Func<string, string?>? PasswordPrompt { get; set; }

    public ObservableCollection<string> SourceSheets { get; } = new();

    [ObservableProperty]
    private string? _selectedSourceSheet;

    [ObservableProperty]
    private int _sourceHeaderRow = 1;   // 1-based for the user; →0-based for Core

    public ObservableCollection<string> SourceHeaderPreview { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFileName))]
    private string? _targetFilePath;

    public string? TargetFileName =>
        string.IsNullOrEmpty(TargetFilePath) ? null : Path.GetFileName(TargetFilePath);

    public ObservableCollection<string> TargetSheets { get; } = new();

    [ObservableProperty]
    private string? _selectedTargetSheet;

    [ObservableProperty]
    private int _targetHeaderRow = 1;

    public ObservableCollection<string> TargetHeaderPreview { get; } = new();

    public string TargetTemplateDirectory { get; } =
        @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_targets";

    public ObservableCollection<TargetChoice> TargetChoices { get; } = new();

    [ObservableProperty]
    private TargetChoice? _selectedTargetChoice;

    public SetupViewModel(IWorkbookReader reader, Action<string> updateStatus, Action resetGrid)
    {
        _reader = reader;
        _updateStatus = updateStatus;
        _resetGrid = resetGrid;

        LoadTargetChoices();
    }

    partial void OnSelectedTargetChoiceChanged(TargetChoice? value)
    {
        if (value is not null && File.Exists(value.FullPath))
            LoadTargetFile(value.FullPath);
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (PickExcelFile() is { } path) LoadSourceFile(path);
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

    public void LoadSourceFile(string path)
    {
        if (!IsValidSourceFile(path, out string rejection))
        {
            _updateStatus(rejection);
            return;
        }

        _sourcePassword = null;

        try
        {
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

        SourceFilePath = path;
        LoadSheets(path, SourceSheets, s => SelectedSourceSheet = s, _sourcePassword);
        _resetGrid();
        RefreshSourceSetup(detect: true);
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
            return;
        }
        TryReadHeaderPreview(SourceFilePath!, SelectedSourceSheet!, detect,
            row => SourceHeaderRow = row, SourceHeaderRow - 1, SourceHeaderPreview, _sourcePassword);
    }

    public void RefreshTargetSetup(bool detect)
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
        Action<int> setHeaderRow1Based, int currentHeaderRow0, ObservableCollection<string> preview,
        string? password = null)
    {
        preview.Clear();
        try
        {
            var data = _reader.ReadSheet(path, sheet, password);
            int headerRow0 = detect ? HeaderRowDetector.Detect(data) : currentHeaderRow0;
            if (detect) setHeaderRow1Based(headerRow0 + 1);
            else headerRow0 = Math.Clamp(headerRow0, 0, Math.Max(0, data.RowCount - 1));

            var headers = HeaderExtractor.Extract(data, headerRow0);
            foreach (var h in headers.Take(10))
                preview.Add(string.IsNullOrWhiteSpace(h.Label) ? "(blank)" : h.Label);
        }
        catch (Exception ex)
        {
            _updateStatus(FriendlyError(ex));
        }
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
