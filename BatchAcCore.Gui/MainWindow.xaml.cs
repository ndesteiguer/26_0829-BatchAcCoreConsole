using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using BatchAcCore.Core;
using Microsoft.Win32;

namespace BatchAcCore.Gui;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new() { WriteIndented = true };
    private string? _profilePath;
    private BatchSettings _settings = new();
    private bool _canRun;
    private bool _isRunning;
    private string _outputText = string.Empty;
    private string _runSummary = "Open or create a profile, then run preflight before starting a batch.";
    private CancellationTokenSource? _cancellation;
    private BatchRunResult? _lastRun;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public BatchSettings Settings
    {
        get => _settings;
        private set => SetField(ref _settings, value);
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
        }
    }

    public bool CanEdit => !IsRunning;
    public bool CanStart => CanEdit && _canRun;
    public bool CanCreateRerun => CanEdit && _lastRun is not null && _lastRun.Jobs.Any(job => job.Status is "Failed" or "TimedOut" or "Cancelled");

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

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
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

    private void CreateRerun_Click(object sender, RoutedEventArgs e)
    {
        var drawings = _lastRun?.Jobs
            .Where(job => job.Status is "Failed" or "TimedOut" or "Cancelled")
            .Select(job => job.Drawing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (drawings.Length == 0) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Batch profiles (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "failed-rerun.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        var drawingListPath = Path.Combine(
            Path.GetDirectoryName(dialog.FileName)!,
            Path.GetFileNameWithoutExtension(dialog.FileName) + ".rerun.drawings.txt");
        if (File.Exists(drawingListPath) &&
            MessageBox.Show(this, "The companion drawing-list file already exists. Replace it?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

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
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not create the rerun profile.\n\n" + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunPreflight_Click(object sender, RoutedEventArgs e) => RunPreflight();

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!RunPreflight() || !EnsureProfilePath()) return;
        if (!SaveProfile()) return;

        IsRunning = true;
        OutputText = string.Empty;
        _cancellation = new CancellationTokenSource();
        RunSummary = "Batch is starting. Cancelling stops queued jobs; active Core Console jobs finish normally.";
        var output = new GuiBatchOutput(AppendOutput);
        var progress = new Progress<BatchProgressEvent>(HandleProgress);

        try
        {
            _lastRun = await BatchRunner.RunWithResultAsync([_profilePath!], output, progress, _cancellation.Token);
            OnPropertyChanged(nameof(CanCreateRerun));
            RunSummary = "Batch completed with exit code " + _lastRun.ExitCode + ". " + BuildQueueSummary();
        }
        catch (Exception exception)
        {
            AppendOutput("Unexpected GUI error: " + exception);
            RunSummary = "Batch ended with an unexpected GUI error.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        RunSummary = "Cancellation requested. No new jobs will start; active Core Console jobs will finish normally.";
    }

    private bool RunPreflight()
    {
        _lastRun = null;
        OnPropertyChanged(nameof(CanCreateRerun));
        var baseDirectory = _profilePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_profilePath)!;
        var report = BatchPreflight.Check(Settings, baseDirectory);
        Diagnostics.Clear();
        foreach (var diagnostic in report.Diagnostics) Diagnostics.Add(diagnostic);

        Queue.Clear();
        foreach (var drawing in report.Drawings) Queue.Add(new QueueItem(drawing));

        _canRun = report.CanRun;
        OnPropertyChanged(nameof(CanStart));
        RunSummary = report.CanRun
            ? "Preflight passed. " + report.Drawings.Count + " drawing(s) are ready for review."
            : "Preflight found one or more errors. Resolve them before starting the batch.";
        return report.CanRun;
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
            }
        }

        if (progress.Kind == BatchEventKind.Warning && progress.Message is not null) AppendOutput("Warning: " + progress.Message);
        if (progress.Kind == BatchEventKind.BatchCompleted) RunSummary = "Batch is finishing. " + BuildQueueSummary();
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

    private void ClearRunState()
    {
        _canRun = false;
        _lastRun = null;
        Diagnostics.Clear();
        Queue.Clear();
        OutputText = string.Empty;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCreateRerun));
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
