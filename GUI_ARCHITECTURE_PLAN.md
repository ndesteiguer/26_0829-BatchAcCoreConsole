# Batch AcCoreConsole GUI — Architecture Plan

> **Scope authority:** [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md) controls product scope. This plan replaces prototype-specific LISP signature, CSV, and compatibility assumptions with the target LISP-or-SCR execution model.

## Current state

The current runner is a working CLI baseline with a LISP-specific profile, generated launcher scripts, Core Console process execution, CSV aggregation, and console reporting. The GUI and Core projects provide the basis for moving to the target model, but the current implementation remains a prototype reference rather than a compatibility contract.

## Target solution layout

```text
BatchAcCore.sln
├── BatchAcCore.Core
│   ├── execution-profile and routine-metadata serialization
│   ├── basic validation and drawing discovery
│   ├── launcher generation and Core Console execution
│   ├── optional declared-output collection and combination
│   ├── structured batch/job events and results
│   └── cancellation and process ownership
├── BatchAcCore.Console
│   └── command-line profile and text-output adapter
└── BatchAcCore.Gui
    └── WPF profile, preflight, queue, run, and results interface
```

`BatchAcCore.Core` targets `net10.0`. `BatchAcCore.Gui` targets `net10.0-windows` and sets `UseWPF` to true. The GUI is framework-dependent and no project bundles the .NET runtime.

## Core public contracts

The core accepts an execution definition rather than inferring a routine contract from source or filename. It distinguishes the selected execution type and records only the information needed to invoke it, such as an explicit AutoLISP entry point or a standalone SCR path.

```csharp
public interface IBatchRunner
{
    Task<BatchRunResult> RunAsync(
        ExecutionProfile profile,
        IProgress<BatchEvent>? progress,
        CancellationToken cancellationToken);
}

public interface IBatchPreflight
{
    PreflightReport Check(ExecutionProfile profile);
}
```

`BatchEvent` is a typed hierarchy or equivalent set of events: batch started, job queued, job started, job completed, batch cancelling, batch completed, and diagnostic warning. A `JobResult` retains drawing path, state, worker identifier, exit code, timestamps, log path, retained launcher-script path when applicable, error information, and optional routine-result/output references.

The core returns information; front ends decide how to render it. Console formatting remains a console responsibility, and WPF view models do not invoke process-management code directly.

## Migration policy

- Design the profile, job-result, and output-declaration contracts for the target model.
- Do not retain the prototype's JSON property names, filename-derived LISP entry point, one-argument invocation, CSV-only output, or legacy summary shape merely for backward compatibility.
- Preserve safe operating behavior that remains in scope: bounded concurrency, worker isolation, logging, timeout handling, cancellation semantics, and deterministic results.
- Keep routine metadata lightweight and external. The application does not parse source to discover dependencies, author routine code, or interpret LISP/SCR behavior.

## Execution design

The core compiles the selected execution definition into the appropriate Core Console launcher flow. An AutoLISP run loads the selected file and invokes the explicitly configured entry point. A standalone SCR run executes the selected script directly. Both modes use the same worker isolation, process monitoring, timeout, logging, cancellation, and application-owned status reporting.

An optional, stable, Core Console-compatible routine-result artifact can surface a routine-specific message and declared outputs. This artifact supplements—not replaces—the process result, log, and batch summary. Output combination is attempted only for compatible outputs explicitly declared by the profile or lightweight metadata.

Cancellation stops the scheduler from starting queued jobs but does not terminate active `accoreconsole.exe` processes. Concurrency remains bounded by the configured worker count. Results are accumulated in a thread-safe collection and sorted deterministically before summary/reporting. Progress notifications are emitted after state changes and do not block job execution.

## Preflight design

Validation is deliberately basic:

1. **Profile validation:** execution type, required files and paths, explicit AutoLISP entry point when applicable, input-method choice, worker/timeout range, and work/results distinction.
2. **Run preflight:** resolved input availability, duplicate/invalid rows, directory creation/write probes, declared-output collision risks, path warnings, and a resolved queue.

Preflight does not validate LISP `defun` signatures, parse SCR commands, resolve routine dependencies, or determine whether a routine is semantically correct. The run command repeats mandatory filesystem and execution-setting validation to prevent bypass through the CLI or stale GUI state.

## GUI design boundary

The WPF application has four main views:

1. Execution-profile editor and profile-file controls.
2. Preflight and drawing-queue review.
3. Active-run dashboard with queue grid and job detail/log view.
4. Completion summary, declared outputs, and failed-only rerun action.

WPF view models depend on `BatchAcCore.Core` contracts only. File/folder selection is isolated behind a GUI service, allowing it to be replaced or tested without affecting the runner.

## Test plan

- Unit tests for profile validation, execution-type selection, drawing discovery, launcher generation, output declaration validation, and output-combination compatibility.
- Runner tests using a controllable fake process launcher to cover both execution types, success with no routine output, declared output collection, nonzero exit, missing optional result artifact, timeout, exception, concurrency, and cancellation.
- GUI/CLI parity tests for target-model profiles and application-owned summaries.
- Manual workstation matrix: standard user, local storage, UNC storage, non-default AutoCAD installation, invalid/missing Core Console, inaccessible output directory, one representative vetted LISP batch, and one representative vetted SCR batch.

## Implementation sequence

1. Define the execution-profile, optional routine-metadata, result-artifact, and output-declaration contracts.
2. Refactor the core runner to support explicit AutoLISP entry points and standalone SCR execution while retaining shared safeguards.
3. Implement target-model preflight, structured progress, output collection/combination, and tests.
4. Update the CLI profile adapter and WPF profile/preflight/queue/run/results views.
5. Validate deployment and support diagnostics on representative workstations.
