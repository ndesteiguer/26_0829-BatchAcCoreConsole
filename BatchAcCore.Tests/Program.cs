using System.Text.Json;
using BatchAcCore.Core;

var workspace = Path.Combine(Path.GetTempPath(), "BatchAcCore.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    await VerifyPreflightAndControlledFailureAsync(workspace);
    VerifyLispLoadFailureHandling(workspace);
    await VerifyDuplicateCsvOutputHandlingAsync(workspace);
    await VerifyUsageAsync();
    VerifyReadOnlySaveDetection();
    VerifyCurrentProfileDefaults();
    VerifySettingsChangeNotifications();
    VerifyExecutionDefinitionModels(workspace);
    await VerifyRoutineOutputCollectionAsync(workspace);
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

static void VerifyLispLoadFailureHandling(string workspace)
{
    var settings = new BatchSettings
    {
        LispFilePath = Path.Combine(workspace, "REFREPORTCSV.lsp"),
        WorkDirectory = Path.Combine(workspace, "work")
    };
    var markerPath = Path.Combine(workspace, "load-failure.result");
    var script = BatchRunner.BuildScript(settings, markerPath);

    Assert(script.Contains("(setq *error* __batchLoadError)", StringComparison.Ordinal), "The launcher must install an error handler before loading the LISP.");
    Assert(script.Contains("(defun __batchLoadError (message)", StringComparison.Ordinal), "The launcher must define a load-specific error handler.");
    Assert(script.Contains("(__batchWriteMarker \"Lisp routine failed to load.\")", StringComparison.Ordinal), "A load-time AutoLISP error must write the standard load-failure marker.");
    Assert(script.Contains("(setq *error* __batchOriginalError)\n    (", StringComparison.Ordinal), "The launcher must restore normal error handling before invoking the routine.");
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
    settings.ExecutionDefinitionPath = "routine.execution.json";

    Assert(changedProperties.SequenceEqual([nameof(BatchSettings.CreateLogFiles), nameof(BatchSettings.TimeoutMinutes), nameof(BatchSettings.ExecutionDefinitionPath)]), "Batch settings must report profile edits so the GUI can identify stale results.");
}

static void VerifyExecutionDefinitionModels(string workspace)
{
    var definitionsDirectory = Path.Combine(workspace, "execution-definitions");
    Directory.CreateDirectory(definitionsDirectory);
    File.WriteAllText(Path.Combine(definitionsDirectory, "BlindScript.scr"), "; no save or quit\n");
    File.WriteAllText(Path.Combine(definitionsDirectory, "BlindLisp.lsp"), "(princ)\n");
    File.WriteAllText(Path.Combine(definitionsDirectory, "Result.lsp"), "(princ)\n");
    File.WriteAllText(Path.Combine(definitionsDirectory, "Report.lsp"), "(princ)\n");
    File.WriteAllText(Path.Combine(definitionsDirectory, "InputReport.lsp"), "(princ)\n");
    var inputPath = Path.Combine(definitionsDirectory, "input.csv");
    File.WriteAllText(inputPath, "Reference,Path\n");

    var cases = new[]
    {
        (FileName: "blind-script.execution.json", Json: "{\"type\":\"blind-script\",\"routine\":\"BlindScript.scr\"}", Type: ExecutionType.BlindScript, Function: (string?)null, OutputFormat: (string?)null),
        (FileName: "blind-lisp.execution.json", Json: "{\"type\":\"blind-lisp\",\"routine\":\"BlindLisp.lsp\",\"function\":\"c:RUN\"}", Type: ExecutionType.BlindLisp, Function: "c:RUN", OutputFormat: (string?)null),
        (FileName: "result.execution.json", Json: "{\"type\":\"lisp-result\",\"routine\":\"Result.lsp\",\"function\":\"RESULT\",\"outputFormat\":\"json\"}", Type: ExecutionType.LispResult, Function: "RESULT", OutputFormat: "json"),
        (FileName: "report.execution.json", Json: "{\"type\":\"lisp-report\",\"routine\":\"Report.lsp\",\"function\":\"REPORT\",\"outputFormat\":\"csv\"}", Type: ExecutionType.LispReport, Function: "REPORT", OutputFormat: "csv"),
        (FileName: "artifact.execution.json", Json: "{\"type\":\"lisp-report\",\"routine\":\"Report.lsp\",\"function\":\"REPORT\",\"outputFormat\":\"xml\"}", Type: ExecutionType.LispReport, Function: "REPORT", OutputFormat: "xml"),
        (FileName: "input-report.execution.json", Json: "{\"type\":\"lisp-input-report\",\"routine\":\"InputReport.lsp\",\"function\":\"INPUTREPORT\",\"outputFormat\":\"csv\"}", Type: ExecutionType.LispInputReport, Function: "INPUTREPORT", OutputFormat: "csv")
    };

    foreach (var testCase in cases)
    {
        var definitionPath = Path.Combine(definitionsDirectory, testCase.FileName);
        File.WriteAllText(definitionPath, testCase.Json);
        var definition = ExecutionDefinition.Load(definitionPath);
        Assert(definition.Type == testCase.Type, $"{testCase.FileName} must resolve its execution type.");
        Assert(definition.Function == testCase.Function, $"{testCase.FileName} must preserve its function.");
        Assert(definition.OutputFormat == testCase.OutputFormat, $"{testCase.FileName} must preserve its output format.");
        Assert(definition.RoutinePath == Path.Combine(definitionsDirectory, Path.GetFileName(definition.RoutinePath)), $"{testCase.FileName} must resolve its routine path relative to the definition.");
    }

    var launcherSettings = new BatchSettings
    {
        SaveAfterRun = true,
        SharedInputFilePath = inputPath
    };
    var markerPath = Path.Combine(definitionsDirectory, "launcher.result");
    var outputDirectory = Path.Combine(definitionsDirectory, "batch-output");
    var escapedOutputDirectory = outputDirectory.Replace("\\", "/");
    var escapedInputPath = inputPath.Replace("\\", "/");

    var blindScriptDefinition = ExecutionDefinition.Load(Path.Combine(definitionsDirectory, "blind-script.execution.json"));
    var blindScriptLauncher = BatchRunner.BuildScript(launcherSettings, blindScriptDefinition, markerPath, null);
    Assert(blindScriptLauncher.Contains("(command \"_.SCRIPT\"", StringComparison.Ordinal), "A blind script launcher must invoke the supplied SCR file.");
    Assert(blindScriptLauncher.Contains("BlindScript.scr", StringComparison.Ordinal), "A blind script launcher must reference its routine.");
    Assert(blindScriptLauncher.Contains("(command \"_.QSAVE\")", StringComparison.Ordinal), "The application must own optional saving for scripts.");
    Assert(blindScriptLauncher.Contains("(command \"_.QUIT\" \"_Yes\")", StringComparison.Ordinal), "The application must own quitting for scripts.");

    var blindLispDefinition = ExecutionDefinition.Load(Path.Combine(definitionsDirectory, "blind-lisp.execution.json"));
    var blindLispLauncher = BatchRunner.BuildScript(launcherSettings, blindLispDefinition, markerPath, null);
    Assert(blindLispLauncher.Contains("(c:RUN)", StringComparison.Ordinal), "A blind LISP launcher must call its function with no arguments.");

    var resultDefinition = ExecutionDefinition.Load(Path.Combine(definitionsDirectory, "result.execution.json"));
    var resultLauncher = BatchRunner.BuildScript(launcherSettings, resultDefinition, markerPath, outputDirectory);
    Assert(resultLauncher.Contains($"(RESULT \"{escapedOutputDirectory}\")", StringComparison.Ordinal), "A lisp-result launcher must pass the batch output directory as its only argument.");

    var inputReportDefinition = ExecutionDefinition.Load(Path.Combine(definitionsDirectory, "input-report.execution.json"));
    var inputReportLauncher = BatchRunner.BuildScript(launcherSettings, inputReportDefinition, markerPath, outputDirectory);
    Assert(inputReportLauncher.Contains($"(INPUTREPORT \"{escapedInputPath}\" \"{escapedOutputDirectory}\")", StringComparison.Ordinal), "A lisp-input-report launcher must pass the shared input path before the batch output directory.");
    AssertThrows(() => BatchRunner.BuildScript(launcherSettings, inputReportDefinition, markerPath, null), "An output-producing LISP launcher must reject a missing batch output directory.");

    var profile = new BatchSettings
    {
        ExecutionDefinitionPath = Path.Combine("execution-definitions", "input-report.execution.json"),
        SharedInputFilePath = Path.Combine("execution-definitions", "input.csv")
    };
    var resolvedProfileDefinition = profile.LoadExecutionDefinition(workspace);
    Assert(resolvedProfileDefinition.RequiresSharedInputFile, "lisp-input-report must require the profile shared input file.");
    Assert(profile.SharedInputFilePath == inputPath, "SharedInputFilePath must resolve relative to the profile directory.");
    Assert(profile.ExecutionDefinitionPath == Path.Combine(definitionsDirectory, "input-report.execution.json"), "ExecutionDefinitionPath must resolve relative to the profile directory.");

    var drawingPath = Path.Combine(definitionsDirectory, "sample.dwg");
    var drawingListPath = Path.Combine(definitionsDirectory, "drawings.txt");
    File.WriteAllText(drawingPath, "Drawing validation placeholder.");
    File.WriteAllText(drawingListPath, "sample.dwg\n");
    var targetPreflightSettings = new BatchSettings
    {
        AcCoreConsolePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        ExecutionDefinitionPath = Path.Combine("execution-definitions", "input-report.execution.json"),
        SharedInputFilePath = Path.Combine("execution-definitions", "input.csv"),
        FileListPath = Path.Combine("execution-definitions", "drawings.txt"),
        WorkDirectory = Path.Combine("execution-definitions", "work"),
        ResultsDirectory = Path.Combine("execution-definitions", "results")
    };
    var targetPreflight = BatchPreflight.Check(targetPreflightSettings, workspace);
    Assert(targetPreflight.CanRun, "A valid target-model execution profile must pass preflight.");
    Assert(targetPreflight.Drawings.Count == 1 && targetPreflight.OmittedDrawings.Count == 0, "Target-model preflight must retain every drawing because output filenames are routine-owned.");
    Assert(targetPreflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Pass, Check: "Execution definition" }), "Target-model preflight must report its execution definition.");
    Assert(targetPreflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Pass, Check: "Shared input file" }), "Target-model preflight must report the required shared input file.");
    Assert(targetPreflight.Diagnostics.Any(diagnostic => diagnostic is { Severity: PreflightSeverity.Pass, Check: "Routine output" } && diagnostic.Message.Contains("csv", StringComparison.Ordinal)), "Target-model preflight must report the declared output format.");
    Assert(!targetPreflight.Diagnostics.Any(diagnostic => diagnostic.Check == "CSV output"), "Target-model preflight must not apply legacy derived CSV-name checks.");

    var missingInputProfile = new BatchSettings { ExecutionDefinitionPath = Path.Combine(definitionsDirectory, "input-report.execution.json") };
    AssertThrows(() => missingInputProfile.LoadExecutionDefinition(workspace), "A lisp-input-report profile must require SharedInputFilePath.");
    var missingInputPreflightSettings = new BatchSettings
    {
        AcCoreConsolePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        ExecutionDefinitionPath = Path.Combine("execution-definitions", "input-report.execution.json"),
        FileListPath = Path.Combine("execution-definitions", "drawings.txt"),
        WorkDirectory = Path.Combine("execution-definitions", "work"),
        ResultsDirectory = Path.Combine("execution-definitions", "results")
    };
    var missingInputPreflight = BatchPreflight.Check(missingInputPreflightSettings, workspace);
    Assert(!missingInputPreflight.CanRun && missingInputPreflight.Diagnostics.Single().Check == "Shared input file", "A missing shared input file must block target-model preflight.");
    AssertThrows(() => ExecutionDefinition.Load(Path.Combine(definitionsDirectory, "{\"type\":\"blind-lisp\"}.json")), "A missing execution-definition file must fail validation.");
    var invalidDefinitionPath = Path.Combine(definitionsDirectory, "invalid.execution.json");
    File.WriteAllText(invalidDefinitionPath, "{\"type\":\"lisp-report\",\"routine\":\"Report.lsp\",\"outputFormat\":\"csv\"}");
    AssertThrows(() => ExecutionDefinition.Load(invalidDefinitionPath), "A LISP execution definition without Function must fail validation.");
}

