using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatchAcCore.Core;
using Microsoft.Win32;

namespace BatchAcCore.Gui;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new() { WriteIndented = true };
    private string? _profilePath;
    private BatchSettings _settings = CreateNewSettings();
    private bool _isFileListInput = true;
    private bool _canRun;
    private bool _isRunning;
    private string _outputText = string.Empty;
    private string _runSummary = "Open or create a profile, then run preflight before starting a batch.";
    private string _statusText = "Ready";
    private int _progressTotal;
    private int _progressValue;
    private CancellationTokenSource? _cancellation;
    private BatchRunResult? _lastRun;
    private QueueItem? _selectedQueueItem;
    private string? _completedRunProfileSnapshot;
    private bool _isResultsStale;
    private bool _failedRerunPromptDismissed;

    public MainWindow()
    {
        InitializeComponent();
        Settings.PropertyChanged += Settings_PropertyChanged;
        DataContext = this;
    }

    public BatchSettings Settings
    {
        get => _settings;
        private set
        {
            if (ReferenceEquals(_settings, value)) return;
            _settings.PropertyChanged -= Settings_PropertyChanged;
            _isFileListInput = string.IsNullOrWhiteSpace(value.FileListPath)
                ? string.IsNullOrWhiteSpace(value.InputDirectory)
                : true;
            if (_isFileListInput && !string.IsNullOrWhiteSpace(value.FileListPath)) value.Recursive = false;
            SetField(ref _settings, value);
            _settings.PropertyChanged += Settings_PropertyChanged;
            OnPropertyChanged(nameof(IsFileListInput));
            OnPropertyChanged(nameof(IsInputDirectoryInput));
        }
    }

    public ObservableCollection<PreflightDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<QueueItem> Queue { get; } = [];

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanCreateRerun));
            OnPropertyChanged(nameof(CanOpenSelectedLog));
            OnPropertyChanged(nameof(CanOpenBatchSummary));
            OnPropertyChanged(nameof(CanOpenCombinedCsv));
        }
    }

    public bool CanEdit => !IsRunning;
    public bool CanStart => CanEdit && _canRun;
    public bool CanCreateRerun => CanEdit && !IsResultsStale && _lastRun is not null && _lastRun.Jobs.Any(job => job.Status is "Failed" or "TimedOut" or "Cancelled");
    public bool CanOpenSelectedLog => CanEdit && File.Exists(SelectedQueueItem?.LogPath);
    public bool CanOpenBatchSummary => CanEdit && File.Exists(_lastRun?.ReadableSummaryPath);
    public bool CanOpenCombinedCsv => CanEdit && File.Exists(_lastRun?.CombinedCsvPath);
    public bool IsFileListInput
    {
        get => _isFileListInput;
        set
        {
            if (!value) return;
            _isFileListInput = true;
            Settings.InputDirectory = null;
            Settings.Recursive = false;
            NotifyInputMethodChanged();
        }
    }

    public bool IsInputDirectoryInput
    {
        get => !_isFileListInput;
        set
        {
            if (!value) return;
            _isFileListInput = false;
            Settings.FileListPath = null;
            NotifyInputMethodChanged();
        }
    }

    public QueueItem? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set
        {
            if (!SetField(ref _selectedQueueItem, value)) return;
            OnPropertyChanged(nameof(CanOpenSelectedLog));
        }
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetField(ref _outputText, value);
    }

    public string RunSummary
    {
        get => _runSummary;
        private set => SetField(ref _runSummary, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public int ProgressMaximum => Math.Max(1, _progressTotal);
    public int ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public string ProgressText => _progressTotal == 0 ? "No batch queued" : $"{ProgressValue} / {_progressTotal} complete";

    public bool IsResultsStale
    {
        get => _isResultsStale;
        private set
        {
            if (!SetField(ref _isResultsStale, value)) return;
            OnPropertyChanged(nameof(RunOutputHeader));
            OnPropertyChanged(nameof(CanCreateRerun));
        }
    }

    public string RunOutputHeader => IsResultsStale ? "Run Output (Previous Run)" : "Run Output";

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmProfileEdit()) return;
        Settings = CreateNewSettings();
        _profilePath = null;
        ClearRunState();
        RunSummary = "New profile. Enter the required paths, then run preflight.";
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmProfileEdit()) return;
        var dialog = new OpenFileDialog { Filter = "Batch profiles (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<BatchSettings>(File.ReadAllText(dialog.FileName));
            if (loaded is null) throw new InvalidOperationException("The profile is empty.");
            Settings = loaded;
            _profilePath = dialog.FileName;
            ClearRunState();
            RunSummary = "Loaded profile: " + _profilePath;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not open the profile.\n\n" + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureProfilePath()) _ = SaveProfile();
    }

    private void SaveProfileAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Batch profiles (*.json)|*.json|All files (*.*)|*.*", DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        _profilePath = dialog.FileName;
        _ = SaveProfile();
    }

    private void BrowseCoreConsole_Click(object sender, RoutedEventArgs e) =>
        ChooseFile("Executables (*.exe)|*.exe|All files (*.*)|*.*", path => Settings.AcCoreConsolePath = path);

    private void BrowseLisp_Click(object sender, RoutedEventArgs e) =>
        ChooseFile("AutoLISP routines (*.lsp)|*.lsp|All files (*.*)|*.*", path => Settings.LispFilePath = path);

    private void BrowseDrawingList_Click(object sender, RoutedEventArgs e) =>
        ChooseFile("Drawing lists (*.txt)|*.txt|All files (*.*)|*.*", path => Settings.FileListPath = path);

    private void BrowseInputDirectory_Click(object sender, RoutedEventArgs e) =>
        ChooseFolder(path => Settings.InputDirectory = path);

    private void BrowseWorkDirectory_Click(object sender, RoutedEventArgs e) =>
        ChooseFolder(path => Settings.WorkDirectory = path);

    private void BrowseResultsDirectory_Click(object sender, RoutedEventArgs e) =>
        ChooseFolder(path => Settings.ResultsDirectory = path);

    private void OpenSelectedLog_Click(object sender, RoutedEventArgs e) => OpenArtifact(SelectedQueueItem?.LogPath, "job log");
    private void OpenBatchSummary_Click(object sender, RoutedEventArgs e) => OpenArtifact(_lastRun?.ReadableSummaryPath, "batch summary");
    private void OpenCombinedCsv_Click(object sender, RoutedEventArgs e) => OpenArtifact(_lastRun?.CombinedCsvPath, "combined CSV");

    private void CreateRerun_Click(object sender, RoutedEventArgs e) => CreateFailedOnlyRerun();

    private bool CreateFailedOnlyRerun()
    {
        var drawings = _lastRun?.Jobs
            .Where(job => job.Status is "Failed" or "TimedOut" or "Cancelled")
            .Select(job => job.Drawing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (drawings.Length == 0) return false;

        var dialog = new SaveFileDialog
        {
            Filter = "Batch profiles (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "failed-rerun.json"
        };
        if (dialog.ShowDialog(this) != true) return false;

        var drawingListPath = Path.Combine(
            Path.GetDirectoryName(dialog.FileName)!,
            Path.GetFileNameWithoutExtension(dialog.FileName) + ".rerun.drawings.txt");
        if (File.Exists(drawingListPath) &&
            MessageBox.Show(this, "The companion drawing-list file already exists. Replace it?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        try
        {
            var rerunSettings = JsonSerializer.Deserialize<BatchSettings>(JsonSerializer.Serialize(Settings, ProfileJsonOptions))
                ?? throw new InvalidOperationException("Could not create a copy of the current profile.");
            rerunSettings.FileListPath = drawingListPath;
            rerunSettings.InputDirectory = null;
            File.WriteAllLines(drawingListPath, drawings);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(rerunSettings, ProfileJsonOptions));

            Settings = rerunSettings;
            _profilePath = dialog.FileName;
            ClearRunState();
            RunPreflight();
            RunSummary = "Created a separate failed-only rerun profile and drawing list. The original profile was not changed.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not create the rerun profile.\n\n" + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RunPreflight_Click(object sender, RoutedEventArgs e)
    {
        RunPreflight();
        PreflightQueueTab.IsSelected = true;
    }

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!RunPreflight() || !EnsureProfilePath()) return;
        if (!SaveProfile()) return;

        _lastRun = null;
        _completedRunProfileSnapshot = null;
        IsResultsStale = false;
        _failedRerunPromptDismissed = false;
        OnPropertyChanged(nameof(CanCreateRerun));
        OnPropertyChanged(nameof(CanOpenBatchSummary));
        OnPropertyChanged(nameof(CanOpenCombinedCsv));
        IsRunning = true;
        OutputText = string.Empty;
        _cancellation = new CancellationTokenSource();
        RunSummary = "Batch is starting. Cancelling stops queued jobs; active Core Console jobs finish normally.";
        StatusText = "Starting batch...";
        SetProgress(0, Queue.Count);
        var output = new GuiBatchOutput(AppendOutput);
        var progress = new Progress<BatchProgressEvent>(HandleProgress);

        try
        {
            _lastRun = await BatchRunner.RunWithResultAsync([_profilePath!], output, progress, _cancellation.Token);
            _completedRunProfileSnapshot = SerializeSettings();
            IsResultsStale = false;
            _failedRerunPromptDismissed = false;
            OnPropertyChanged(nameof(CanCreateRerun));
            OnPropertyChanged(nameof(CanOpenBatchSummary));
            OnPropertyChanged(nameof(CanOpenCombinedCsv));
            RunSummary = "Batch completed with exit code " + _lastRun.ExitCode + ". " + BuildQueueSummary();
        }
        catch (Exception exception)
        {
            AppendOutput("Unexpected GUI error: " + exception);
            RunSummary = "Batch ended with an unexpected GUI error.";
            StatusText = "Batch ended with an unexpected error.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            if (_lastRun is not null) UpdateBatchProgressStatus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        RunSummary = "Cancellation requested. No new jobs will start; active Core Console jobs will finish normally.";
        StatusText = "Cancelling queued work; active jobs will finish.";
    }

    private bool RunPreflight()
    {
        var baseDirectory = _profilePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_profilePath)!;
        var report = BatchPreflight.Check(Settings, baseDirectory);
        Diagnostics.Clear();
        foreach (var diagnostic in report.Diagnostics) Diagnostics.Add(diagnostic);

        Queue.Clear();
        var inputSource = string.IsNullOrWhiteSpace(Settings.FileListPath) ? "Input directory" : "Drawing list";
        foreach (var drawing in report.Drawings) Queue.Add(new QueueItem(drawing, inputSource));
        foreach (var duplicate in report.OmittedDrawings)
        {
            var item = new QueueItem(duplicate.Drawing, inputSource)
            {
                Status = "Skipped",
                Message = $"Skipped because its derived CSV output duplicates {duplicate.RetainedDrawing}."
            };
            Queue.Add(item);
        }

        _canRun = report.CanRun;
        OnPropertyChanged(nameof(CanStart));
        RunSummary = report.CanRun
            ? "Preflight passed. " + report.Drawings.Count + " drawing(s) are ready for review."
            : "Preflight found one or more errors. Resolve them before starting the batch.";
        SetProgress(0, Queue.Count);
        StatusText = !report.CanRun
            ? "Preflight failed"
            : report.Diagnostics.Any(diagnostic => diagnostic.Severity == PreflightSeverity.Warning)
                ? "Preflight passed (with warnings)"
                : "Preflight passed";
        return report.CanRun;
    }

    private void ChooseFile(string filter, Action<string> setPath)
    {
        if (!ConfirmProfileEdit()) return;
        var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(this) != true) return;
        setPath(dialog.FileName);
        OnPropertyChanged(nameof(Settings));
    }

    private void ChooseFolder(Action<string> setPath)
    {
        if (!ConfirmProfileEdit()) return;
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) != true) return;
        setPath(dialog.FolderName);
        OnPropertyChanged(nameof(Settings));
    }

    private void NotifyInputMethodChanged()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsFileListInput));
        OnPropertyChanged(nameof(IsInputDirectoryInput));
    }

    private void ProfileEditor_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox or CheckBox or RadioButton && !ConfirmProfileEdit())
            e.Handled = true;
    }

    private bool ConfirmProfileEdit()
    {
        if (_failedRerunPromptDismissed || !HasFailedOnlyRerunAvailable()) return true;

        var result = MessageBox.Show(
            this,
            "The completed run has failed, timed-out, or cancelled files.\n\nDo you want to create a Failed-Only Rerun now?",
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            CreateFailedOnlyRerun();
            return false;
        }

        _failedRerunPromptDismissed = true;
        return true;
    }

    private bool HasFailedOnlyRerunAvailable() =>
        _lastRun is not null &&
        !IsResultsStale &&
        _lastRun.Jobs.Any(job => job.Status is "Failed" or "TimedOut" or "Cancelled");

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Settings));
        if (_lastRun is null || IsRunning) return;

        var differsFromCompletedRun = !string.Equals(_completedRunProfileSnapshot, SerializeSettings(), StringComparison.Ordinal);
        if (differsFromCompletedRun && !_failedRerunPromptDismissed && HasFailedOnlyRerunAvailable() && !ConfirmProfileEdit()) return;

        IsResultsStale = differsFromCompletedRun;
    }

    private string SerializeSettings() => JsonSerializer.Serialize(Settings, ProfileJsonOptions);

    private static BatchSettings CreateNewSettings() => new();

    private void OpenArtifact(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "The " + description + " is not available at the recorded path.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not open the " + description + ".\n\n" + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool EnsureProfilePath()
    {
        if (_profilePath is not null) return true;
        var dialog = new SaveFileDialog { Filter = "Batch profiles (*.json)|*.json|All files (*.*)|*.*", DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return false;
        _profilePath = dialog.FileName;
        return true;
    }

    private bool SaveProfile()
    {
        try
        {
            File.WriteAllText(_profilePath!, JsonSerializer.Serialize(Settings, ProfileJsonOptions));
            RunSummary = "Saved profile: " + _profilePath;
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not save the profile.\n\n" + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void HandleProgress(BatchProgressEvent progress)
    {
        if (progress.Drawing is not null)
        {
            var item = Queue.FirstOrDefault(candidate => string.Equals(candidate.Drawing, progress.Drawing, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                item.Status = progress.Status ?? item.Status;
                item.WorkerId = progress.WorkerId;
                item.Message = progress.Message;
                if (progress.Result is not null) item.ApplyResult(progress.Result);
                OnPropertyChanged(nameof(CanOpenSelectedLog));
            }
        }

        if (progress.Kind == BatchEventKind.Warning && progress.Message is not null) AppendOutput("Warning: " + progress.Message);
        if (progress.Kind == BatchEventKind.BatchCompleted) RunSummary = "Batch is finishing. " + BuildQueueSummary();
        if (progress.Kind is BatchEventKind.BatchStarted or BatchEventKind.JobStarted or BatchEventKind.JobCompleted or BatchEventKind.BatchCompleted)
            UpdateBatchProgressStatus();
    }

    private void AppendOutput(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendOutput(message));
            return;
        }

        OutputText += message + Environment.NewLine;
    }

    private string BuildQueueSummary()
    {
        var succeeded = Queue.Count(item => item.Status == "Succeeded");
        var failed = Queue.Count(item => item.Status == "Failed");
        var skipped = Queue.Count(item => item.Status == "Skipped");
        var cancelled = Queue.Count(item => item.Status == "Cancelled");
        var running = Queue.Count(item => item.Status == "Running");
        var queued = Queue.Count(item => item.Status == "Queued");
        return "Succeeded: " + succeeded + "; failed: " + failed + "; skipped: " + skipped + "; cancelled: " + cancelled + "; running: " + running + "; queued: " + queued + ".";
    }

    private void UpdateBatchProgressStatus()
    {
        var total = Queue.Count;
        var completed = Queue.Count(item => item.Status is "Succeeded" or "Failed" or "Skipped" or "Cancelled");
        var running = Queue.Count(item => item.Status == "Running");
        SetProgress(completed, total);

        if (total == 0) return;
        if (IsRunning)
        {
            StatusText = _cancellation?.IsCancellationRequested == true
                ? $"Cancelling: {ProgressText} · {running} active"
                : $"Running: {ProgressText} · {running} active";
            return;
        }

        StatusText = "Batch complete: " + ProgressText;
    }

    private void SetProgress(int value, int total)
    {
        _progressTotal = total;
        ProgressValue = Math.Clamp(value, 0, Math.Max(total, 1));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void ClearRunState()
    {
        _canRun = false;
        _lastRun = null;
        _completedRunProfileSnapshot = null;
        IsResultsStale = false;
        _failedRerunPromptDismissed = false;
        SelectedQueueItem = null;
        Diagnostics.Clear();
        Queue.Clear();
        OutputText = string.Empty;
        SetProgress(0, 0);
        StatusText = "Ready";
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCreateRerun));
        OnPropertyChanged(nameof(CanOpenSelectedLog));
        OnPropertyChanged(nameof(CanOpenBatchSummary));
        OnPropertyChanged(nameof(CanOpenCombinedCsv));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class GuiBatchOutput(Action<string> append) : IBatchOutput
    {
        public void WriteLine(string message) => append(message);
        public void WriteError(string message) => append(message);
    }
}
