# Batch AcCoreConsole

Windows command-line runner for applying an AutoLISP routine to many DWG files in parallel through `accoreconsole.exe`.

## Workflow

1. Put one DWG path per line in `drawings.txt` (blank lines and lines beginning with `#` are ignored), or configure `InputDirectory` to discover DWGs.
2. Make the LISP routine callable without UI. Configure its function name as `LispFunction`, for example `PROCESSDRAWING`.
3. Copy `settings.example.json` to `settings.json` and supply the AutoCAD, LISP, input, and worker settings. Use **either** `FileListPath` or `InputDirectory`.
4. Run:

   ```powershell
   dotnet run --project . -- settings.json
   ```

The runner validates `AcCoreConsolePath`, `LispFilePath`, and `FileListPath` (when used) as existing files before any drawing is processed. It also verifies that `LispFilePath` contains a standard `defun` for `LispFunction` with exactly one argument (local variables after `/` are ignored). Blank and comment rows in `FileListPath` are always ignored. By default, every other entry must point to an existing `.dwg` file and invalid entries are reported before processing starts. Set `SkipInvalidFileListEntries` to `true` to silently skip invalid entries instead. It creates `WorkDirectory` and `CombinedCsvOutputDirectory` when needed.

For deployment, publish a single executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## What each worker does

For every drawing, the runner creates a unique temporary `.scr`, launches:

```text
accoreconsole.exe /i "drawing.dwg" /s "unique-job.scr"
```

The script disables dialogs, loads your `.lsp`, evaluates a `LispExpression` built from `LispFunction` and `WorkDirectory`, optionally saves, writes a plain AutoLISP completion marker, and exits AutoCAD. It deliberately contains no `vl-`, `vla-`, or `vlax-` calls for Core Console compatibility. If the routine errors before it reaches the marker, the runner marks that drawing as failed. Scripts are deleted after execution by default, so they cannot be re-used accidentally. Set `KeepScripts` to `true` only when troubleshooting.

Each worker runs Core Console with its own temporary isolated profile under the Windows temporary directory. The profiles and short-lived per-drawing completion markers are removed after the batch finishes and are kept outside `WorkDirectory` so they do not mix with retained batch artifacts or ordinary folder synchronization.

`summary.json` and a separate stdout/stderr `.log` for each drawing are retained in `WorkDirectory`. Exit code 1 means one or more drawings failed; use `summary.json` to re-run just those files.

## Combined CSV output

Set `CombinedCsvOutputDirectory` to the folder where completed-batch CSVs should be written. After the batch ends, the runner combines every CSV created or updated in `WorkDirectory` into a timestamped `combined-*.csv` containing one header row and every data row. It reports a missing or extra CSV-file count but still writes the combined output from every CSV found. For the bundled XREF routine, it lists missing `<drawing name>.xrefs.csv` files without listing all files that were found. The combined CSV is not created when no CSV files are found or the source CSV headers differ.

## AutoLISP output directory

Set `LispFunction` to the name of a function that accepts one string argument: the output directory. The runner constructs the AutoLISP expression, escaping and converting the resolved `WorkDirectory` to forward slashes. For this configuration:

```json
"LispFunction": "PROCESSDRAWING",
"WorkDirectory": "C:\\BatchJobs\\work"
```

the generated script evaluates:

```lisp
(PROCESSDRAWING "C:/BatchJobs/work")
```

## Operational notes

- Start with `WorkerCount: 1` to validate the LISP routine, then increase gradually. AutoCAD instances consume substantial RAM; 2–4 is usually a sensible starting point.
- The routine must be non-interactive: no selection prompts, dialogs, or input requests. Use full paths for any files it reads/writes.
- Test on copies first. With `SaveAfterRun: true`, the DWG is saved in place after the routine returns.
- A nonzero AcCoreConsole exit code or an elapsed `TimeoutMinutes` marks only that job failed; the other workers continue.
