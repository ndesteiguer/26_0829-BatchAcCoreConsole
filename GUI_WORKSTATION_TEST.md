# GUI Workstation Test Checklist

Use this checklist on a standard-user Windows workstation that has the matching x64 .NET Desktop Runtime and a licensed AutoCAD/Core Console installation. Do not run it against production-only drawings.

## 1. Launch and profile setup

1. Build the GUI from this branch:

    dotnet run --project .\BatchAcCore.Gui\BatchAcCore.Gui.csproj

2. Confirm the application starts without elevation.
3. Create a new profile and enter the actual Core Console executable, AutoLISP file, drawing list or input directory, work directory, combined-output directory, worker count, and timeout.
4. Save the profile in a user-writable folder, close/open it, and confirm the fields reload.
5. If applicable, confirm an editable UNC path remains intact after saving and reopening.

Expected: the GUI does not install software, request elevation, change AutoCAD profiles, or change AutoCAD trust/security settings.

## 2. Preflight

1. Run preflight with a deliberately invalid Core Console path.
2. Confirm preflight shows an error and Start batch is disabled.
3. Restore the valid value and run preflight again.
4. Confirm the resolved drawing count and queue match the chosen list/directory; duplicate drawing paths appear once.
5. Confirm any save-after-run and mapped-drive warnings are understandable.

Expected: preflight performs no DWG processing and does not create the work/output folders solely by being run.

## 3. Representative batch

1. Use copies of representative DWGs and an established Core Console-compatible LISP routine.
2. Run the GUI profile.
3. Confirm queue rows change from queued to running and then terminal states.
4. Confirm worker numbers, UTC timestamps, elapsed time, exit code, log path, and error details appear when applicable.
5. Open a retained log, readable batch summary, and combined CSV from the GUI.
6. Compare the result, generated CSVs, exit behavior, and summaries with the stable CLI run using the same profile.

Expected: the GUI and CLI produce equivalent batch artifacts and outcome.

## 4. Cancellation

1. Prepare enough copies of drawings that some jobs remain queued.
2. Start the batch, wait until at least one job is running, then select Cancel queued work.
3. Confirm no newly queued drawing starts after cancellation.
4. Confirm jobs that had already started finish normally.
5. Confirm unstarted jobs become Cancelled; no claim is made that DWG changes were rolled back.

Expected: cancellation never force-terminates a Core Console process.

## 5. Failed-only rerun

1. Produce at least one failed, timed-out, or cancelled job.
2. Select Create failed-only rerun and save the new profile.
3. Confirm the new drawing-list file contains only those jobs.
4. Confirm the original profile and its drawing list are unchanged.
5. Run preflight on the rerun profile before starting it.

## Report back

Please report:

- GUI launch success/failure and .NET Desktop Runtime version;
- preflight behavior and any diagnostic text that seems misleading;
- GUI-versus-CLI result comparison;
- cancellation behavior;
- profile/rerun behavior; and
- any log, summary, or screenshot relevant to a failure.
