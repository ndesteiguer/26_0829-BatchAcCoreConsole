# Batch AcCoreConsole

Windows command-line runner for applying vetted AutoLISP routines or scripts to many DWG files in parallel through `accoreconsole.exe`.

The intended product direction and boundaries for the next development stage are documented in [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md).

## Workflow

1. Put one DWG path per line in `drawings.txt` (blank lines and lines beginning with `#` are ignored), or configure `InputDirectory` to discover DWGs.
2. Keep each routine beside its paired execution-definition JSON. The JSON declares the execution type, routine path, callable LISP function when applicable, and output format when applicable. See [EXECUTION_CONTRACT.md](EXECUTION_CONTRACT.md).
3. Copy `settings.example.json` to `settings.json` and supply the AutoCAD, execution-definition, input, and worker settings. Use **either** `FileListPath` or `InputDirectory`. Add `SharedInputFilePath` only for `lisp-input-report`. New profiles default to AutoCAD 2026 Core Console, four workers, a ten-minute timeout, no drawing save, and per-job logs enabled.
4. Run:

   ```powershell
   dotnet run --project . -- settings.json
   ```

The runner validates `AcCoreConsolePath`, `ExecutionDefinitionPath`, the paired routine, `SharedInputFilePath` when required, and `FileListPath` when used before any drawing is processed. It does not interpret or author scripts or LISP files. Blank and comment rows in `FileListPath` are always ignored. By default, every other entry must point to an existing `.dwg` file and invalid entries are reported before processing starts. Set `SkipInvalidFileListEntries` to `true` to silently skip invalid entries instead. It creates `WorkDirectory` and `ResultsDirectory` when needed.

For framework-dependent Windows deployment, publish a 64-bit CLI application folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The target workstation needs the matching x64 .NET Runtime. AutoCAD/Core Console remains a separate prerequisite.

## Windows GUI

The WPF GUI is the desktop workflow for the same runner. It supports profile editing and saving, explicit preflight, resolved-queue review, live job status, cancellation of queued work, result/log review, and creation of a separate failed-only rerun profile.

Run it from a development checkout:

```powershell
dotnet run --project .\BatchAcCore.Gui\BatchAcCore.Gui.csproj
```

For deployment, publish a framework-dependent x64 application folder:

```powershell
dotnet publish .\BatchAcCore.Gui\BatchAcCore.Gui.csproj -c Release -r win-x64 --self-contained false
```

The workstation needs the matching x64 **.NET Desktop Runtime**, plus a separately installed/licensed AutoCAD Core Console. The GUI does not bundle or install .NET, AutoCAD, or Core Console; it uses user-chosen paths and JSON profile files, requires no administrator permission, and does not modify AutoCAD profiles or security settings. UNC paths remain supported through editable path fields.

Preflight is non-running: it validates the profile and resolves the drawing queue before the batch starts. If cancellation is requested during a batch, the GUI stops starting queued jobs but lets already-running Core Console jobs finish normally. A failed-only rerun writes a new profile and companion drawing list, leaving the source profile untouched.

The agreed product, functional, and architectural scope is recorded in:

- [GUI project outline](GUI_PROJECT_OUTLINE.md)
- [GUI functional specification](GUI_FUNCTIONAL_SPEC.md)
- [GUI architecture plan](GUI_ARCHITECTURE_PLAN.md)
- [GUI workstation test checklist](GUI_WORKSTATION_TEST.md)

## Verification

Build every project:

```powershell
dotnet build .\BatchAcCore.sln
```

Run the dependency-free Core verification harness:

```powershell
dotnet run --project .\BatchAcCore.Tests\BatchAcCore.Tests.csproj
```

It validates preflight and a controlled non-AutoCAD failure path, including job-result fields and summaries. A live GUI run still requires a workstation with the intended AutoCAD/Core Console installation and representative drawings.

## What each worker does

For every drawing, the runner creates a unique temporary `.scr`, launches:

```text
accoreconsole.exe /i "drawing.dwg" /s "unique-job.scr"
```

