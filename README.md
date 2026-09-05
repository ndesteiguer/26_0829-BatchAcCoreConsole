# Batch AcCoreConsole

Windows command-line runner for applying an AutoLISP routine to many DWG files in parallel through `accoreconsole.exe`.

## Workflow

1. Put one DWG path per line in `drawings.txt` (blank lines and lines beginning with `#` are ignored), or configure `InputDirectory` to discover DWGs.
2. Make the LISP routine callable without UI. Its filename and exported one-argument function must match—for example, `REFREPORTCSV.lsp` must define `REFREPORTCSV`.
3. Copy `settings.example.json` to `settings.json` and supply the AutoCAD, LISP, input, and worker settings. Use **either** `FileListPath` or `InputDirectory`.
4. Run:

   ```powershell
   dotnet run --project . -- settings.json
   ```

The runner validates `AcCoreConsolePath`, `LispFilePath`, and `FileListPath` (when used) as existing files before any drawing is processed. `LispFilePath` must point to a `.lsp` file whose filename (without `.lsp`) is the function to run. The runner verifies that the file contains a standard `defun` for that function with exactly one argument (local variables after `/` are ignored). Blank and comment rows in `FileListPath` are always ignored. By default, every other entry must point to an existing `.dwg` file and invalid entries are reported before processing starts. Set `SkipInvalidFileListEntries` to `true` to silently skip invalid entries instead. It creates `WorkDirectory` and `CombinedCsvOutputDirectory` when needed.

For deployment, publish a single executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## What each worker does

For every drawing, the runner creates a unique temporary `.scr`, launches:

```text
accoreconsole.exe /i "drawing.dwg" /s "unique-job.scr"
```

The script disables dialogs, loads your `.lsp`, evaluates the function derived from its filename with `WorkDirectory` as its one argument, optionally saves, writes a plain AutoLISP completion marker, and exits AutoCAD. It deliberately contains no `vl-`, `vla-`, or `vlax-` calls for Core Console compatibility. If the routine errors before it reaches the marker, the runner marks that drawing as failed. If the LISP cannot be loaded—for example, because its folder is untrusted—the runner records `Lisp routine failed to load.` even if Core Console exits with code 0. That batch-wide failure prevents queued drawings from starting; drawings already running finish safely, and CSV combination is skipped. By default, scripts are written to the temporary batch root and deleted after execution. Set `KeepScripts` to `true` only when troubleshooting; retained scripts are then written to `WorkDirectory`.

Each batch creates a temporary root under the Windows temporary directory, for example `%TEMP%\BatchAcCoreConsole-<unique-batch-id>`. Each worker runs Core Console with its isolated profile in a `worker-<n>` subfolder, and the short-lived per-drawing completion markers and default transient scripts are written directly in the temporary root. The entire temporary root is removed after the batch finishes, even when `KeepScripts` is `true`, and remains outside `WorkDirectory` so its files do not mix with retained batch artifacts or ordinary folder synchronization.

`summary.json` is retained in `WorkDirectory`. By default, a separate stdout/stderr `.log` is also retained for each drawing. Set `CreateLogFiles` to `false` in `settings.json` to discard that output after it is drained, avoiding per-drawing log files and their synchronization events. A batch-wide LISP load failure records queued drawings as `Skipped`. Exit code 1 means one or more drawings failed or were skipped; use `summary.json` to re-run just those files.

## Combined CSV output

Set `CombinedCsvOutputDirectory` to the folder where completed-batch CSVs should be written. Every routine must write one CSV per drawing using `<drawing name without extension>.<function name>.csv`, where the function name is derived from `LispFilePath`. After the batch ends, the runner combines each expected CSV that was created or updated in `WorkDirectory` into a timestamped `combined-*.csv` containing one header row and every data row. It reports missing expected CSVs but still writes the combined output from those found. The combined CSV is not created when no expected CSV files are found or the source CSV headers differ. The directory also receives a timestamped `batch-summary-*.txt` file with every successful, failed, and skipped drawing, batch-level issues, and the effective settings used for the run.

## AutoLISP output directory

The function name comes from `LispFilePath`; it is not configured separately. It must accept one string argument: the output directory. The runner constructs the AutoLISP expression, escaping and converting the resolved `WorkDirectory` to forward slashes. For this configuration:

```json
"LispFilePath": "C:\\BatchJobs\\PROCESSDRAWING.lsp",
"WorkDirectory": "C:\\BatchJobs\\work"
```

the generated script evaluates:

```lisp
(PROCESSDRAWING "C:/BatchJobs/work")
```

## Operational notes

- Start with `WorkerCount: 1` to validate the LISP routine, then increase gradually. AutoCAD instances consume substantial RAM; 2–4 is usually a sensible starting point.
- The routine must be non-interactive: no selection prompts, dialogs, or input requests. Use full paths for any files it reads/writes.
- Each input drawing must have a unique filename, even if drawings are in different directories, because the CSV name is based on that filename.
- Test on copies first. With `SaveAfterRun: true`, the DWG is saved in place after the routine returns.
- A nonzero AcCoreConsole exit code or an elapsed `TimeoutMinutes` marks only that job failed; the other workers continue.
