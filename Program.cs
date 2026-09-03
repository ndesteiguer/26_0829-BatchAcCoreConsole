using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

return await BatchRunner.RunAsync(args);

internal static class BatchRunner
{
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

        var drawings = GetDrawings(settings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (drawings.Length == 0) return Fail("No DWG files were found.");

        Directory.CreateDirectory(settings.WorkDirectory!);
        Console.WriteLine($"Queued {drawings.Length} drawing(s), using {settings.WorkerCount} worker(s).");
        Console.WriteLine($"Work directory: {settings.WorkDirectory}");

        var results = new ConcurrentBag<JobResult>();
        using var semaphore = new SemaphoreSlim(settings.WorkerCount);
        var jobs = drawings.Select(async drawing =>
        {
            await semaphore.WaitAsync();
            try { results.Add(await RunJobAsync(settings, drawing)); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(jobs);

        var ordered = results.OrderBy(r => r.Drawing, StringComparer.OrdinalIgnoreCase).ToArray();
        var summaryPath = Path.Combine(settings.WorkDirectory!, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(ordered, JsonOptions));
        var succeeded = ordered.Count(result => result.Status == "Succeeded");
        Console.WriteLine($"Finished: {succeeded} succeeded, {ordered.Length - succeeded} failed. Summary: {summaryPath}");
        return succeeded == ordered.Length ? 0 : 1;
    }

    private static async Task<JobResult> RunJobAsync(BatchSettings settings, string drawing)
    {
        var jobId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var scriptPath = Path.Combine(settings.WorkDirectory!, $"{jobId}.scr");
        var logPath = Path.Combine(settings.WorkDirectory!, $"{jobId}.log");
        var resultPath = Path.Combine(settings.WorkDirectory!, $"{jobId}.result");
        var started = DateTimeOffset.UtcNow;
        Console.WriteLine($"START {Path.GetFileName(drawing)}");

        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildScript(settings, resultPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = settings.AcCoreConsolePath!,
                    Arguments = $"/i \"{drawing}\" /s \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var completion = process.WaitForExitAsync();
            var exited = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMinutes(settings.TimeoutMinutes))) == completion;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await File.WriteAllTextAsync(logPath, (await stdout) + Environment.NewLine + (await stderr));
                return new(drawing, "TimedOut", null, started, DateTimeOffset.UtcNow, logPath, "Worker exceeded configured timeout.");
            }

            await File.WriteAllTextAsync(logPath, (await stdout) + Environment.NewLine + (await stderr));
            var lispResult = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "No completion marker was written.";
            File.Delete(resultPath);
            var status = process.ExitCode == 0 && lispResult.Trim() == "OK" ? "Succeeded" : "Failed";
            Console.WriteLine($"{status.ToUpperInvariant()} {Path.GetFileName(drawing)} (exit {process.ExitCode})");
            return new(drawing, status, process.ExitCode, started, DateTimeOffset.UtcNow, logPath, status == "Succeeded" ? null : lispResult.Trim());
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(logPath, exception.ToString());
            Console.Error.WriteLine($"FAILED {Path.GetFileName(drawing)}: {exception.Message}");
            return new(drawing, "Failed", null, started, DateTimeOffset.UtcNow, logPath, exception.Message);
        }
        finally
        {
            // A fresh script is made for every run. Retain failures only when explicitly requested.
            if (!settings.KeepScripts && File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
    }

    private static string BuildScript(BatchSettings settings, string resultPath)
    {
        var lispPath = EscapeLispString(settings.LispFilePath!);
        var workDirectory = EscapeLispString(settings.WorkDirectory!);
        var markerPath = EscapeLispString(resultPath);
        var lispExpression = $"({settings.LispFunction} \"{workDirectory}\")";
        var save = settings.SaveAfterRun ? "(command \"_.QSAVE\")\n" : string.Empty;
        // Keep the launcher script compatible with the Core Console subset: no Visual LISP / COM functions.
        // A LISP error prevents execution from reaching the marker, which the runner reports as a failed job.
        return $"(setvar \"FILEDIA\" 0)\n(setvar \"CMDDIA\" 0)\n(load \"{lispPath}\")\n{lispExpression}\n{save}(setq __batchMarker (open \"{markerPath}\" \"w\"))\n(write-line \"OK\" __batchMarker)\n(close __batchMarker)\n(command \"_.QUIT\")\n";
    }

    private static string EscapeLispString(string value) => value.Replace("\\", "/").Replace("\"", "\\\"");

    private static IEnumerable<string> GetDrawings(BatchSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FileListPath))
        {
            foreach (var line in File.ReadLines(settings.FileListPath!))
            {
                var path = line.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(path)) path = Path.Combine(Path.GetDirectoryName(settings.FileListPath!)!, path);
                if (path.Length > 0 && !path.StartsWith('#') && File.Exists(path) && Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetFullPath(path);
            }
        }
        if (!string.IsNullOrWhiteSpace(settings.InputDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(settings.InputDirectory!, "*.dwg", settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                yield return path;
        }
    }

    private static int Fail(string message) { Console.Error.WriteLine($"Error: {message}"); return 2; }
}

internal sealed class BatchSettings
{
    public string? AcCoreConsolePath { get; set; }
    public string? LispFilePath { get; set; }
    public string? LispFunction { get; set; }
    public string? FileListPath { get; set; }
    public string? InputDirectory { get; set; }
    public bool Recursive { get; set; }
    public int WorkerCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int TimeoutMinutes { get; set; } = 30;
    public bool SaveAfterRun { get; set; } = true;
    public bool KeepScripts { get; set; }
    public string? WorkDirectory { get; set; }

    public void Normalize(string baseDirectory)
    {
        AcCoreConsolePath = RequiredFile(AcCoreConsolePath, "AcCoreConsolePath", baseDirectory);
        LispFilePath = RequiredFile(LispFilePath, "LispFilePath", baseDirectory);
        if (string.IsNullOrWhiteSpace(LispFunction) || LispFunction.Any(char.IsWhiteSpace) || LispFunction.IndexOfAny(['(', ')', '"']) >= 0)
            throw new ArgumentException("LispFunction is required and must be an AutoLISP function name, e.g. PROCESSDRAWING or c:MYCOMMAND.");
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
    }

    private static string RequiredFile(string? value, string name, string baseDirectory)
    {
        var path = Resolve(value ?? throw new ArgumentException($"{name} is required."), baseDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException($"{name} not found", path);
        return path;
    }

    private static string Resolve(string value, string baseDirectory) => Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(baseDirectory, value));
}

internal sealed record JobResult(string Drawing, string Status, int? ExitCode, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string LogPath, string? Error);
