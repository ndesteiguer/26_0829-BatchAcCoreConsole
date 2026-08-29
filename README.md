# Batch AcCoreConsole

Windows command-line runner for applying an AutoLISP routine to many DWG files in parallel through `accoreconsole.exe`.

## Workflow

1. Put one DWG path per line in `drawings.txt` (blank lines and lines beginning with `#` are ignored), or configure `InputDirectory` to discover DWGs.
2. Make the LISP routine callable without UI. For a command named `PROCESSDRAWING`, use the expression `(c:PROCESSDRAWING)`.
3. Copy `settings.example.json` to `settings.json` and supply the AutoCAD, LISP, input, and worker settings. Use **either** `FileListPath` or `InputDirectory`.
4. Run:

   ```powershell
   dotnet run --project . -- settings.json
   ```

For deployment, publish a single executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## What each worker does

For every drawing, the runner creates a unique temporary `.scr`, launches:

```text
accoreconsole.exe /i "drawing.dwg" /s "unique-job.scr"
```

The script disables dialogs, loads your `.lsp`, evaluates `LispExpression`, optionally saves, and exits AutoCAD. Scripts are deleted after execution by default, so they cannot be re-used accidentally. Set `KeepScripts` to `true` only when troubleshooting.

`summary.json` and a separate stdout/stderr `.log` for each drawing are retained in `WorkDirectory`. Exit code 1 means one or more drawings failed; use `summary.json` to re-run just those files.

## Operational notes

- Start with `WorkerCount: 1` to validate the LISP routine, then increase gradually. AutoCAD instances consume substantial RAM; 2–4 is usually a sensible starting point.
- The routine must be non-interactive: no selection prompts, dialogs, or input requests. Use full paths for any files it reads/writes.
- Test on copies first. With `SaveAfterRun: true`, the DWG is saved in place after the routine returns.
- A nonzero AcCoreConsole exit code or an elapsed `TimeoutMinutes` marks only that job failed; the other workers continue.