static async Task VerifyRoutineOutputCollectionAsync(string workspace)
{
    var routineOutputDirectory = Path.Combine(workspace, "routine-output");
    var resultsDirectory = Path.Combine(workspace, "combined-output");
    Directory.CreateDirectory(Path.Combine(routineOutputDirectory, "nested"));
    File.WriteAllText(Path.Combine(routineOutputDirectory, "first.json"), "{\"drawing\":\"first\"}");
    File.WriteAllText(Path.Combine(routineOutputDirectory, "nested", "second.json"), "[{\"drawing\":\"second\"}]");
    File.WriteAllText(Path.Combine(routineOutputDirectory, "ignored.txt"), "not routine output");

    var jsonFiles = BatchRunner.FindRoutineOutputFiles(routineOutputDirectory, "json");
    Assert(jsonFiles.Count == 2, "Routine output collection must find only declared-format files, including nested routine-created folders.");
    var combinedJsonPath = await BatchRunner.CombineOutputFilesAsync(resultsDirectory, "json", jsonFiles);
    using (var combinedJson = JsonDocument.Parse(File.ReadAllText(combinedJsonPath)))
    {
        Assert(combinedJson.RootElement.ValueKind == JsonValueKind.Array && combinedJson.RootElement.GetArrayLength() == 2, "Combined JSON output must be a valid array containing one element per routine output file.");
    }

    File.WriteAllText(Path.Combine(routineOutputDirectory, "first.csv"), "Drawing,Value\nfirst,1\n");
    File.WriteAllText(Path.Combine(routineOutputDirectory, "second.csv"), "Drawing,Value\nsecond,2\n");
    var csvFiles = BatchRunner.FindRoutineOutputFiles(routineOutputDirectory, "csv");
    var combinedCsvPath = await BatchRunner.CombineOutputFilesAsync(resultsDirectory, "csv", csvFiles);
    var csvLines = File.ReadAllLines(combinedCsvPath);
    Assert(csvLines.SequenceEqual(["Drawing,Value", "first,1", "second,2"]), "Combined CSV output must retain one header and all data rows.");
    Assert(!BatchRunner.IsSupportedOutputFormat("xml"), "An unsupported declared format must be reported as an artifact rather than sent to a CSV or JSON combiner.");
}

static void AssertThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }
    catch (FileNotFoundException)
    {
        return;
    }

    throw new InvalidOperationException(message);
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
