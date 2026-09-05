# Batch AcCoreConsole GUI — Functional Specification

## 1. Purpose

The application batches AcCoreConsole-compatible AutoLISP processing over DWG drawings. It provides a safe, inspectable desktop workflow around the existing runner while retaining the command-line interface for automation.

## 2. Minimum viable GUI

### 2.1 Profile setup

The user can create, open, edit, save, duplicate, and validate a JSON batch profile.

Required profile choices:

- `AcCoreConsolePath`
- `LispFilePath`
- `LispFunction`
- exactly one input method: `FileListPath` or `InputDirectory`
- `WorkDirectory`
- `CombinedCsvOutputDirectory`

The GUI also exposes the existing optional execution settings: recursive discovery, invalid-entry handling, worker count, timeout, save-after-run, and retained scripts. File and folder selectors are convenience controls; paths remain editable text values so UNC paths and unusual installations are supported.

### 2.2 Preflight

Preflight is explicit and read-only except for any already-supported creation of work/output directories after the user elects to run. It reports each check as pass, warning, or failure.

Required checks:

| Check | Outcome when it fails |
|---|---|
| Core Console executable exists and is readable | Cannot run |
| LISP file exists, is readable, and contains the configured one-argument `defun` | Cannot run |
| Exactly one input method is configured | Cannot run |
| File-list entries or input-directory discovery produce drawings | Cannot run |
| Each required drawing is accessible and is a `.dwg` file | Cannot run unless skip-invalid is enabled |
| Work and combined-output directories are distinct and can be created/written | Cannot run |
| Worker count and timeout are within supported bounds | Cannot run |
| `SaveAfterRun` is enabled | Warning: drawings may be changed in place |
| Mapped-drive paths are used | Warning: recommend a UNC path when access differs across processes |

The GUI may offer a non-destructive Core Console test launch in a later release. It must never be treated as proof that a specific LISP routine will work.

### 2.3 Queue review

After a successful preflight, users can review the resolved drawings before starting. The queue displays full path, source (list/directory), and validation state. Duplicate paths are shown once, matching current runner behavior.

### 2.4 Batch run

The run screen shows a persistent overview and one row per drawing:

- Queue state: queued, running, succeeded, failed, cancelled, or unknown
- Drawing name and full path
- Assigned worker number while running
- Start time, finish time, elapsed time
- Process exit code, when available
- Error summary and a link to the corresponding log

The total succeeded, failed, running, and queued count updates as jobs end. Generated `summary.json`, logs, scripts retained for troubleshooting, per-drawing CSVs, and combined CSV path remain accessible after completion.

### 2.5 Rerun support

After a completed run, the user can create a new queue containing only failed or cancelled drawings. The source profile remains unchanged. The run record retains a snapshot of the actual normalized settings used.

## 3. Cancellation policy

Cancelling a batch stops scheduling queued jobs immediately and allows every already-started Core Console job to finish normally. The application does not force-terminate Core Console processes as part of ordinary cancellation.

This policy preserves the runner's normal per-drawing completion, logging, and save behavior and avoids representing a forcibly interrupted DWG as a known safe or known failed state. A cancelled batch therefore has two result groups:

1. Jobs that had already started, which retain their normal succeeded or failed result after completion.
2. Jobs that had not started, which are marked `cancelled` and are eligible for a later rerun.

The summary records the cancellation time and preserves logs and scripts when available. If an active job exceeds its configured timeout, the existing timeout handling determines its result; cancellation does not alter that behavior.

## 4. Error reporting

Error messages must identify the failed operation and the relevant path, without implying that the application can bypass permissions or repair workstation policy. Categories include configuration, filesystem access, Core Console startup, process timeout, process exit failure, LISP completion-marker failure, CSV combination, and cancellation.

## 5. Security and portability constraints

- Store profiles per user or in a user-chosen folder; do not require registry configuration.
- Do not require administrator privileges, install prerequisites, modify AutoCAD profiles, or alter endpoint policy.
- Framework-dependent deployment requires documented x64 .NET runtime prerequisites; this is outside GUI self-diagnosis because a missing WPF runtime prevents launch.
- AutoCAD/Core Console installation and licensing remain external prerequisites.
- Diagnostic export is opt-in and must let the user exclude settings and paths that could be sensitive.

## 6. Compatibility invariants

- The existing console executable, JSON settings fields, validation rules, output conventions, and exit-code semantics continue to work.
- The GUI uses the same runner behavior rather than reimplementing Core Console script generation or CSV combination.
- A profile saved by the GUI is usable by the CLI when it contains the established settings schema.

## 7. Acceptance checks for the first release

1. A standard Windows user can run the GUI from a writable folder with the required .NET runtime and AutoCAD installed, without elevation.
2. The GUI can select a non-default Core Console location, save it in a profile, and use it for a batch.
3. Preflight prevents an invalid batch from starting and explains each failure.
4. A valid profile run produces the same batch artifacts and success/failure outcome as the CLI using that profile.
5. A user can inspect a failed drawing's log and create a failed-only rerun queue.
6. Cancellation behaves exactly as the documented policy and never claims that DWG changes were rolled back.
