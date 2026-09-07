namespace BatchAcCore.Core;

public enum PreflightSeverity
{
    Pass,
    Warning,
    Error
}

public sealed record PreflightDiagnostic(PreflightSeverity Severity, string Check, string Message);

public sealed record BatchPreflightReport(
    BatchSettings? Settings,
    IReadOnlyList<string> Drawings,
    IReadOnlyList<PreflightDiagnostic> Diagnostics)
{
    public bool CanRun => Settings is not null && Diagnostics.All(diagnostic => diagnostic.Severity != PreflightSeverity.Error);
}

public static class BatchPreflight
{
    public static BatchPreflightReport Check(BatchSettings settings, string profileDirectory)
    {
        var diagnostics = new List<PreflightDiagnostic>();
        try
        {
            settings.Normalize(profileDirectory);
            diagnostics.Add(new(PreflightSeverity.Pass, "Core Console", $"Found executable: {settings.AcCoreConsolePath}"));
            diagnostics.Add(new(PreflightSeverity.Pass, "AutoLISP routine", $"Validated one-argument function '{settings.RoutineFunction}' in: {settings.LispFilePath}"));
            diagnostics.Add(new(PreflightSeverity.Pass, "Execution settings", $"Using {settings.WorkerCount} worker(s) with a {settings.TimeoutMinutes}-minute timeout."));
        }
        catch (Exception exception)
        {
            diagnostics.Add(new(PreflightSeverity.Error, GetValidationCheck(exception), exception.Message));
            return new(null, [], diagnostics);
        }

        string[] drawings;
        try
        {
            drawings = BatchRunner.DiscoverDrawings(settings)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new(PreflightSeverity.Error, "Drawing input", exception.Message));
            return new(settings, [], diagnostics);
        }

        if (drawings.Length == 0)
            diagnostics.Add(new(PreflightSeverity.Error, "Drawing input", "No DWG files were found."));
        else
            diagnostics.Add(new(PreflightSeverity.Pass, "Drawing input", $"Resolved {drawings.Length} drawing(s)."));

        var expectedCsvFiles = BatchRunner.GetExpectedCsvFiles(settings.WorkDirectory!, drawings, settings.RoutineFunction);
        if (expectedCsvFiles.Count != drawings.Length)
            diagnostics.Add(new(PreflightSeverity.Error, "CSV output", "Each drawing must have a unique filename because CSV output names are derived from the drawing filename and LISP function name."));
        else
            diagnostics.Add(new(PreflightSeverity.Pass, "CSV output", "Each drawing has a unique expected CSV output name."));

        ReportDirectory(diagnostics, "Work directory", settings.WorkDirectory!);
        ReportDirectory(diagnostics, "Combined output directory", settings.CombinedCsvOutputDirectory!);

        if (settings.SaveAfterRun)
        {
            diagnostics.Add(new(PreflightSeverity.Warning, "Drawing changes", "SaveAfterRun is enabled; each successful job may save its DWG in place."));
            ReportReadOnlyDrawings(diagnostics, drawings);
        }
        if (UsesMappedDrive(settings))
            diagnostics.Add(new(PreflightSeverity.Warning, "Path portability", "A mapped drive is in use. Prefer a UNC path if Core Console runs under a different access context."));

        return new(settings, drawings, diagnostics);
    }

    private static string GetValidationCheck(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("AcCoreConsolePath", StringComparison.OrdinalIgnoreCase))
            return "Core Console";
        if (message.Contains("LispFilePath", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("function '", StringComparison.OrdinalIgnoreCase))
            return "AutoLISP routine";
        if (message.Contains("FileListPath", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("InputDirectory", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exactly one", StringComparison.OrdinalIgnoreCase))
            return "Drawing input";
        if (message.Contains("WorkDirectory", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("CombinedCsvOutputDirectory", StringComparison.OrdinalIgnoreCase))
            return "Output directories";
        return "Execution settings";
    }

    private static void ReportDirectory(ICollection<PreflightDiagnostic> diagnostics, string check, string path)
    {
        if (Directory.Exists(path))
        {
            diagnostics.Add(new(PreflightSeverity.Pass, check, $"Directory exists: {path}"));
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            diagnostics.Add(new(PreflightSeverity.Warning, check, $"Directory will be created when the batch starts: {path}"));
        else
            diagnostics.Add(new(PreflightSeverity.Warning, check, $"Directory will be created when the batch starts if its parent is writable: {path}"));
    }

    private static void ReportReadOnlyDrawings(ICollection<PreflightDiagnostic> diagnostics, IReadOnlyList<string> drawings)
    {
        var readOnlyDrawings = new List<string>();
        var inaccessibleDrawings = new List<string>();

        foreach (var drawing in drawings)
        {
            try
            {
                if ((File.GetAttributes(drawing) & FileAttributes.ReadOnly) != 0)
                    readOnlyDrawings.Add(drawing);
            }
            catch (UnauthorizedAccessException)
            {
                inaccessibleDrawings.Add(drawing);
            }
            catch (IOException)
            {
                inaccessibleDrawings.Add(drawing);
            }
        }

        if (readOnlyDrawings.Count > 0)
        {
            var examples = string.Join(", ", readOnlyDrawings.Take(3).Select(Path.GetFileName));
            var remainder = readOnlyDrawings.Count > 3 ? $" (and {readOnlyDrawings.Count - 3} more)" : string.Empty;
            diagnostics.Add(new(PreflightSeverity.Warning, "Drawing changes", $"{readOnlyDrawings.Count} drawing(s) have the Windows read-only attribute: {examples}{remainder}. Saving may fail; for ACC/Forma files, also check cloud lock status and Desktop Connector sync state."));
        }

        if (inaccessibleDrawings.Count > 0)
            diagnostics.Add(new(PreflightSeverity.Warning, "Drawing changes", $"Could not read file attributes for {inaccessibleDrawings.Count} drawing(s). Save permission cannot be confirmed before the batch runs."));
    }

    private static bool UsesMappedDrive(BatchSettings settings) =>
        new[] { settings.AcCoreConsolePath, settings.LispFilePath, settings.FileListPath, settings.InputDirectory, settings.WorkDirectory, settings.CombinedCsvOutputDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(IsNetworkDrive);

    private static bool IsNetworkDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
