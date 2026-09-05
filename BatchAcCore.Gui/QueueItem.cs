using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using BatchAcCore.Core;

namespace BatchAcCore.Gui;

public sealed class QueueItem(string drawing) : INotifyPropertyChanged
{
    private string _status = "Queued";
    private int? _workerId;
    private string? _message;
    private int? _exitCode;
    private string? _logPath;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _finishedUtc;

    public string Drawing { get; } = drawing;
    public string Name => Path.GetFileName(Drawing);

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int? WorkerId
    {
        get => _workerId;
        set => SetField(ref _workerId, value);
    }

    public string? Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public int? ExitCode
    {
        get => _exitCode;
        set => SetField(ref _exitCode, value);
    }

    public string? LogPath
    {
        get => _logPath;
        set => SetField(ref _logPath, value);
    }

    public DateTimeOffset? StartedUtc
    {
        get => _startedUtc;
        set => SetField(ref _startedUtc, value);
    }

    public DateTimeOffset? FinishedUtc
    {
        get => _finishedUtc;
        set => SetField(ref _finishedUtc, value);
    }

    public TimeSpan? Elapsed => StartedUtc is null || FinishedUtc is null ? null : FinishedUtc - StartedUtc;

    public void ApplyResult(JobResult result)
    {
        Status = result.Status;
        ExitCode = result.ExitCode;
        LogPath = result.LogPath;
        StartedUtc = result.StartedUtc;
        FinishedUtc = result.FinishedUtc;
        Message = result.Error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
