using System.Text.Json;

namespace BatchAcCore.Core;

public enum ExecutionType
{
    BlindScript,
    BlindLisp,
    LispResult,
    LispReport,
    LispInputReport
}

public sealed class ExecutionDefinition
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string? Type { get; set; }
    public string? Routine { get; set; }
    public string? Function { get; set; }
    public string? OutputFormat { get; set; }

    public static ResolvedExecutionDefinition Load(string definitionPath)
    {
        if (string.IsNullOrWhiteSpace(definitionPath))
            throw new ArgumentException("ExecutionDefinitionPath is required.");

        var fullDefinitionPath = Path.GetFullPath(definitionPath);
        if (!File.Exists(fullDefinitionPath))
            throw new FileNotFoundException("ExecutionDefinitionPath not found", fullDefinitionPath);

        ExecutionDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<ExecutionDefinition>(File.ReadAllText(fullDefinitionPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Execution definition JSON is invalid: {exception.Message}", exception);
        }

        if (definition is null)
            throw new ArgumentException("Execution definition is empty.");

        return definition.Resolve(fullDefinitionPath);
    }

    private ResolvedExecutionDefinition Resolve(string definitionPath)
    {
        var executionType = ParseType(Type);
        var routinePath = ResolveRoutinePath(Routine, definitionPath);
        var expectedExtension = executionType == ExecutionType.BlindScript ? ".scr" : ".lsp";
        if (!Path.GetExtension(routinePath).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Routine must point to a {expectedExtension} file for execution type '{Type}'.");
        if (!File.Exists(routinePath))
            throw new FileNotFoundException("Routine file not found", routinePath);

        var requiresFunction = executionType != ExecutionType.BlindScript;
        var function = NormalizeFunction(Function, requiresFunction);
        var producesOutput = executionType is ExecutionType.LispResult or ExecutionType.LispReport or ExecutionType.LispInputReport;
        var outputFormat = NormalizeOutputFormat(OutputFormat, producesOutput);

        return new ResolvedExecutionDefinition(definitionPath, executionType, routinePath, function, outputFormat);
    }

    private static ExecutionType ParseType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "blind-script" => ExecutionType.BlindScript,
        "blind-lisp" => ExecutionType.BlindLisp,
        "lisp-result" => ExecutionType.LispResult,
        "lisp-report" => ExecutionType.LispReport,
        "lisp-input-report" => ExecutionType.LispInputReport,
        _ => throw new ArgumentException("Type must be one of: blind-script, blind-lisp, lisp-result, lisp-report, lisp-input-report.")
    };

    private static string ResolveRoutinePath(string? routine, string definitionPath)
    {
        if (string.IsNullOrWhiteSpace(routine))
            throw new ArgumentException("Routine is required.");

        var definitionDirectory = Path.GetDirectoryName(definitionPath)!;
        return Path.GetFullPath(Path.IsPathFullyQualified(routine)
            ? routine
            : Path.Combine(definitionDirectory, routine));
    }

    private static string? NormalizeFunction(string? function, bool required)
    {
        if (string.IsNullOrWhiteSpace(function))
        {
            if (required) throw new ArgumentException("Function is required for LISP execution types.");
            return null;
        }

        var normalized = function.Trim();
        if (normalized.IndexOfAny(['(', ')', '"']) >= 0 || normalized.Any(char.IsWhiteSpace))
            throw new ArgumentException("Function must be a callable AutoLISP function name without whitespace, parentheses, or quotes.");
        return normalized;
    }

    private static string? NormalizeOutputFormat(string? outputFormat, bool required)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
        {
            if (required) throw new ArgumentException("OutputFormat is required for output-producing LISP execution types.");
            return null;
        }

        var normalized = outputFormat.Trim();
        if (!string.Equals(normalized, normalized.ToLowerInvariant(), StringComparison.Ordinal) || normalized is not ("csv" or "json"))
            throw new ArgumentException("OutputFormat must be one of: csv, json.");
        if (!required)
            throw new ArgumentException("OutputFormat is valid only for output-producing LISP execution types.");
        return normalized;
    }
}

public sealed record ResolvedExecutionDefinition(
    string DefinitionPath,
    ExecutionType Type,
    string RoutinePath,
    string? Function,
    string? OutputFormat)
{
    public bool RequiresSharedInputFile => Type == ExecutionType.LispInputReport;
    public bool ProducesOutput => Type is ExecutionType.LispResult or ExecutionType.LispReport or ExecutionType.LispInputReport;
}