The generated launcher disables dialogs, dispatches the configured execution type and arguments, optionally saves, writes a plain AutoLISP completion marker, and exits AutoCAD. It deliberately contains no `vl-`, `vla-`, or `vlax-` calls for Core Console compatibility. If the routine errors before it reaches the marker, the runner marks that drawing as failed. If a LISP cannot be loaded—for example, because its folder is untrusted—the runner records `Lisp routine failed to load.` even if Core Console exits with code 0. That batch-wide failure prevents queued drawings from starting; drawings already running finish safely, and output combination is skipped. By default, generated launchers are written to the temporary batch root and deleted after execution. Set `KeepScripts` to `true` only when troubleshooting; retained launchers are then written to `WorkDirectory`.

Each batch creates a temporary root under the Windows temporary directory, for example `%TEMP%\BatchAcCoreConsole-<unique-batch-id>`. Each worker runs Core Console with its isolated profile in a `worker-<n>` subfolder, and the short-lived per-drawing completion markers and default transient scripts are written directly in the temporary root. The entire temporary root is removed after the batch finishes, even when `KeepScripts` is `true`, and remains outside `WorkDirectory` so its files do not mix with retained batch artifacts or ordinary folder synchronization.

Each batch writes a timestamped `summary-*.json` file in `WorkDirectory`. By default, a separate stdout/stderr `.log` is also retained for each job. Set `CreateLogFiles` to `false` in `settings.json` to discard that output after it is drained, avoiding per-job log files and their synchronization events. A batch-wide LISP load failure records queued drawings as `Skipped`. Exit code 1 means a drawing failed, queued work was cancelled, the LISP failed to load, an output count did not match successful DWGs, or output combination failed.

## Results directory and combined routine output

Set `ResultsDirectory` to the folder where completed-batch artifacts should be written. It receives a combined routine output and readable batch summary. For `lisp-result`, `lisp-report`, and `lisp-input-report`, the runner creates one unique `batch-output-*` directory inside `WorkDirectory` and passes that same directory to every routine invocation. The routine owns its filenames and leaves its per-DWG output files there. After the batch ends, the runner counts matching files against successful DWGs, reports the expected-versus-found count, and combines supported formats into `ResultsDirectory`: CSV files become a single header-preserving CSV, and JSON files become one JSON array containing each source document. Other declared formats are reported as found artifacts and remain in the batch-output directory until a dedicated combiner is added. An output-count mismatch or failed supported-format combination causes a nonzero batch result. The directory also receives a timestamped `batch-summary-*.txt` file with every successful, failed, and skipped drawing, output counts, batch-level issues, effective settings, and approximate elapsed runtime for the run.

## Execution definitions and routine arguments

The profile selects the paired execution JSON rather than a raw LISP file or function name. The runner resolves the JSON and generates the Core Console launcher as follows:

- `blind-script`: runs the `.scr` without arguments.
- `blind-lisp`: loads the `.lsp` and calls its configured function without arguments.
- `lisp-result` and `lisp-report`: pass the fresh batch-output directory as the function’s only argument.
- `lisp-input-report`: passes `SharedInputFilePath` first, then the fresh batch-output directory.

The application owns optional saving, the completion marker, and Core Console quit. User routines must not save or quit.

## Operational notes

- Start with `WorkerCount: 1` to validate the LISP routine, then increase gradually. AutoCAD instances consume substantial RAM; 2–4 is usually a sensible starting point.
- The routine must be non-interactive: no selection prompts, dialogs, or input requests. Use full paths for any files it reads/writes.
- Output-producing routines should create one declared-format output file per successful DWG in the supplied batch-output directory. The runner reports a count mismatch but does not inspect the routine’s contents or naming convention.
- Test on copies first. `SaveAfterRun` defaults to `false`; when enabled, the DWG is saved in place after the routine returns.
- A nonzero AcCoreConsole exit code or an elapsed `TimeoutMinutes` marks only that job failed; the other workers continue.
