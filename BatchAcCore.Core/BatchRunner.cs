using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatchAcCore.Core;

public interface IBatchOutput
{
    void WriteLine(string message);
    void WriteError(string message);
}

public enum BatchEventKind
{
    BatchStarted,
    JobQueued,
    JobStarted,
    JobCompleted,
    Warning,
    BatchCompleted
}

public sealed record BatchProgressEvent(
    BatchEventKind Kind,
    DateTimeOffset OccurredUtc,
    string? Drawing = null,
    int? WorkerId = null,
    string? Status = null,
    string? Message = null,
    JobResult? Result = null);

public static class BatchRunner
{
    private const string LispLoadFailure = "Lisp routine failed to load.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(
        string[] args,
        IBatchOutput output,
        IProgress<BatchProgressEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        (await RunWithResultAsync(args, output, progress, cancellationToken)).ExitCode;

    public static async Task<BatchRunResult> RunWithResultAsync(
        string[] args,
        IBatchOutput output,
        IProgress<BatchProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 1 || args[0] is "--help" or "-h")
        {
            output.WriteLine("Usage: BatchAcCoreConsole <settings.json>");
            output.WriteLine("Copy settings.example.json, set the paths and run this command.");
            return new(args.Length == 1 ? 0 : 2, [], null, null, null, [], false);
        }

        var settingsFile = Path.GetFullPath(args[0]);
        if (!File.Exists(settingsFile)) return Fail(output, $"Settings file not found: {settingsFile}");

        BatchSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<BatchSettings>(await File.ReadAllTextAsync(settingsFile), JsonOptions);
        }
        catch (JsonException exception)
        {
            return Fail(output, $"Invalid JSON: {exception.Message}");
        }

        if (settings is null) return Fail(output, "Settings file is empty.");
        var baseDirectory = Path.GetDirectoryName(settingsFile)!;
        try { settings.Normalize(baseDirectory); }
        catch (Exception exception) { return Fail(output, exception.Message); }

        string[] drawings;
        try
        {
            drawings = DiscoverDrawings(settings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (ArgumentException exception)
        {
            return Fail(output, exception.Message);
        }
        if (drawings.Length == 0) return Fail(output, "No DWG files were found.");
        var expectedCsvFiles = GetExpectedCsvFiles(settings.WorkDirectory!, drawings, settings.RoutineFunction);
        if (expectedCsvFiles.Count != drawings.Length)
            return Fail(output, "Each drawing must have a unique filename because CSV output names are derived from the drawing filename and LISP function name.");

        Directory.CreateDirectory(settings.WorkDirectory!);
        var isolateRoot = Path.Combine(Path.GetTempPath(), $"BatchAcCoreConsole-{Guid.NewGuid():N}");
        var csvFilesBeforeBatch = SnapshotCsvFiles(settings.WorkDirectory!);
        output.WriteLine($"Queued {drawings.Length} drawing(s), using {settings.WorkerCount} worker(s).");
        output.WriteLine($"Work directory: {settings.WorkDirectory}");
        Report(progress, BatchEventKind.BatchStarted, message: $"Queued {drawings.Length} drawing(s).");
        foreach (var drawing in drawings)
            Report(progress, BatchEventKind.JobQueued, drawing: drawing, status: "Queued");

        var results = new ConcurrentBag<JobResult>();
        var pendingDrawings = new ConcurrentQueue<string>(drawings);
        var lispLoadFailureDetected = 0;
        var workers = Enumerable.Range(1, settings.WorkerCount).Select(async workerId =>
        {
            while (pendingDrawings.TryDequeue(out var drawing))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    var cancelledAt = DateTimeOffset.UtcNow;
                    const string reason = "Cancelled before processing started.";
                    var queuedCancellation = new JobResult(drawing, "Cancelled", null, cancelledAt, cancelledAt, null, reason);
                    results.Add(queuedCancellation);
                    Report(progress, BatchEventKind.JobCompleted, drawing, workerId, "Cancelled", reason, queuedCancellation);
                    break;
                }

                if (Volatile.Read(ref lispLoadFailureDetected) != 0)
                {
                    var skippedAt = DateTimeOffset.UtcNow;
                    const string reason = "Skipped because the LISP routine failed to load in another worker.";
                    var skippedResult = new JobResult(drawing, "Skipped", null, skippedAt, skippedAt, null, reason);
                    results.Add(skippedResult);
                    output.WriteLine($"SKIPPED {Path.GetFileName(drawing)} ({reason})");
                    Report(progress, BatchEventKind.JobCompleted, drawing, workerId, "Skipped", reason, skippedResult);
                    continue;
                }

                Report(progress, BatchEventKind.JobStarted, drawing, workerId, "Running");
                var result = await RunJobAsync(settings, drawing, isolateRoot, workerId, output);
                if (string.Equals(result.Error, LispLoadFailure, StringComparison.Ordinal))
                    Interlocked.Exchange(ref lispLoadFailureDetected, 1);
                results.Add(result);
                Report(progress, BatchEventKind.JobCompleted, drawing, workerId, result.Status, result.Error, result);
            }
        });
        await Task.WhenAll(workers);
        while (pendingDrawings.TryDequeue(out var drawing))
        {
            var cancelledAt = DateTimeOffset.UtcNow;
            const string reason = "Cancelled before processing started.";
            var result = new JobResult(drawing, "Cancelled", null, cancelledAt, cancelledAt, null, reason);
            results.Add(result);
            Report(progress, BatchEventKind.JobCompleted, drawing, status: "Cancelled", message: reason, result: result);
        }
        if (!TryDeleteDirectory(isolateRoot))
        {
            output.WriteError($"Could not remove temporary Core Console profile data: {isolateRoot}");
            Report(progress, BatchEventKind.Warning, message: $"Could not remove temporary Core Console profile data: {isolateRoot}");
        }

        var ordered = results.OrderBy(r => r.Drawing, StringComparer.OrdinalIgnoreCase).ToArray();
        var succeeded = ordered.Count(result => result.Status == "Succeeded");
        var skipped = ordered.Count(result => result.Status == "Skipped");
        var cancelled = ordered.Count(result => result.Status == "Cancelled");
        var failed = ordered.Length - succeeded - skipped - cancelled;
        var elapsedRuntime = ordered.Max(result => result.FinishedUtc) - ordered.Min(result => result.StartedUtc);
        string? combinedCsvPath = null;
        string? combinationError = null;
        var issues = new List<string>();
        if (Volatile.Read(ref lispLoadFailureDetected) != 0)
        {
            const string issue = "CSV combination skipped because the LISP routine failed to load.";
            issues.Add(issue);
            output.WriteError(issue);
        }
        else
        {
            var batchCsvFiles = GetBatchCsvFiles(expectedCsvFiles, csvFilesBeforeBatch).ToArray();
            if (batchCsvFiles.Length != expectedCsvFiles.Count)
            {
                combinationError = $"Expected {expectedCsvFiles.Count} CSV file(s) from this batch, but found {batchCsvFiles.Length}. The combined CSV includes every expected CSV that was found.";
                var missingCsvFiles = GetMissingExpectedCsvFiles(expectedCsvFiles, batchCsvFiles);
                if (missingCsvFiles.Count > 0)
                    combinationError = $"{combinationError}{Environment.NewLine}Missing expected CSV file(s):{Environment.NewLine}{string.Join(Environment.NewLine, missingCsvFiles)}";
                issues.Add(combinationError);
                output.WriteError(combinationError);
            }

            try
            {
                combinedCsvPath = await CombineCsvFilesAsync(settings.CombinedCsvOutputDirectory!, batchCsvFiles);
                output.WriteLine($"Combined CSV: {combinedCsvPath}");
            }
            catch (Exception exception)
            {
                combinationError = combinationError is null ? exception.Message : $"{combinationError}{Environment.NewLine}{exception.Message}";
                var issue = $"CSV combination failed: {exception.Message}";
                issues.Add(issue);
                output.WriteError(issue);
            }
        }

        var summaryPath = Path.Combine(settings.WorkDirectory!, $"summary-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(ordered, JsonOptions));
        var readableSummaryCandidate = Path.Combine(settings.CombinedCsvOutputDirectory!, $"batch-summary-{DateTime.UtcNow:yyyyMMddHHmmssfff}.txt");
        string? readableSummaryPath = null;
        try
        {
            Directory.CreateDirectory(settings.CombinedCsvOutputDirectory!);
            await File.WriteAllTextAsync(readableSummaryCandidate, BuildReadableSummary(settings, ordered, succeeded, failed, skipped, cancelled, elapsedRuntime, combinedCsvPath, issues, summaryPath));
            readableSummaryPath = readableSummaryCandidate;
            output.WriteLine($"Batch summary: {readableSummaryPath}");
        }
        catch (Exception exception)
        {
            combinationError = combinationError is null ? exception.Message : $"{combinationError}{Environment.NewLine}{exception.Message}";
            output.WriteError($"Could not write batch summary: {exception.Message}");
        }
        var completionMessage = cancelled == 0
            ? $"Finished: {succeeded} succeeded, {failed} failed, {skipped} skipped. Summary: {summaryPath}"
            : $"Finished: {succeeded} succeeded, {failed} failed, {skipped} skipped, {cancelled} cancelled. Summary: {summaryPath}";
        output.WriteLine(completionMessage);
        Report(progress, BatchEventKind.BatchCompleted, status: cancelled > 0 ? "Cancelled" : "Completed", message: $"{succeeded} succeeded, {failed} failed, {skipped} skipped, {cancelled} cancelled.");
        var exitCode = succeeded == ordered.Length && combinationError is null ? 0 : 1;
        return new(exitCode, ordered, summaryPath, readableSummaryPath, combinedCsvPath, issues, cancelled > 0);
    }

    private static async Task<JobResult> RunJobAsync(BatchSettings settings, string drawing, string isolateRoot, int workerId, IBatchOutput output)
    {
        var jobId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var scriptDirectory = settings.KeepScripts ? settings.WorkDirectory! : isolateRoot;
        var scriptPath = Path.Combine(scriptDirectory, $"{jobId}.scr");
        var logPath = settings.CreateLogFiles ? Path.Combine(settings.WorkDirectory!, $"{jobId}.log") : null;
        var resultPath = Path.Combine(isolateRoot, $"{jobId}.result");
        var isolateDirectory = Path.Combine(isolateRoot, $"worker-{workerId}");
        // Reuse a bounded number of isolated registry identities rather than creating one per drawing.
        var isolateUserId = $"BatchAcCoreConsole-Worker-{workerId}";
        var started = DateTimeOffset.UtcNow;
        output.WriteLine($"START {Path.GetFileName(drawing)}");

        try
        {
            Directory.CreateDirectory(isolateDirectory);
            await File.WriteAllTextAsync(scriptPath, BuildScript(settings, resultPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = settings.AcCoreConsolePath!,
                    Arguments = $"/i \"{drawing}\" /s \"{scriptPath}\" /isolate \"{isolateUserId}\" \"{isolateDirectory}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var processOutput = CaptureProcessOutputAsync(process, settings.CreateLogFiles || settings.SaveAfterRun);
            var completion = process.WaitForExitAsync();
            var exited = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMinutes(settings.TimeoutMinutes))) == completion;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var outputText = await processOutput;
                if (outputText is not null && logPath is not null) await File.WriteAllTextAsync(logPath, outputText);
                return new(drawing, "TimedOut", null, started, DateTimeOffset.UtcNow, logPath, "Worker exceeded configured timeout.");
            }

            var completedOutput = await processOutput;
            if (completedOutput is not null && logPath is not null) await File.WriteAllTextAsync(logPath, completedOutput);
            var lispResult = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "No completion marker was written.";
            File.Delete(resultPath);
            var status = process.ExitCode == 0 && lispResult.Trim() == "OK" ? "Succeeded" : "Failed";
            var error = status == "Succeeded" ? null : lispResult.Trim();
            if (status == "Succeeded" && settings.SaveAfterRun && ReportsReadOnlyDrawing(completedOutput))
            {
                status = "Failed";
                error = "SaveAfterRun is enabled, but Core Console reported that the drawing is read-only and could not be saved.";
            }
            output.WriteLine($"{status.ToUpperInvariant()} {Path.GetFileName(drawing)} (exit {process.ExitCode})");
            return new(drawing, status, process.ExitCode, started, DateTimeOffset.UtcNow, logPath, error);
        }
        catch (Exception exception)
        {
            if (logPath is not null) await File.WriteAllTextAsync(logPath, exception.ToString());
            output.WriteError($"FAILED {Path.GetFileName(drawing)}: {exception.Message}");
            return new(drawing, "Failed", null, started, DateTimeOffset.UtcNow, logPath, exception.Message);
        }
        finally
        {
            // A fresh script is made for every run. Retain scripts in WorkDirectory only when explicitly requested.
            if (!settings.KeepScripts && File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
    }

    private static async Task<string?> CaptureProcessOutputAsync(Process process, bool retainOutput)
    {
        if (retainOutput)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            return (await stdout) + Environment.NewLine + (await stderr);
        }

        await Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
        return null;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer, 0, buffer.Length) > 0) { }
    }

    internal static bool ReportsReadOnlyDrawing(string? processOutput) =>
        !string.IsNullOrWhiteSpace(processOutput) &&
        Regex.IsMatch(
            processOutput,
            @"\b(?:drawing|dwg|file)\b[\s\S]{0,120}\b(?:read[-\s]?only|readonly)\b|\b(?:read[-\s]?only|readonly)\b[\s\S]{0,120}\b(?:drawing|dwg|file)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BuildScript(BatchSettings settings, string resultPath)
    {
        var lispPath = EscapeLispString(settings.LispFilePath!);
        var workDirectory = EscapeLispString(settings.WorkDirectory!);
        var markerPath = EscapeLispString(resultPath);
        var lispExpression = $"({settings.RoutineFunction} \"{workDirectory}\")";
        var save = settings.SaveAfterRun ? "(command \"_.QSAVE\")\n" : string.Empty;
        // Keep the launcher script compatible with the Core Console subset: no Visual LISP / COM functions.
        // Do not write an OK marker if the LISP cannot load; Core Console can otherwise exit successfully after a load error.
        return $"(setvar \"FILEDIA\" 0)\n(setvar \"CMDDIA\" 0)\n(if (load \"{lispPath}\")\n  (progn\n    {lispExpression}\n    {save}(setq __batchMarker (open \"{markerPath}\" \"w\"))\n    (write-line \"OK\" __batchMarker)\n    (close __batchMarker)\n  )\n  (progn\n    (setq __batchMarker (open \"{markerPath}\" \"w\"))\n    (write-line \"{LispLoadFailure}\" __batchMarker)\n    (close __batchMarker)\n  )\n)\n(command \"_.QUIT\" \"_Yes\")\n";
    }

    private static string EscapeLispString(string value) => value.Replace("\\", "/").Replace("\"", "\\\"");

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Dictionary<string, CsvFileStamp> SnapshotCsvFiles(string directory) => Directory
        .EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
        .ToDictionary(path => Path.GetFullPath(path), GetCsvFileStamp, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> GetExpectedCsvFiles(string workDirectory, IReadOnlyList<string> drawings, string routineFunction) => drawings
        .Select(drawing => Path.Combine(workDirectory, $"{Path.GetFileNameWithoutExtension(drawing)}.{routineFunction}.csv"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static IEnumerable<string> GetBatchCsvFiles(IReadOnlyList<string> expectedCsvFiles, IReadOnlyDictionary<string, CsvFileStamp> beforeBatch) => expectedCsvFiles
        .Where(File.Exists)
        .Where(path => !beforeBatch.TryGetValue(path, out var priorStamp) || priorStamp != GetCsvFileStamp(path));

    private static IReadOnlyList<string> GetMissingExpectedCsvFiles(IReadOnlyList<string> expectedCsvFiles, IReadOnlyList<string> batchCsvFiles)
    {
        var foundNames = batchCsvFiles
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedCsvFiles
            .Where(path => !foundNames.Contains(Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CsvFileStamp GetCsvFileStamp(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc);
    }

    private static async Task<string> CombineCsvFilesAsync(string outputDirectory, IReadOnlyList<string> inputPaths)
    {
        if (inputPaths.Count == 0)
            throw new InvalidOperationException("No CSV files were found for this batch.");

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"combined-{DateTime.UtcNow:yyyyMMddHHmmssfff}.csv");
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            string? header = null;
            await using (var writer = new StreamWriter(temporaryPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var inputPath in inputPaths)
                {
                    using var reader = new StreamReader(inputPath);
                    var inputHeader = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(inputHeader))
                        throw new InvalidOperationException($"CSV file is empty or missing a header: {inputPath}");
                    if (header is null)
                    {
                        header = inputHeader;
                        await writer.WriteLineAsync(header);
                    }
                    else if (!string.Equals(header, inputHeader, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"CSV header does not match the first file: {inputPath}");
                    }

                    string? row;
                    while ((row = await reader.ReadLineAsync()) is not null)
                        await writer.WriteLineAsync(row);
                }
            }

            File.Move(temporaryPath, outputPath);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static IEnumerable<string> DiscoverDrawings(BatchSettings settings)
    {
        var drawings = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.FileListPath))
        {
            var errors = new List<string>();
            var lineNumber = 0;
            foreach (var line in File.ReadLines(settings.FileListPath!))
            {
                lineNumber++;
                var entry = line.Trim().Trim('"');
                if (entry.Length == 0 || entry.StartsWith('#')) continue;

                string path;
                try
                {
                    path = Path.IsPathFullyQualified(entry) ? entry : Path.Combine(Path.GetDirectoryName(settings.FileListPath!)!, entry);
                    path = Path.GetFullPath(path);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    if (!settings.SkipInvalidFileListEntries)
                        errors.Add($"Line {lineNumber}: invalid path '{entry}' ({exception.Message})");
                    continue;
                }

                if (!Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    if (!settings.SkipInvalidFileListEntries)
                        errors.Add($"Line {lineNumber}: not a .dwg file: {entry}");
                }
                else if (!File.Exists(path))
                {
                    if (!settings.SkipInvalidFileListEntries)
                        errors.Add($"Line {lineNumber}: drawing not found: {path}");
                }
                else
                {
                    drawings.Add(path);
                }
            }

            if (errors.Count > 0)
                throw new ArgumentException($"FileListPath contains invalid drawing entries:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
        if (!string.IsNullOrWhiteSpace(settings.InputDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(settings.InputDirectory!, "*.dwg", settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                drawings.Add(path);
        }
        return drawings;
    }

    private static string BuildReadableSummary(
        BatchSettings settings,
        IReadOnlyList<JobResult> results,
        int succeeded,
        int failed,
        int skipped,
        int cancelled,
        TimeSpan elapsedRuntime,
        string? combinedCsvPath,
        IReadOnlyList<string> issues,
        string jsonSummaryPath)
    {
        var summary = new StringBuilder();
        summary.AppendLine("Batch AcCoreConsole summary");
        summary.AppendLine(new string('=', 26));
        summary.AppendLine($"Completed (UTC): {DateTimeOffset.UtcNow:O}");
        summary.AppendLine($"Elapsed runtime (approx.): {FormatElapsedRuntime(elapsedRuntime)}");
        summary.AppendLine(cancelled == 0
            ? $"Results: {succeeded} succeeded, {failed} failed, {skipped} skipped"
            : $"Results: {succeeded} succeeded, {failed} failed, {skipped} skipped, {cancelled} cancelled");
        summary.AppendLine($"Structured summary: {jsonSummaryPath}");
        summary.AppendLine($"Combined CSV: {combinedCsvPath ?? "Not created"}");

        summary.AppendLine();
        summary.AppendLine("Effective settings:");
        summary.AppendLine($"- AcCoreConsole path: {settings.AcCoreConsolePath}");
        summary.AppendLine($"- LISP file path: {settings.LispFilePath}");
        summary.AppendLine($"- LISP function: {settings.RoutineFunction} (derived from the filename)");
        summary.AppendLine($"- File list path: {settings.FileListPath ?? "Not used"}");
        summary.AppendLine($"- Skip invalid file-list entries: {settings.SkipInvalidFileListEntries}");
        summary.AppendLine($"- Input directory: {settings.InputDirectory ?? "Not used"}");
        summary.AppendLine($"- Recursive input scan: {settings.Recursive}");
        summary.AppendLine($"- Workers: {settings.WorkerCount}");
        summary.AppendLine($"- Timeout: {settings.TimeoutMinutes} minute(s)");
        summary.AppendLine($"- Save drawings after processing: {settings.SaveAfterRun}");
        summary.AppendLine($"- Keep generated scripts: {settings.KeepScripts}");
        summary.AppendLine($"- Create per-drawing log files: {settings.CreateLogFiles}");
        summary.AppendLine($"- Work directory: {settings.WorkDirectory}");
        summary.AppendLine($"- Combined output directory: {settings.CombinedCsvOutputDirectory}");

        summary.AppendLine();
        summary.AppendLine($"Batch issues ({issues.Count}):");
        if (issues.Count == 0)
            summary.AppendLine("- None");
        else
            foreach (var issue in issues)
                summary.AppendLine($"- {issue.Replace(Environment.NewLine, Environment.NewLine + "  ")}");

        var successfulDrawings = results.Where(result => result.Status == "Succeeded").ToArray();
        summary.AppendLine();
        summary.AppendLine($"Succeeded drawings ({successfulDrawings.Length}):");
        if (successfulDrawings.Length == 0)
            summary.AppendLine("- None");
        else
            foreach (var result in successfulDrawings)
                summary.AppendLine($"- {result.Drawing}");

        var failedDrawings = results.Where(result => result.Status is not "Succeeded" and not "Skipped").ToArray();
        summary.AppendLine();
        summary.AppendLine($"Failed drawings ({failedDrawings.Length}):");
        if (failedDrawings.Length == 0)
            summary.AppendLine("- None");
        else
            foreach (var result in failedDrawings)
                summary.AppendLine($"- {result.Drawing} (exit {result.ExitCode?.ToString() ?? "not started"}): {result.Error ?? "No reason was reported."}{FormatLogPath(result.LogPath)}");

        var skippedDrawings = results.Where(result => result.Status == "Skipped").ToArray();
        summary.AppendLine();
        summary.AppendLine($"Skipped drawings ({skippedDrawings.Length}):");
        if (skippedDrawings.Length == 0)
            summary.AppendLine("- None");
        else
            foreach (var result in skippedDrawings)
                summary.AppendLine($"- {result.Drawing}: {result.Error ?? "No reason was reported."}");

        return summary.ToString();
    }

    private static string FormatLogPath(string? logPath) => logPath is null ? string.Empty : $" (log: {logPath})";

    private static string FormatElapsedRuntime(TimeSpan elapsedRuntime) => $"{(int)elapsedRuntime.TotalHours:D2}:{elapsedRuntime.Minutes:D2}:{elapsedRuntime.Seconds:D2}";

    private static void Report(
        IProgress<BatchProgressEvent>? progress,
        BatchEventKind kind,
        string? drawing = null,
        int? workerId = null,
        string? status = null,
        string? message = null,
        JobResult? result = null) =>
        progress?.Report(new BatchProgressEvent(kind, DateTimeOffset.UtcNow, drawing, workerId, status, message, result));

    private static BatchRunResult Fail(IBatchOutput output, string message)
    {
        output.WriteError($"Error: {message}");
        return new(2, [], null, null, null, [message], false);
    }
}

public sealed class BatchSettings : INotifyPropertyChanged
{
    private string? _acCoreConsolePath;
    private string? _lispFilePath;
    private string? _fileListPath;
    private bool _skipInvalidFileListEntries;
    private string? _inputDirectory;
    private bool _recursive;
    private int _workerCount = Math.Max(1, Environment.ProcessorCount / 2);
    private int _timeoutMinutes = 30;
    private bool _saveAfterRun = true;
    private bool _keepScripts;
    private bool _createLogFiles = true;
    private string? _workDirectory;
    private string? _combinedCsvOutputDirectory;

    public string? AcCoreConsolePath { get => _acCoreConsolePath; set => SetField(ref _acCoreConsolePath, value); }
    public string? LispFilePath { get => _lispFilePath; set => SetField(ref _lispFilePath, value); }
    internal string RoutineFunction { get; private set; } = string.Empty;
    public string? FileListPath { get => _fileListPath; set => SetField(ref _fileListPath, value); }
    public bool SkipInvalidFileListEntries { get => _skipInvalidFileListEntries; set => SetField(ref _skipInvalidFileListEntries, value); }
    public string? InputDirectory { get => _inputDirectory; set => SetField(ref _inputDirectory, value); }
    public bool Recursive { get => _recursive; set => SetField(ref _recursive, value); }
    public int WorkerCount { get => _workerCount; set => SetField(ref _workerCount, value); }
    public int TimeoutMinutes { get => _timeoutMinutes; set => SetField(ref _timeoutMinutes, value); }
    public bool SaveAfterRun { get => _saveAfterRun; set => SetField(ref _saveAfterRun, value); }
    public bool KeepScripts { get => _keepScripts; set => SetField(ref _keepScripts, value); }
    public bool CreateLogFiles { get => _createLogFiles; set => SetField(ref _createLogFiles, value); }
    public string? WorkDirectory { get => _workDirectory; set => SetField(ref _workDirectory, value); }
    public string? CombinedCsvOutputDirectory { get => _combinedCsvOutputDirectory; set => SetField(ref _combinedCsvOutputDirectory, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Normalize(string baseDirectory)
    {
        AcCoreConsolePath = RequiredFile(AcCoreConsolePath, "AcCoreConsolePath", baseDirectory);
        LispFilePath = RequiredFile(LispFilePath, "LispFilePath", baseDirectory);
        if (!Path.GetExtension(LispFilePath).Equals(".lsp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("LispFilePath must point to a .lsp file.");
        RoutineFunction = Path.GetFileNameWithoutExtension(LispFilePath);
        if (string.IsNullOrWhiteSpace(RoutineFunction) || RoutineFunction.Any(char.IsWhiteSpace) || RoutineFunction.IndexOfAny(['(', ')', '"']) >= 0)
            throw new ArgumentException("The filename in LispFilePath must be a valid AutoLISP function name, e.g. PROCESSDRAWING.lsp.");
        ValidateLispFunctionSignature(LispFilePath, RoutineFunction);
        if (string.IsNullOrWhiteSpace(FileListPath) == string.IsNullOrWhiteSpace(InputDirectory))
            throw new ArgumentException("Set exactly one of FileListPath or InputDirectory.");
        if (!string.IsNullOrWhiteSpace(FileListPath)) FileListPath = RequiredFile(FileListPath, "FileListPath", baseDirectory);
        if (!string.IsNullOrWhiteSpace(InputDirectory))
        {
            InputDirectory = Resolve(InputDirectory, baseDirectory);
            if (!Directory.Exists(InputDirectory)) throw new DirectoryNotFoundException($"InputDirectory not found: {InputDirectory}");
        }
        if (WorkerCount is < 1 or > 64) throw new ArgumentException("WorkerCount must be between 1 and 64.");
        if (TimeoutMinutes is < 1 or > 1440) throw new ArgumentException("TimeoutMinutes must be between 1 and 1440.");
        WorkDirectory = Resolve(string.IsNullOrWhiteSpace(WorkDirectory) ? "batch-work" : WorkDirectory, baseDirectory);
        CombinedCsvOutputDirectory = Resolve(string.IsNullOrWhiteSpace(CombinedCsvOutputDirectory) ? "combined-output" : CombinedCsvOutputDirectory, baseDirectory);
        if (string.Equals(WorkDirectory, CombinedCsvOutputDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CombinedCsvOutputDirectory must be different from WorkDirectory.");
    }

    private static string RequiredFile(string? value, string name, string baseDirectory)
    {
        var path = Resolve(value ?? throw new ArgumentException($"{name} is required."), baseDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException($"{name} not found", path);
        return path;
    }

    private static void ValidateLispFunctionSignature(string lispFilePath, string routineFunction)
    {
        var expression = $@"^[\t ]*\([\t ]*defun[\t ]+{Regex.Escape(routineFunction)}(?=[\t \r\n(])[\t \r\n]+\(([^)]*)\)";
        var match = Regex.Match(File.ReadAllText(lispFilePath), expression, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException($"The function '{routineFunction}', derived from LispFilePath, was not found as a defun in {lispFilePath}.");

        var parameters = match.Groups[1].Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(parameter => parameter != "/")
            .ToArray();
        if (parameters.Length != 1)
            throw new ArgumentException($"The function '{routineFunction}' in {lispFilePath} must accept exactly one argument, but its defun declares {parameters.Length}.");
    }

    private static string Resolve(string value, string baseDirectory) => Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(baseDirectory, value));
}

public sealed record JobResult(string Drawing, string Status, int? ExitCode, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string? LogPath, string? Error);
public sealed record BatchRunResult(
    int ExitCode,
    IReadOnlyList<JobResult> Jobs,
    string? StructuredSummaryPath,
    string? ReadableSummaryPath,
    string? CombinedCsvPath,
    IReadOnlyList<string> Issues,
    bool WasCancellationRequested);
internal readonly record struct CsvFileStamp(long Length, DateTime LastWriteUtc);
