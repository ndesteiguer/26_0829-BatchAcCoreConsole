using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

return await BatchRunner.RunAsync(args);

internal static class BatchRunner
{
    private const string LispLoadFailure = "Lisp routine failed to load.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: BatchAcCoreConsole <settings.json>");
            Console.WriteLine("Copy settings.example.json, set the paths and run this command.");
            return args.Length == 1 ? 0 : 2;
        }

        var settingsFile = Path.GetFullPath(args[0]);
        if (!File.Exists(settingsFile)) return Fail($"Settings file not found: {settingsFile}");

        BatchSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<BatchSettings>(await File.ReadAllTextAsync(settingsFile), JsonOptions);
        }
        catch (JsonException exception)
        {
            return Fail($"Invalid JSON: {exception.Message}");
        }

        if (settings is null) return Fail("Settings file is empty.");
        var baseDirectory = Path.GetDirectoryName(settingsFile)!;
        try { settings.Normalize(baseDirectory); }
        catch (Exception exception) { return Fail(exception.Message); }

        string[] drawings;
        try
        {
            drawings = GetDrawings(settings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (ArgumentException exception)
        {
            return Fail(exception.Message);
        }
        if (drawings.Length == 0) return Fail("No DWG files were found.");
        var expectedCsvFiles = GetExpectedCsvFiles(settings.WorkDirectory!, drawings, settings.RoutineFunction);
        if (expectedCsvFiles.Count != drawings.Length)
            return Fail("Each drawing must have a unique filename because CSV output names are derived from the drawing filename and LISP function name.");

        Directory.CreateDirectory(settings.WorkDirectory!);
        var isolateRoot = Path.Combine(Path.GetTempPath(), $"BatchAcCoreConsole-{Guid.NewGuid():N}");
        var csvFilesBeforeBatch = SnapshotCsvFiles(settings.WorkDirectory!);
        Console.WriteLine($"Queued {drawings.Length} drawing(s), using {settings.WorkerCount} worker(s).");
        Console.WriteLine($"Work directory: {settings.WorkDirectory}");

        var results = new ConcurrentBag<JobResult>();
        using var semaphore = new SemaphoreSlim(settings.WorkerCount);
        var workerSlots = new ConcurrentBag<int>(Enumerable.Range(1, settings.WorkerCount));
        var lispLoadFailureDetected = 0;
        var jobs = drawings.Select(async drawing =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (Volatile.Read(ref lispLoadFailureDetected) != 0)
                {
                    var skippedAt = DateTimeOffset.UtcNow;
                    const string reason = "Skipped because the LISP routine failed to load in another worker.";
                    results.Add(new(drawing, "Skipped", null, skippedAt, skippedAt, null, reason));
                    Console.WriteLine($"SKIPPED {Path.GetFileName(drawing)} ({reason})");
                    return;
                }

                if (!workerSlots.TryTake(out var workerId))
                    throw new InvalidOperationException("A worker slot was unavailable.");
                try
                {
                    var result = await RunJobAsync(settings, drawing, isolateRoot, workerId);
                    if (string.Equals(result.Error, LispLoadFailure, StringComparison.Ordinal))
                        Interlocked.Exchange(ref lispLoadFailureDetected, 1);
                    results.Add(result);
                }
                finally
                {
                    workerSlots.Add(workerId);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(jobs);
        if (!TryDeleteDirectory(isolateRoot))
            Console.Error.WriteLine($"Could not remove temporary Core Console profile data: {isolateRoot}");

        var ordered = results.OrderBy(r => r.Drawing, StringComparer.OrdinalIgnoreCase).ToArray();
        var succeeded = ordered.Count(result => result.Status == "Succeeded");
        var skipped = ordered.Count(result => result.Status == "Skipped");
        var failed = ordered.Length - succeeded - skipped;
        string? combinedCsvPath = null;
        string? combinationError = null;
        var issues = new List<string>();
        if (Volatile.Read(ref lispLoadFailureDetected) != 0)
        {
            const string issue = "CSV combination skipped because the LISP routine failed to load.";
            issues.Add(issue);
            Console.Error.WriteLine(issue);
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
                Console.Error.WriteLine(combinationError);
            }

            try
            {
                combinedCsvPath = await CombineCsvFilesAsync(settings.CombinedCsvOutputDirectory!, batchCsvFiles);
                Console.WriteLine($"Combined CSV: {combinedCsvPath}");
            }
            catch (Exception exception)
            {
                combinationError = combinationError is null ? exception.Message : $"{combinationError}{Environment.NewLine}{exception.Message}";
                var issue = $"CSV combination failed: {exception.Message}";
                issues.Add(issue);
                Console.Error.WriteLine(issue);
            }
        }

        var summaryPath = Path.Combine(settings.WorkDirectory!, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(ordered, JsonOptions));
        var readableSummaryPath = Path.Combine(settings.CombinedCsvOutputDirectory!, $"batch-summary-{DateTime.UtcNow:yyyyMMddHHmmssfff}.txt");
        try
        {
            Directory.CreateDirectory(settings.CombinedCsvOutputDirectory!);
            await File.WriteAllTextAsync(readableSummaryPath, BuildReadableSummary(settings, ordered, succeeded, failed, skipped, combinedCsvPath, issues, summaryPath));
            Console.WriteLine($"Batch summary: {readableSummaryPath}");
        }
        catch (Exception exception)
        {
            combinationError = combinationError is null ? exception.Message : $"{combinationError}{Environment.NewLine}{exception.Message}";
            Console.Error.WriteLine($"Could not write batch summary: {exception.Message}");
        }
        Console.WriteLine($"Finished: {succeeded} succeeded, {failed} failed, {skipped} skipped. Summary: {summaryPath}");
        return succeeded == ordered.Length && combinationError is null ? 0 : 1;
    }

    private static async Task<JobResult> RunJobAsync(BatchSettings settings, string drawing, string isolateRoot, int workerId)
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
        Console.WriteLine($"START {Path.GetFileName(drawing)}");

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
            var output = CaptureProcessOutputAsync(process, settings.CreateLogFiles);
            var completion = process.WaitForExitAsync();
            var exited = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMinutes(settings.TimeoutMinutes))) == completion;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var outputText = await output;
                if (outputText is not null) await File.WriteAllTextAsync(logPath!, outputText);
                return new(drawing, "TimedOut", null, started, DateTimeOffset.UtcNow, logPath, "Worker exceeded configured timeout.");
            }

            var completedOutput = await output;
            if (completedOutput is not null) await File.WriteAllTextAsync(logPath!, completedOutput);
            var lispResult = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "No completion marker was written.";
            File.Delete(resultPath);
            var status = process.ExitCode == 0 && lispResult.Trim() == "OK" ? "Succeeded" : "Failed";
            Console.WriteLine($"{status.ToUpperInvariant()} {Path.GetFileName(drawing)} (exit {process.ExitCode})");
            return new(drawing, status, process.ExitCode, started, DateTimeOffset.UtcNow, logPath, status == "Succeeded" ? null : lispResult.Trim());
        }
        catch (Exception exception)
        {
            if (logPath is not null) await File.WriteAllTextAsync(logPath, exception.ToString());
            Console.Error.WriteLine($"FAILED {Path.GetFileName(drawing)}: {exception.Message}");
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

    private static IReadOnlyList<string> GetExpectedCsvFiles(string workDirectory, IReadOnlyList<string> drawings, string routineFunction) => drawings
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

    private static IEnumerable<string> GetDrawings(BatchSettings settings)
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
        string? combinedCsvPath,
        IReadOnlyList<string> issues,
        string jsonSummaryPath)
    {
        var summary = new StringBuilder();
        summary.AppendLine("Batch AcCoreConsole summary");
        summary.AppendLine(new string('=', 26));
        summary.AppendLine($"Completed (UTC): {DateTimeOffset.UtcNow:O}");
        summary.AppendLine($"Results: {succeeded} succeeded, {failed} failed, {skipped} skipped");
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

    private static int Fail(string message) { Console.Error.WriteLine($"Error: {message}"); return 2; }
}

internal sealed class BatchSettings
{
    public string? AcCoreConsolePath { get; set; }
    public string? LispFilePath { get; set; }
    internal string RoutineFunction { get; private set; } = string.Empty;
    public string? FileListPath { get; set; }
    public bool SkipInvalidFileListEntries { get; set; }
    public string? InputDirectory { get; set; }
    public bool Recursive { get; set; }
    public int WorkerCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int TimeoutMinutes { get; set; } = 30;
    public bool SaveAfterRun { get; set; } = true;
    public bool KeepScripts { get; set; }
    public bool CreateLogFiles { get; set; } = true;
    public string? WorkDirectory { get; set; }
    public string? CombinedCsvOutputDirectory { get; set; }

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

internal sealed record JobResult(string Drawing, string Status, int? ExitCode, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string? LogPath, string? Error);
internal readonly record struct CsvFileStamp(long Length, DateTime LastWriteUtc);
