using System.Text.Json;
using BatchAcCore.Core;

var workspace = Path.Combine(Path.GetTempPath(), "BatchAcCore.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    await VerifyPreflightAndControlledFailureAsync(workspace);
    await VerifyDuplicateCsvOutputHandlingAsync(workspace);
    await VerifyUsageAsync();
    VerifyReadOnlySaveDetection();
    VerifyCurrentProfileDefaults();
    VerifySettingsChangeNotifications();
    Console.WriteLine("All BatchAcCore verification checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Verification failed: " + exception);
    return 1;
}
finally
{
    if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
}

static async Task VerifyPreflightAndControlledFailureAsync(string workspace)
{
    var lispPath = Path.Combine(workspace, "REFREPORTCSV.lsp");
    var drawingPath = Path.Combine(workspace, "sample.dwg");
    var listPath = Path.Combine(workspace, "drawings.txt");
    var settingsPath = Path.Combine(workspace, "settings.json");
    File.WriteAllText(lispPath, "(defun REFREPORTCSV (output-folder) output-folder)\n");
    File.WriteAllText(drawingPath, "This is a validation placeholder, not a DWG.");
    File.WriteAllText(listPath, "sample.dwg\n");

    var settings = new BatchSettings
    {
        AcCoreConsolePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        LispFilePath = lispPath,
        FileListPath = listPath,
        WorkerCount = 1,
        TimeoutMinutes = 1,
        SaveAfterRun = false,
        CreateLogFiles = true,
        WorkDirectory = Path.Combine(workspace, "work"),
        ResultsDirectory = Path.Combine(workspace, "results")
    };

    var preflight = BatchPreflight.Check(settings, workspace);
    Assert(preflight.CanRun, "Expected the controlled validation profile to pass preflight.");
    Assert(preflight.Drawings.Count == 1, "Preflight should resolve one drawing.");
    Assert(preflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Pass, Check: "Core Console" }), "Preflight must report a found Core Console executable.");

    var originalAttributes = File.GetAttributes(drawingPath);
    try
    {
        File.SetAttributes(drawingPath, originalAttributes | FileAttributes.ReadOnly);
        settings.SaveAfterRun = true;
        var readOnlyPreflight = BatchPreflight.Check(settings, workspace);
        Assert(readOnlyPreflight.CanRun, "A read-only drawing must warn but not block preflight.");
        Assert(readOnlyPreflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Warning, Check: "Drawing changes" } && diagnostic.Message.Contains("Windows read-only attribute", StringComparison.Ordinal)), "A read-only drawing must be reported before a save-enabled batch.");
    }
    finally
    {
        File.SetAttributes(drawingPath, originalAttributes);
        settings.SaveAfterRun = false;
    }

    var invalidSettings = JsonSerializer.Deserialize<BatchSettings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("Could not copy the validation profile.");
    invalidSettings.AcCoreConsolePath = Path.Combine(workspace, "missing-accoreconsole.exe");
    var invalidPreflight = BatchPreflight.Check(invalidSettings, workspace);
    Assert(!invalidPreflight.CanRun, "A missing Core Console executable must block the batch.");
    Assert(invalidPreflight.Diagnostics.Single().Check == "Core Console", "A missing Core Console executable must be reported under the Core Console check.");

    File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
    var output = new CapturedOutput();
    var result = await BatchRunner.RunWithResultAsync([settingsPath], output);

    Assert(result.ExitCode == 1, "A process that writes no completion marker must fail the batch.");
    Assert(result.Jobs.Count == 1, "The run should return one job result.");
    var job = result.Jobs[0];
    Assert(job.Status == "Failed", "A missing completion marker must be reported as failed.");
    Assert(job.ExitCode == 0, "cmd.exe should provide the controlled zero process exit code.");
    Assert(job.Error == "No completion marker was written.", "The missing marker error must be preserved.");
    Assert(job.LogPath is not null && File.Exists(job.LogPath), "The per-job log path must be retained.");
    Assert(result.StructuredSummaryPath is not null && File.Exists(result.StructuredSummaryPath), "The structured summary must be written.");
    Assert(result.ReadableSummaryPath is not null && File.Exists(result.ReadableSummaryPath), "The readable summary must be written.");
    Assert(result.CombinedCsvPath is null, "No combined CSV should be reported when no per-drawing CSV exists.");
    Assert(output.Errors.Any(message => message.Contains("Expected 1 CSV file", StringComparison.Ordinal)), "The missing CSV diagnostic must be written to output.");
}

static async Task VerifyUsageAsync()
{
    var output = new CapturedOutput();
    var exitCode = await BatchRunner.RunAsync(["--help"], output);
    Assert(exitCode == 0, "The CLI help invocation must retain exit code 0.");
    Assert(output.Lines.Any(message => message.StartsWith("Usage:", StringComparison.Ordinal)), "The CLI usage line must be retained.");
}

