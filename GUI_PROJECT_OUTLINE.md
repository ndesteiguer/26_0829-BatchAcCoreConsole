# Batch AcCoreConsole GUI — Project Outline

## Product purpose

Provide a focused Windows desktop tool for batch processing and data extraction of DWG files using AutoCAD Core Console (`accoreconsole.exe`) and non-interactive, AcCoreConsole-compatible AutoLISP routines.

The product is an orchestration and diagnostics tool. It is not an AutoCAD editor, a general AutoCAD automation authoring environment, or a replacement for interactive AutoCAD workflows.

## Core user workflow

1. Select or load a saved profile.
2. Choose a Core Console executable, a LISP routine, and either a drawing list or input directory.
3. Run preflight checks before any drawings are changed.
4. Review the discovered drawing queue.
5. Run jobs in parallel with clear per-drawing status and retained logs.
6. Review generated data files, combined CSV results, failures, and summary information.
7. Re-run failed drawings when needed.

## Functional priorities

- Batch execution of a compatible LISP routine against many DWGs.
- Data extraction and collection, including per-drawing CSV output and combined CSVs.
- Input review, validation, and duplicate/missing-file reporting.
- Live job-level queue status: queued, running, succeeded, failed, cancelled, or unknown.
- Per-drawing logs, errors, elapsed time, and re-run support from `summary.json`.
- Saved portable JSON profiles and run-specific settings snapshots.
- Read-only preflight diagnostics and exportable support information.

## Compatibility and deployment baseline

- Windows-only; AutoCAD Core Console already defines this platform constraint.
- Framework-dependent deployment: do not bundle .NET.
- GUI requires the matching x64 .NET Desktop Runtime; CLI requires the matching x64 .NET Runtime.
- AutoCAD/Core Console remains an external, user-selected dependency. The tool neither installs nor bundles it.
- Support differing AutoCAD versions and installation paths by detecting likely candidates and permitting explicit path selection.
- Preserve the existing CLI and JSON configuration behavior for scripts and scheduled work.

## Security and workstation constraints

- Per-user configuration; no administrator rights required.
- No registry edits, AutoCAD profile edits, permanent environment-variable changes, or security-policy workarounds.
- Validate executable availability, input readability, output/work-folder writability, temporary-directory access, and Core Console launchability.
- Prefer and clearly support UNC paths for shared storage; diagnose mapped-drive accessibility issues.
- Explain failures precisely rather than masking them behind generic batch errors.

## Recommended architecture

```text
BatchAcCore.Core       shared execution, validation, progress, cancellation
BatchAcCore.Console    existing command-line interface
BatchAcCore.Gui        WPF desktop interface
```

The core exposes structured progress, results, validation diagnostics, and controlled cancellation. The console and GUI use the same core behavior.

## Explicit non-goals

- Editing DWGs interactively.
- Making interactive or incompatible LISP routines safe for unattended Core Console use.
- Installing the .NET runtime, AutoCAD, or licensing components.
- Elevating privileges or attempting to bypass endpoint/security controls.

## Open implementation decisions

- Cancellation behavior for running Core Console processes and any partially modified DWGs.
- Exact profile storage defaults and rules for relative versus absolute paths.
- Scope of Core Console test-launch preflight and its non-destructive test artifact.
- Minimum supported Windows and AutoCAD versions.
- Code signing, distribution channel, and IT deployment documentation.
