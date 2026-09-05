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
            diagnostics.Add(new(PreflightSeverity.Pass, "Profile", "Required paths and execution settings are valid."));
        }
        catch (Exception exception)
        {
            diagnostics.Add(new(PreflightSeverity.Error, "Profile", exception.Message));
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
            diagnostics.Add(new(PreflightSeverity.Warning, "Drawing changes", "SaveAfterRun is enabled; each successful job may save its DWG in place."));
        if (UsesMappedDrive(settings))
            diagnostics.Add(new(PreflightSeverity.Warning, "Path portability", "A mapped drive is in use. Prefer a UNC path if Core Console runs under a different access context."));

        return new(settings, drawings, diagnostics);
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