static async Task VerifyDuplicateCsvOutputHandlingAsync(string workspace)
{
    var duplicateRoot = Path.Combine(workspace, "duplicate-output");
    var firstDirectory = Path.Combine(duplicateRoot, "first");
    var secondDirectory = Path.Combine(duplicateRoot, "second");
    var listPath = Path.Combine(duplicateRoot, "drawings.txt");
    var lispPath = Path.Combine(duplicateRoot, "DUPLICATE.lsp");
    var settingsPath = Path.Combine(duplicateRoot, "settings.json");
    var firstDrawing = Path.Combine(firstDirectory, "same-name.dwg");
    var secondDrawing = Path.Combine(secondDirectory, "same-name.dwg");
    Directory.CreateDirectory(firstDirectory);
    Directory.CreateDirectory(secondDirectory);
    File.WriteAllText(firstDrawing, "First placeholder drawing.");
    File.WriteAllText(secondDrawing, "Second placeholder drawing.");
    File.WriteAllText(lispPath, "(defun DUPLICATE (output-folder) output-folder)\n");
    File.WriteAllLines(listPath, [firstDrawing, secondDrawing]);

    var settings = new BatchSettings
    {
        AcCoreConsolePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        LispFilePath = lispPath,
        FileListPath = listPath,
        WorkerCount = 1,
        TimeoutMinutes = 1,
        SaveAfterRun = false,
        CreateLogFiles = false,
        WorkDirectory = Path.Combine(duplicateRoot, "work"),
        ResultsDirectory = Path.Combine(duplicateRoot, "results")
    };

    var preflight = BatchPreflight.Check(settings, duplicateRoot);
    Assert(preflight.CanRun, "Duplicate derived CSV names must warn but not block preflight.");
    Assert(preflight.Drawings.Count == 1 && preflight.OmittedDrawings.Count == 1, "Preflight must retain one drawing and identify the duplicate-output drawing.");
    Assert(preflight.OmittedDrawings[0].Drawing == secondDrawing && preflight.OmittedDrawings[0].RetainedDrawing == firstDrawing, "The first drawing-list entry must be retained for a duplicate CSV output.");
    Assert(preflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Warning, Check: "CSV output" }), "Duplicate derived CSV names must appear as a CSV-output warning.");

    File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
    var result = await BatchRunner.RunWithResultAsync([settingsPath], new CapturedOutput());
    Assert(result.Jobs.Count == 2, "The batch summary must include both the processed and duplicate-output drawing.");
    Assert(result.Jobs.Single(job => job.Drawing == secondDrawing).Status == "Skipped", "The duplicate-output drawing must be skipped instead of processed.");
    Assert(result.Issues.Any(issue => issue.Contains("derived CSV output filenames", StringComparison.Ordinal)), "The batch issues must note duplicate-output omissions.");
    Assert(result.ReadableSummaryPath is not null && File.ReadAllText(result.ReadableSummaryPath).Contains(secondDrawing, StringComparison.Ordinal), "The readable summary must list the skipped duplicate-output drawing.");
}

static void VerifyReadOnlySaveDetection()
{
    Assert(BatchRunner.ReportsReadOnlyDrawing("Warning: Drawing is read-only; changes will not be saved."), "A read-only drawing warning must be detected.");
    Assert(BatchRunner.ReportsReadOnlyDrawing("READ ONLY DWG FILE cannot be saved."), "A read-only DWG warning must be detected regardless of case.");
    Assert(!BatchRunner.ReportsReadOnlyDrawing("The report contains a read-only field."), "Unrelated read-only text must not be treated as a drawing save warning.");
}

static void VerifyCurrentProfileDefaults()
{
    var settings = new BatchSettings();
    Assert(settings.AcCoreConsolePath == @"C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe", "New CLI and GUI profiles must default to the AutoCAD 2026 Core Console path.");
    Assert(settings.WorkerCount == 4, "New CLI and GUI profiles must default to four workers.");
    Assert(settings.TimeoutMinutes == 10, "New CLI and GUI profiles must default to a ten-minute timeout.");
    Assert(!settings.SaveAfterRun, "New CLI and GUI profiles must not save drawings by default.");
    Assert(settings.CreateLogFiles, "New CLI and GUI profiles must create per-job logs by default.");
    var profileJson = JsonSerializer.Serialize(settings);
    Assert(profileJson.Contains("\"ResultsDirectory\"", StringComparison.Ordinal) && !profileJson.Contains("CombinedCsvOutputDirectory", StringComparison.Ordinal), "New CLI and GUI profiles must use ResultsDirectory as the shared output setting.");
}

static void VerifySettingsChangeNotifications()
{
    var settings = new BatchSettings();
    var changedProperties = new List<string?>();
    settings.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

    settings.CreateLogFiles = false;
    settings.TimeoutMinutes = 45;

    Assert(changedProperties.SequenceEqual([nameof(BatchSettings.CreateLogFiles), nameof(BatchSettings.TimeoutMinutes)]), "Batch settings must report profile edits so the GUI can identify stale results.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class CapturedOutput : IBatchOutput
{
    public List<string> Lines { get; } = [];
    public List<string> Errors { get; } = [];
    public void WriteLine(string message) => Lines.Add(message);
    public void WriteError(string message) => Errors.Add(message);
}
