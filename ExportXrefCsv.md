# Export XREF CSV

`ExportXrefCsv.lsp` is a standalone, non-interactive AutoLISP routine for
AutoCAD Core Console.  It does not use COM, ActiveX, `vl-`, or `vlax-`
functions.

Configure the batch runner with:

```json
"LispPath": "C:\\full\\path\\to\\ExportXrefCsv.lsp",
"LispExpression": "(c:EXPORTXREFCSV)"
```

For each processed drawing, the command creates a CSV beside the drawing:

```text
<drawing name>.xrefs.csv
```

The CSV contains one row per top-level XREF insertion. XREF definitions with
no insertion (including unloaded or missing references) remain present as a
row with blank insertion fields. It reports the definition name/path, loaded
state, attached-versus-overlay type, block and insertion handles, layer,
space, insertion point, scale, and rotation in degrees.
