# Batch AcCoreConsole — Product Scope

## Intermediate goal

Batch AcCoreConsole is a reliable Windows batch executor for externally developed, user-vetted AutoLISP and Core Console script (`.scr`) routines. It processes large sets of DWG files through multiple bounded, isolated `accoreconsole.exe` instances.

The application is an orchestration, safety, and reporting layer around known-good routines. Routine authoring and AutoCAD automation expertise remain outside the application.

## In scope

- Select an AcCoreConsole executable, an AutoLISP routine with an explicit entry-point function, or a standalone `.scr` script.
- Resolve a drawing list or input directory into a reviewable queue, then execute the queue with configurable bounded concurrency.
- Apply basic validation for profile values, required files, input drawings, output locations, worker limits, and timeouts.
- Apply operational guardrails: per-worker Core Console isolation, dialog suppression where applicable, controlled saving, timeouts, cancellation of queued work, retained logs, and per-drawing/batch summaries.
- Report each drawing's lifecycle and outcome independently of whether its routine creates an output file.
- Combine compatible per-drawing outputs when a routine is configured to create them. A routine may produce no output, and CSV is not the only possible output format.
- Store routine metadata outside routine source when needed, using a small manifest/catalog rather than source-file comment conventions.

## Execution assumptions

- Users create, test, and vet their own LISP and SCR routines before selecting them for a batch.
- A selected routine is treated as a stable user artifact. The application safeguards its execution; it does not attempt to determine whether its AutoCAD operations are correct.
- Scripts and routines may be repeatable production tools or one-off, niche jobs. Both are valid batch inputs.
- A routine can optionally write a small, stable, Core Console-compatible result artifact so the application can surface routine-specific messages and declared outputs. The application-owned process, log, and summary records remain authoritative for execution status.

## Deliberate boundaries

- Do not recreate AutoCAD editing tools, interactive workflows, or AutoCAD toolsets.
- Do not author, edit, debug, interpret, or comprehensively analyze AutoLISP or SCR source.
- Do not build a script-pipeline designer, a LISP/SCR IDE, or a general automation environment.
- Do not require a one-argument LISP function, infer an entry point from a filename, or require CSV output.
- Do not implement deep dependency discovery or resolution for LISP modules, DCL files, fonts, templates, plot styles, or other routine dependencies.
- Do not make backwards compatibility with the current prototype's JSON/profile or single-LISP conventions a design constraint; deployment has not progressed beyond testing.
- Defer package distribution, trust/signing, advanced routine authoring assistance, and non-destructive single-DWG test harnesses until a future need justifies them.

## Design principle

Keep the product simple: execute known-good routines safely and observably at scale. Add only the configuration and reporting needed to make that execution repeatable, diagnosable, and practical.
