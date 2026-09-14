# Batch AcCoreConsole — Execution Contract v1

> **Scope:** This document defines the paired execution-definition JSON contract for vetted AutoLISP and SCR routines. It is the authoritative execution-detail reference for the product direction in [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md).

## Purpose

An execution definition tells Batch AcCoreConsole only how to launch a vetted routine and, when applicable, how to collect its output. It does not describe, parse, validate, or author the routine's domain logic.

Each routine is distributed with one paired definition file:

```text
MyRoutine.lsp
MyRoutine.execution.json
```

The definition refers to the routine by a path relative to its own location. No master catalog is required.

## Execution types

| Type | Required fields | Invocation | Routine output |
|---|---|---|---|
| `blind-script` | `type`, `routine` | Executes the SCR for each DWG | None |
| `blind-lisp` | `type`, `routine`, `function` | `(FunctionName)` | None |
| `lisp-result` | `type`, `routine`, `function`, `outputFormat` | `(FunctionName batchOutputDirectory)` | One result file per successful DWG |
| `lisp-report` | `type`, `routine`, `function`, `outputFormat` | `(FunctionName batchOutputDirectory)` | One multi-line report file per successful DWG |
| `lisp-input-report` | `type`, `routine`, `function`, `outputFormat` | `(FunctionName sharedInputFilePath batchOutputDirectory)` | One multi-line report file per successful DWG |

`FunctionName` is the exact named AutoLISP function, including a `c:` prefix when that is how the routine exposes its callable entry point.

## Definition fields

| Field | Required for | Meaning |
|---|---|---|
| `type` | All definitions | One of the five execution types above. The type fixes the argument convention. |
| `routine` | All definitions | Relative path to the paired `.lsp` or `.scr` file. |
| `function` | LISP definitions | Explicit named LISP entry point. |
| `outputFormat` | Output-producing LISP definitions | Lowercase file extension (letters/digits only), such as `csv`, `json`, `xml`, or `log`. |

There are no argument arrays, output filename templates, input labels, input-extension lists, source-code headers, or dependency declarations in v1.

## Examples

### Blind script

```json
{
  "type": "blind-script",
  "routine": "BlindScript.scr"
}
```

### Blind LISP

```json
{
  "type": "blind-lisp",
  "routine": "BlindLisp.lsp",
  "function": "c:XREFLAYERS"
}
```

### LISP result

```json
{
  "type": "lisp-result",
  "routine": "PurgeResources.lsp",
  "function": "BC:PurgeResources",
  "outputFormat": "json"
}
```

### LISP report

```json
{
  "type": "lisp-report",
  "routine": "XrefReport.lsp",
  "function": "REFREPORTCSV",
  "outputFormat": "csv"
}
```

### LISP input report

```json
{
  "type": "lisp-input-report",
  "routine": "RepathXrefs.lsp",
  "function": "wsle-repathcsv",
  "outputFormat": "csv"
}
```

## Batch profile inputs

The batch profile selects the execution-definition file and supplies batch-specific values. These do not belong in the paired definition:

- Core Console executable path.
- DWG input list or input directory.
- Worker count, timeout, save policy, logs, work directory, and results directory.
- `SharedInputFilePath`, required only for `lisp-input-report`.

The application validates that a required shared input file exists and is readable. Its file format, columns, and domain meaning are defined by routine documentation and handled by the vetted LISP.

## Output handling

For every output-producing LISP type, the runner creates one fresh batch-output directory within the configured work directory and passes that directory to every LISP invocation in the batch.

The routine chooses its own filename and writes one primary output file of its declared `outputFormat` into that directory for each successful DWG. The runner does not require or infer an output naming convention.

After the batch completes, the runner:

1. Enumerates files of the declared format in the fresh batch-output directory.
2. Reports the number of successful DWGs and the number of output files found.
3. Combines found files using the format's supported combiner.

Initial supported formats:

- `csv`: combine files with matching headers into one CSV.
- `json`: combine valid per-file JSON values into one JSON array.

Other formats are counted and reported as found artifacts in the batch-output directory, but are not combined until an explicit, tested combiner is added.

## Execution lifecycle

The application owns the Core Console session lifecycle for every execution type:

1. Prepare the DWG and routine invocation.
2. Run the selected routine.
3. Apply the profile's save policy.
4. Record completion for application-level success/failure reporting.
5. Quit Core Console.

User-provided SCR and LISP routines must not save or quit the session. A future release may offer a non-blocking warning when a selected SCR appears to contain save or quit commands.

## Validation boundary

The application validates the definition structure, supported type/format values, routine-file existence and extension, explicit LISP function field, required shared input-file existence/readability, and normal batch settings.

It does not parse LISP/SCR contents, validate LISP signatures, parse shared-input contents, resolve routine dependencies, or determine whether routine behavior is correct.
