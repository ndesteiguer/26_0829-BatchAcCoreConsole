# Batch AcCoreConsole GUI — Functional Specification

> **Scope authority:** [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md) supersedes this document if a detail differs. The GUI implements a safe, observable execution workflow for user-vetted routines; it does not author or interpret them.

## 1. Purpose

The application batches externally developed, AcCoreConsole-compatible AutoLISP entry points and standalone Core Console scripts (`.scr`) over DWG drawings. It provides a safe desktop workflow around a shared execution runner while retaining a CLI for automation.

## 2. GUI workflow

### 2.1 Execution profile

The user can create, open, edit, save, duplicate, and validate a JSON execution profile.

Every profile selects:

- `AcCoreConsolePath`
- an execution type: `AutoLisp` or `Script`
- a routine file path (`.lsp` or `.scr`)
- an explicit LISP entry-point function when the execution type is `AutoLisp`
- exactly one input method: `FileListPath` or `InputDirectory`
- work and results directories

Profiles also expose basic execution settings: recursive discovery, invalid-entry handling, worker count, timeout, save-after-run, retained generated launcher scripts, and per-job logs. If the selected routine accepts inputs or declares outputs, the profile records only the minimal values and output metadata necessary to invoke and report it; the GUI does not inspect routine source to derive those values.

### 2.2 Preflight

Preflight is explicit and read-only, except for work/output directory creation after the user elects to run. It reports each check as pass, warning, or failure.

Required checks:

| Check | Outcome when it fails |
|---|---|
| Core Console executable exists and is readable | Cannot run |
| Selected routine file exists, is readable, and matches the selected execution type | Cannot run |
| AutoLISP entry point is supplied for an AutoLISP profile | Cannot run |
| Exactly one input method is configured | Cannot run |
| File-list entries or input-directory discovery produce drawings | Cannot run |
| Required drawings are accessible `.dwg` files | Cannot run unless skip-invalid is enabled |
| Work and results directories are distinct and can be created/written | Cannot run |
| Worker count and timeout are within supported bounds | Cannot run |
| Save-after-run is enabled | Warning: drawings may be changed in place |
| Mapped-drive paths are used | Warning: recommend a UNC path when access differs across processes |

Preflight validates profile and filesystem conditions only. It does not prove that a routine is correct, Core Console-compatible, or safe for a specific drawing.

### 2.3 Queue review

After successful preflight, users can review the resolved drawings before starting. The queue displays the full path, source, and validation state. Duplicate input paths are shown once.

### 2.4 Batch run

The run screen shows a persistent overview and one row per drawing:

- Queue state: queued, running, succeeded, failed, cancelled, or unknown
- Drawing name and full path
- Assigned worker number while running
- Start time, finish time, elapsed time, and process exit code when available
- Error summary and corresponding log
- Optional routine-result message and declared output paths when available

The total succeeded, failed, running, and queued count updates as jobs end. The application always retains its structured summary and configured logs. A routine may create no output. When a profile declares compatible per-drawing outputs, the application combines them and records either the combined artifact or a combination issue.

### 2.5 Rerun support

After a completed run, the user can create a new queue containing only failed, timed-out, or cancelled drawings. The source profile remains unchanged. The run record retains a snapshot of the normalized execution settings used.

## 3. Cancellation policy

Cancelling a batch stops scheduling queued jobs immediately and allows every already-started Core Console job to finish normally. The application does not force-terminate Core Console processes as part of ordinary cancellation.

The summary records the cancellation time and preserves available logs and scripts. Jobs that had not started are marked `cancelled` and are eligible for a later rerun. An active job that exceeds its configured timeout uses normal timeout handling; cancellation does not alter that behavior.

## 4. Error reporting

Error messages identify the failed operation and relevant path, without claiming that the application can bypass permissions or repair workstation policy. Categories include profile/configuration, filesystem access, Core Console startup, process timeout, process exit failure, optional routine-result artifact failure, output combination, and cancellation.

## 5. Security and portability constraints

- Store profiles per user or in a user-chosen folder; do not require registry configuration.
- Do not require administrator privileges, install prerequisites, modify AutoCAD profiles, or alter endpoint policy.
- Framework-dependent deployment requires documented x64 .NET runtime prerequisites; a missing WPF runtime prevents launch before GUI self-diagnosis is possible.
- AutoCAD/Core Console installation and licensing remain external prerequisites.
- Diagnostic export is opt-in and lets the user exclude settings and paths that could be sensitive.

## 6. Implementation invariants

- The GUI and CLI use the same core execution, validation, cancellation, and result-reporting behavior.
- The runner controls launcher generation, Core Console process ownership, and application-level summaries; the GUI does not reimplement them.
- Execution status is application-owned and remains available whether or not a routine produces an output file.
- New profile and result contracts are designed for the target model; compatibility with the prototype's single-LISP, filename-derived, one-argument, CSV-specific contract is not required.

## 7. Acceptance checks

1. A standard Windows user can run the GUI from a writable folder with the required .NET runtime and AutoCAD installed, without elevation.
2. The GUI can select a non-default Core Console location and run both a vetted AutoLISP entry point and a vetted standalone SCR profile.
3. Preflight prevents an invalid profile from starting and explains each failure without analyzing routine source.
4. A valid run provides equivalent execution status and batch artifacts through the GUI and CLI using the same target-model profile.
5. The GUI reports a routine with no output successfully, and combines configured compatible per-drawing outputs when present.
6. A user can inspect a failed drawing's log and create a failed-only rerun queue.
7. Cancellation follows the documented policy and never claims DWG changes were rolled back.
