# Batch AcCoreConsole GUI — Product Outline

> **Scope authority:** [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md) defines the product goals, boundaries, and deferred work. This outline describes the GUI direction within that scope; it does not preserve constraints from the current prototype.

## Product purpose

Provide a focused Windows desktop workflow for applying externally developed, user-vetted AutoLISP entry points or standalone Core Console scripts (`.scr`) to large sets of DWG files through `accoreconsole.exe`.

The product is an orchestration, safety, and reporting tool. It is not an AutoCAD editor, a LISP/SCR authoring environment, an interpreter, or a replacement for interactive AutoCAD workflows.

## Core user workflow

1. Select or load a saved execution profile.
2. Choose Core Console, an execution type (AutoLISP or SCR), its externally authored routine, and a drawing list or input directory.
3. Supply the explicit AutoLISP entry point when applicable and any minimal execution settings the profile requires.
4. Run basic preflight checks and review the resolved drawing queue.
5. Run jobs in parallel with clear per-drawing status and retained logs.
6. Review execution results, optional routine-declared output files and combinations, failures, and summaries.
7. Re-run failed, timed-out, or cancelled drawings when needed.

## Functional priorities

- Execute vetted AutoLISP entry points and standalone SCR files against many DWGs.
- Bounded parallel Core Console workers with isolated worker state.
- Input review, basic validation, duplicate/missing-file reporting, and practical save/path warnings.
- Live job-level queue status: queued, running, succeeded, failed, cancelled, or unknown.
- Per-drawing logs, errors, elapsed time, and failed-only reruns.
- Saved execution profiles and run-specific settings snapshots.
- Routine-independent execution reporting, with optional combination of compatible per-drawing outputs.
- Lightweight external routine metadata when needed; no source parsing or authoring features.

## Compatibility and deployment baseline

- Windows-only; AutoCAD Core Console defines this platform constraint.
- Framework-dependent deployment: do not bundle .NET.
- GUI requires the matching x64 .NET Desktop Runtime; CLI requires the matching x64 .NET Runtime.
- AutoCAD/Core Console remains an external, user-selected dependency. The tool neither installs nor bundles it.
- Support differing AutoCAD versions and installation paths by detecting likely candidates and permitting explicit path selection.
- The CLI and GUI should use the same shared execution engine, but backward compatibility with the prototype profile schema, output conventions, or legacy routine rules is not a requirement.

## Security and workstation constraints

- Per-user configuration; no administrator rights required.
- No registry edits, AutoCAD profile edits, permanent environment-variable changes, or security-policy workarounds.
- Validate executable availability, selected routine availability, input readability, output/work-folder writability, temporary-directory access, and supported execution settings.
- Prefer and clearly support UNC paths for shared storage; diagnose mapped-drive accessibility issues.
- Explain failures precisely rather than masking them behind generic batch errors.

## Recommended architecture

```text
BatchAcCore.Core       execution-definition validation, queueing, Core Console execution, output collection
BatchAcCore.Console    command-line profile and text-output adapter
BatchAcCore.Gui        WPF profile, preflight, queue, run, and results interface
```

The core exposes structured validation, progress, job results, and controlled cancellation. The console and GUI use the same core behavior.

## Explicit non-goals

- Editing DWGs interactively.
- Authoring, debugging, interpreting, or comprehensively analyzing LISP or SCR files.
- Building a script-pipeline designer, LISP/SCR IDE, or general automation environment.
- Making interactive or incompatible routines safe for unattended Core Console use.
- Installing the .NET runtime, AutoCAD, or licensing components.
- Elevating privileges or attempting to bypass endpoint/security controls.
- Deep routine-dependency discovery, package distribution, trust/signing, or a single-DWG test harness in this stage.

## Open implementation decisions

- Exact execution-profile schema for AutoLISP entry points, standalone SCR files, optional parameters, and optional output declarations.
- Stable Core Console-compatible routine-result artifact format.
- Rules for combining compatible output files beyond CSV.
- Cancellation behavior for running Core Console processes and any partially modified DWGs.
- Minimum supported Windows and AutoCAD versions.
- Code signing, distribution channel, and IT deployment documentation.
