# Batch AcCoreConsole GUI — Architecture Plan

## Current state

The repository currently has one executable project. `Program.cs` contains argument parsing, JSON loading, settings normalization, drawing discovery, script generation, Core Console process execution, CSV aggregation, and console reporting.

This is a good working CLI baseline, but a GUI cannot safely obtain its queue state or cancellation control from `Console.WriteLine` output. The runner needs structured interfaces before a WPF front end is added.

## Target solution layout

```text
BatchAcCore.sln
├── BatchAcCore.Core
│   ├── BatchSettings and profile serialization
│   ├── validation and drawing discovery
│   ├── script generation and Core Console execution
│   ├── CSV aggregation
│   ├── structured batch/job events and results
│   └── cancellation and process ownership
├── BatchAcCore.Console
│   └── existing command-line argument and text-output adapter
└── BatchAcCore.Gui
    └── WPF profile, preflight, queue, run, and results interface
```

`BatchAcCore.Core` targets `net10.0`. `BatchAcCore.Gui` targets `net10.0-windows` and sets `UseWPF` to true. The GUI is framework-dependent and no project bundles the .NET runtime.

## Core public contracts

The exact type names can change, but the capabilities below should be present.

```csharp
public interface IBatchRunner
{
    Task<BatchRunResult> RunAsync(
        BatchSettings settings,
        IProgress<BatchEvent>? progress,
        CancellationToken cancellationToken);
}

public interface IBatchPreflight
{
    PreflightReport Check(BatchSettings settings);
}
```

`BatchEvent` is a discriminated set of events or an equivalent typed hierarchy: batch started, job queued, job started, job completed, job failed, batch cancelling, batch completed, and diagnostic warning. A `JobResult` retains drawing path, state, worker identifier, exit code, timestamps, log path, script path when retained, and error information.

The core returns information; front ends decide how to render it. Console formatting must remain a console responsibility, and WPF view models must not invoke process-management code directly.

## Compatibility strategy

- Keep the existing JSON property names and defaults for `BatchSettings`.
- Preserve CLI syntax: one settings-file argument, `--help`, exit code `0` for a fully successful batch, `1` for one or more failed jobs, and `2` for configuration/usage failure.
- Preserve generated script content and CSV-combination behavior except for intentional, tested fixes.
- Write a normalized settings snapshot to the run work directory without overwriting a user-owned source profile.
- Maintain existing summary fields, adding fields only in a backward-compatible way.

## Execution design

The core owns a per-run registry of `Process` instances it started. This is required so cancellation can act only on child Core Console processes belonging to the active batch. It does not search for or terminate unrelated `accoreconsole.exe` processes.

Concurrency remains bounded by `WorkerCount`. Results are accumulated in a thread-safe collection and sorted deterministically before writing the summary and reporting final results. Progress notifications are emitted after state changes and must not block job execution.

## Preflight design

Validation is split into two levels:

1. **Profile validation:** schema/range checks, required paths, LISP function signature, input-method choice, and work/output distinction.
2. **Run preflight:** resolved input availability, duplicate/invalid rows, directory creation/write probes, output collision risks, path warnings, and a resolved queue.

The run command repeats mandatory validation to prevent bypass through the CLI or stale GUI state. GUI preflight is advisory for review; it is not the only enforcement point.

## GUI design boundary

The initial WPF application has four main views:

1. Profile editor and profile-file controls.
2. Preflight and drawing-queue review.
3. Active-run dashboard with queue grid and job detail/log view.
4. Completion summary and failed-only rerun action.

WPF view models depend on `BatchAcCore.Core` contracts only. File/folder selection is isolated behind a GUI service, allowing it to be replaced or tested without affecting the runner.

## Test plan before GUI work

- Unit tests for profile validation, LISP signature validation, drawing discovery, AutoLISP path escaping, CSV-header validation, and script generation.
- Runner tests using a controllable fake process launcher to cover success, nonzero exit, missing completion marker, timeout, exception, concurrency, and cancellation states.
- CLI compatibility tests for old valid profiles, error codes, and summary output.
- Manual workstation matrix: standard user, local storage, UNC storage, non-default AutoCAD installation, invalid/missing Core Console, inaccessible output directory, and one successful representative LISP batch.

## Implementation sequence

1. Add a solution and Core project; move logic without behavior changes and cover it with tests.
2. Introduce typed validation/preflight and structured progress while retaining CLI text output.
3. Introduce cancellation/process ownership with documented semantics and tests.
4. Add the WPF project and implement profile/preflight/queue/run/results views.
5. Validate deployment and support diagnostics on representative workstations.
