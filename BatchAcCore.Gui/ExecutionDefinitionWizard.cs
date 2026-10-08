using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BatchAcCore.Core;
using Microsoft.Win32;

namespace BatchAcCore.Gui;

public sealed record ExecutionDefinitionDraft(ExecutionType Type, string RoutinePath, string Function, string? OutputFormat);

/// <summary>Creates a paired LISP execution definition without requiring the operator to edit JSON.</summary>
public sealed class ExecutionDefinitionWizard : Window
{
    private readonly string _routinePath;
    private readonly ComboBox _type = new() { Margin = new Thickness(0, 4, 0, 8), MinWidth = 320 };
    private readonly TextBox _function = new() { Margin = new Thickness(0, 4, 0, 8), MinWidth = 320 };
    private readonly TextBox _outputFormat = new() { Margin = new Thickness(0, 4, 0, 12), MinWidth = 320 };

    public string? SavedDefinitionPath { get; private set; }
    public ExecutionDefinitionDraft? UnsavedDefinition { get; private set; }

    public ExecutionDefinitionWizard(string routinePath)
    {
        _routinePath = routinePath;
        Title = "Create Execution Definition";
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        _type.ItemsSource = new[]
        {
            new ExecutionTypeChoice("Blind LISP — no arguments or routine output", ExecutionType.BlindLisp),
            new ExecutionTypeChoice("LISP result — one output file per drawing", ExecutionType.LispResult),
            new ExecutionTypeChoice("LISP report — multi-line output per drawing", ExecutionType.LispReport),
            new ExecutionTypeChoice("LISP input report — shared input file and output", ExecutionType.LispInputReport)
        };
        _type.DisplayMemberPath = nameof(ExecutionTypeChoice.Label);
        _type.SelectedIndex = 0;
        _type.SelectionChanged += (_, _) => UpdateFields();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Routine", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = _routinePath, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        panel.Children.Add(new TextBlock { Text = "Execution type", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_type);
        panel.Children.Add(new TextBlock { Text = "LISP entry point", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_function);
        panel.Children.Add(new TextBlock { Text = "Output file type", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_outputFormat);
        panel.Children.Add(new TextBlock
        {
            Text = "Save creates the conventional sidecar definition. Run once keeps this configuration only for the current profile session.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var runOnce = new Button { Content = "Run once", Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        runOnce.Click += RunOnce_Click;
        var save = new Button { Content = "Save definition", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 110 };
        save.Click += Save_Click;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(runOnce);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        UpdateFields();
    }

    private void UpdateFields()
    {
        var producesOutput = SelectedType is ExecutionType.LispResult or ExecutionType.LispReport or ExecutionType.LispInputReport;
        _outputFormat.IsEnabled = producesOutput;
        if (!producesOutput) _outputFormat.Text = string.Empty;
    }

    private ExecutionType SelectedType => ((ExecutionTypeChoice)_type.SelectedItem!).Type;

    private ExecutionDefinitionDraft? ValidateDraft()
    {
        var function = _function.Text.Trim();
        if (string.IsNullOrWhiteSpace(function) || function.IndexOfAny(['(', ')', '"']) >= 0)
        {
            MessageBox.Show(this, "Enter the explicit AutoLISP entry point.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var outputFormat = _outputFormat.Text.Trim().ToLowerInvariant();
        if (SelectedType is ExecutionType.LispResult or ExecutionType.LispReport or ExecutionType.LispInputReport &&
            (string.IsNullOrWhiteSpace(outputFormat) || outputFormat.Any(character => !char.IsLetterOrDigit(character))))
        {
            MessageBox.Show(this, "Enter an output file type using letters and digits only, for example csv or json.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return new ExecutionDefinitionDraft(SelectedType, _routinePath, function, string.IsNullOrWhiteSpace(outputFormat) ? null : outputFormat);
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        var draft = ValidateDraft();
        if (draft is null) return;
        UnsavedDefinition = draft;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var draft = ValidateDraft();
        if (draft is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Execution definitions (*.execution.json)|*.execution.json|JSON files (*.json)|*.json",
            DefaultExt = ".execution.json",
            FileName = Path.GetFileNameWithoutExtension(_routinePath) + ".execution.json",
            InitialDirectory = Path.GetDirectoryName(_routinePath)
        };
        if (dialog.ShowDialog(this) != true) return;

        var definitionDirectory = Path.GetDirectoryName(dialog.FileName)!;
        var definition = new Dictionary<string, string?>
        {
            ["type"] = ToWireType(draft.Type),
            ["routine"] = Path.GetRelativePath(definitionDirectory, draft.RoutinePath),
            ["function"] = draft.Function,
            ["outputFormat"] = draft.OutputFormat
        };
        if (draft.OutputFormat is null) definition.Remove("outputFormat");
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(definition, new JsonSerializerOptions { WriteIndented = true }));
        SavedDefinitionPath = dialog.FileName;
        DialogResult = true;
    }

    private static string ToWireType(ExecutionType type) => type switch
    {
        ExecutionType.BlindLisp => "blind-lisp",
        ExecutionType.LispResult => "lisp-result",
        ExecutionType.LispReport => "lisp-report",
        ExecutionType.LispInputReport => "lisp-input-report",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported wizard execution type.")
    };

    private sealed record ExecutionTypeChoice(string Label, ExecutionType Type);
}
