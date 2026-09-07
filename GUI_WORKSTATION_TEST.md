# GUI Workstation Test Checklist

Use this checklist on a standard-user Windows workstation that has the matching x64 .NET Desktop Runtime and a licensed AutoCAD/Core Console installation. Do not run it against production-only drawings.

## 1. Launch and profile setup

1. Build the GUI from this branch:

    dotnet run --project .\BatchAcCore.Gui\BatchAcCore.Gui.csproj

2. Confirm the application starts without elevation.
3. Create a new profile and enter the actual Core Console executable, AutoLISP file, drawing list or input directory, work directory, combined-output directory, worker count, and timeout.
4. Confirm **Create per-job log files** is selected for a new profile. Clear it, save the profile, close/open it, and confirm the setting remains cleared.
5. Save the profile in a user-writable folder, close/open it, and confirm the fields reload.
6. If applicable, confirm an editable UNC path remains intact after saving and reopening.

Expected: the GUI does not install software, request elevation, change AutoCAD profiles, or change AutoCAD trust/security settings.

## 2. Button and layout feedback

1. Hover over a toolbar button, then press and release it. Confirm the hover and pressed states are visibly distinct.
2. Use Tab to move through the toolbar. Confirm the focused button has a visible focus outline.
3. Confirm unavailable actions are visibly disabled, including Start Batch before a passing preflight and Cancel Queued Work before a batch starts.
4. Narrow the window until the toolbar wraps to two rows. Confirm the rows have visible vertical spacing and button labels remain readable.
5. Confirm toolbar and tab labels use title case, and the second tab reads **Preflight & Queue**.

## 3. Preflight

1. Run preflight with a deliberately invalid Core Console path.
2. Confirm preflight shows an error and Start batch is disabled.
3. Restore the valid value and run preflight again.
4. Confirm the resolved drawing count and queue match the chosen list/directory; duplicate drawing paths appear once.
5. Confirm any save-after-run and mapped-drive warnings are understandable.
6. With **Save drawings after successful processing** selected, mark a disposable input DWG read-only in Windows. Run preflight and confirm it reports a warning—not an error—explaining that saving may fail. Restore the file attribute after the test.
7. Run preflight from the Profile tab. Confirm the GUI automatically selects the **Preflight & Queue** tab.

Expected: preflight performs no DWG processing and does not create the work/output folders solely by being run.

## 4. Representative batch

1. Use copies of representative DWGs and an established Core Console-compatible LISP routine.
2. Run the GUI profile.
3. Confirm queue rows change from queued to running and then terminal states.
4. Confirm worker numbers, UTC timestamps, elapsed time, exit code, log path, and error details appear when applicable.
5. Open a retained log, readable batch summary, and combined CSV from the GUI.
6. Compare the result, generated CSVs, exit behavior, and summaries with the stable CLI run using the same profile.
7. Repeat with **Create per-job log files** cleared. Confirm the job log path is empty/unavailable and no per-job `.log` files are created, while the batch summary and CSV behavior remain available.

Expected: the GUI and CLI produce equivalent batch artifacts and outcome.

## 5. Cancellation

1. Prepare enough copies of drawings that some jobs remain queued.
2. Start the batch, wait until at least one job is running, then select Cancel queued work.
3. Confirm no newly queued drawing starts after cancellation.
4. Confirm jobs that had already started finish normally.
5. Confirm unstarted jobs become Cancelled; no claim is made that DWG changes were rolled back.

Expected: cancellation never force-terminates a Core Console process.

## 6. Failed-only rerun and stale results

1. Produce at least one failed, timed-out, or cancelled job.
2. Select Create failed-only rerun and save the new profile.
3. Confirm the new drawing-list file contains only those jobs.
4. Confirm the original profile and its drawing list are unchanged.
5. Run preflight on the rerun profile before starting it.
6. Run a batch that has at least one failed, timed-out, or cancelled drawing, but do not create the rerun yet. Attempt to edit a profile field, checkbox, radio button, or use a Browse/Open/Reset Profile action.
7. Confirm the warning explains that failed-only rerun will become unavailable. Select **No** and confirm the profile is unchanged and **Create Failed-Only Rerun** remains available.
8. Repeat and select **Yes**, then make a profile change. Confirm the Run Output tab becomes **Run Output (Previous Run)**, shows the stale-results warning, and **Create Failed-Only Rerun** is disabled.
9. Confirm prior logs, batch summary, and CSV remain available after the profile change.
10. Run a new batch. Confirm the stale-results warning and the "Previous Run" tab label clear when the batch completes.

## Report back

Please report:

- GUI launch success/failure and .NET Desktop Runtime version;
- preflight behavior and any diagnostic text that seems misleading;
- GUI-versus-CLI result comparison;
- cancellation behavior;
- profile/rerun behavior; and
- any log, summary, or screenshot relevant to a failure.
