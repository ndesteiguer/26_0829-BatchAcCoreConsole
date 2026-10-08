using System.ComponentModel;
using System.Runtime.CompilerServices;
using BatchAcCore.Core;

namespace BatchAcCore.Gui;

public sealed class ScriptAdditionOption(ScriptAdditionDefinition definition) : INotifyPropertyChanged
{
    public string Id => definition.Id;
    public string Name => definition.Name;
    public string Description => definition.Description;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
