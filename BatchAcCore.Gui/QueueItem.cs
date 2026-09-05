using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BatchAcCore.Gui;

public sealed class QueueItem(string drawing) : INotifyPropertyChanged
{
    private string _status = "Queued";
    private int? _workerId;
    private string? _message;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
