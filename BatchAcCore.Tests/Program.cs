using System.Text.Json;
using BatchAcCore.Core;

var workspace = Path.Combine(Path.GetTempPath(), "BatchAcCore.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    await VerifyPreflightAndControlledFailureAsync(workspace);
    await VerifyUsageAsync();
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
        CombinedCsvOutputDirectory = Path.Combine(workspace, "combined")
    };

    var preflight = BatchPreflight.Check(settings, workspace);
    Assert(preflight.CanRun, "Expected the controlled validation profile to pass preflight.");
    Assert(preflight.Drawings.Count == 1, "Preflight should resolve one drawing.");

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
