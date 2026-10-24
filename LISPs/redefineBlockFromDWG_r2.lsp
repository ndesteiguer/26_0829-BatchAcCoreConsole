;;; Batch AcCoreConsole-compatible block redefinition and attribute synchronization.
;;;
;;; Input CSV columns: Block Name,Source DWG Path
;;; Batch entry point:
;;;   (R2-RedefineBlocksFromCsv "C:\\path\\blocks.csv" "C:\\path\\output")
;;;
;;; This file is intentionally limited to core AutoLISP: no Visual LISP, COM,
;;; dialogs, anonymous error handlers, or user input prompts. ATTSYNC is not
;;; available in AcCoreConsole, so existing references are recreated directly.

(defun r2:trim (value / first last)
  (setq first 1
        last (strlen value))
  (while (and (<= first last)
              (member (ascii (substr value first 1)) '(9 32)))
    (setq first (1+ first))
  )
  (while (and (<= first last)
              (member (ascii (substr value last 1)) '(9 32)))
    (setq last (1- last))
  )
  (if (<= first last)
    (substr value first (1+ (- last first)))
    "")
)

(defun r2:parse-csv-row (line / index length quoted field fields character)
  (setq index 1
        length (strlen line)
        quoted nil
        field ""
        fields nil)
  (while (<= index length)
    (setq character (substr line index 1))
    (cond
      ((= character "\"")
       (if (and quoted
                (< index length)
                (= (substr line (1+ index) 1) "\""))
         (progn
           (setq field (strcat field "\"")
                 index (+ index 2)))
         (progn
           (setq quoted (not quoted)
                 index (1+ index)))))
      ((and (= character ",") (not quoted))
       (setq fields (cons field fields)
             field ""
             index (1+ index)))
      (T
       (setq field (strcat field character)
             index (1+ index))))
  )
  (if quoted
    nil
    (reverse (cons field fields)))
)

(defun r2:csv-cell (value / index length result character)
  (if (null value)
    (setq value ""))
  (setq index 1
        length (strlen value)
        result "\"")
  (while (<= index length)
    (setq character (substr value index 1))
    (setq result
      (strcat result (if (= character "\"") "\"\"" character)))
    (setq index (1+ index))
  )
  (strcat result "\"")
)

(defun r2:write-csv-row (stream values / line first)
  (setq line ""
        first T)
  (foreach value values
    (if first
      (setq first nil)
      (setq line (strcat line ",")))
    (setq line (strcat line (r2:csv-cell value))))
  (write-line line stream)
)

(defun r2:drawing-base-name (/ drawing-name dot-position)
  (setq drawing-name (getvar "DWGNAME")
        dot-position (strlen drawing-name))
  (while (and (> dot-position 0)
              (/= (substr drawing-name dot-position 1) "."))
    (setq dot-position (1- dot-position)))
  (if (> dot-position 0)
    (substr drawing-name 1 (1- dot-position))
    drawing-name)
)

(defun r2:report-path (output-folder / last-character)
  (setq last-character (substr output-folder (strlen output-folder) 1))
  (if (and (/= last-character "\\") (/= last-character "/"))
    (setq output-folder (strcat output-folder "\\")))
  (strcat output-folder (r2:drawing-base-name) ".redefineBlockFromDWG_r2.csv")
)

(defun r2:write-report (output-folder rows / report-path stream row)
  (if (or (null output-folder) (= output-folder ""))
    (princ "\nR2: no report output folder was supplied.")
    (progn
      (setq report-path (r2:report-path output-folder))
      (if (setq stream (open report-path "w"))
        (progn
          (r2:write-csv-row stream
            (list "Host File Path" "Host File Name" "Block Name"
                  "Source DWG Path" "Result"))
          (foreach row (reverse rows)
            (r2:write-csv-row stream
              (append (list (getvar "DWGPREFIX") (getvar "DWGNAME")) row)))
          (close stream)
          (princ (strcat "\nR2: wrote block report to " report-path)))
        (princ (strcat "\nR2: unable to write report: " report-path)))))
)

(defun r2:xref-p (block-record / flags)
  (setq flags (cdr (assoc 70 block-record)))
  (/= 0 (logand flags 12))
)

(defun r2:find-insert-after (previous block-name / current data found)
  (setq current
    (if previous
      (entnext previous)
      (entnext (tblobjname "BLOCK" "*Model_Space"))))
  (while (and current (not found))
    (setq data (entget current))
    (if (and (= (cdr (assoc 0 data)) "INSERT")
             (= (strcase (cdr (assoc 2 data))) (strcase block-name)))
      (setq found current)
      (setq current (entnext current))))
  found
)

(defun r2:drawing-inserts (block-name / selection index inserts)
  ;; "_X" searches the whole drawing database, including model space and all
  ;; paper-space layouts.  This is more reliable than navigating block-table
  ;; records, whose entity chains do not consistently expose layout contents.
  (setq selection (ssget "_X" (list '(0 . "INSERT") (cons 2 block-name)))
        index 0
        inserts nil)
  (if selection
    (while (< index (sslength selection))
      (setq inserts (cons (ssname selection index) inserts)
            index (1+ index))))
  (reverse inserts)
)

(defun r2:attribute-values (insert / current data values tag value)
  (setq current (if (= 1 (cdr (assoc 66 (entget insert)))) (entnext insert))
        values nil)
  (while (and current
              (/= (cdr (assoc 0 (setq data (entget current)))) "SEQEND"))
    (if (= (cdr (assoc 0 data)) "ATTRIB")
      (progn
        (setq tag (cdr (assoc 2 data))
              value (cdr (assoc 1 data)))
        (if tag
          (setq values (cons (cons tag (if value value "")) values)))))
    (setq current (entnext current)))
  values
)

(defun r2:attribute-value (tag values / value)
  (foreach item values
    (if (= (strcase tag) (strcase (car item)))
      (setq value (cdr item))))
  value
)

(defun r2:apply-attribute-values (insert values / current data tag value cell)
  (setq current (if (= 1 (cdr (assoc 66 (entget insert)))) (entnext insert)))
  (while (and current
              (/= (cdr (assoc 0 (setq data (entget current)))) "SEQEND"))
    (if (= (cdr (assoc 0 data)) "ATTRIB")
      (progn
        (setq tag (cdr (assoc 2 data))
              value (if tag (r2:attribute-value tag values)))
        (if value
          (progn
            (setq cell (assoc 1 data))
            (if cell
              (entmod (subst (cons 1 value) cell data)))))))
    (setq current (entnext current)))
  (princ)
)

(defun r2:copy-insert-code (old-data new-data code / old-cell new-cell)
  (if (setq old-cell (assoc code old-data))
    (if (setq new-cell (assoc code new-data))
      (subst old-cell new-cell new-data)
      (append new-data (list old-cell)))
    new-data)
)

(defun r2:preserve-insert-properties (old-insert new-insert / old-data new-data code)
  (setq old-data (entget old-insert)
        new-data (entget new-insert))
  ; Keep reference-level overrides. Attribute-level properties intentionally
  ; come from the redefined block, which is the observable ATTSYNC behavior.
  (foreach code '(8 6 48 60 62 370 390 420 430 440)
    (setq new-data (r2:copy-insert-code old-data new-data code)))
  (entmod new-data)
)

(defun r2:insert-sequence (insert / current data entities)
  (setq entities (list insert))
  (if (= 1 (cdr (assoc 66 (entget insert))))
    (progn
      (setq current (entnext insert))
      (while current
        (setq data (entget current)
              entities (cons current entities))
        (if (= (cdr (assoc 0 data)) "SEQEND")
          (setq current nil)
          (setq current (entnext current))))))
  entities
)

(defun r2:delete-insert (insert)
  (foreach entity (r2:insert-sequence insert)
    (entdel entity))
)

(defun r2:layer-locked-p (layer-name / layer-data flags)
  (setq layer-data (tblsearch "LAYER" layer-name)
        flags (if layer-data (cdr (assoc 70 layer-data)) 0))
  (/= 0 (logand flags 4))
)

(defun r2:set-layer-lock (layer-name locked / layer-entity layer-data flags new-flags)
  (setq layer-entity (tblobjname "LAYER" layer-name)
        layer-data (if layer-entity (entget layer-entity)))
  (if layer-data
    (progn
      (setq flags (cdr (assoc 70 layer-data))
            new-flags (if locked
                        (logior flags 4)
                        (- flags (logand flags 4))))
      (if (entmod (subst (cons 70 new-flags) (assoc 70 layer-data) layer-data))
        T
        nil))
    nil)
)

(defun r2:unlock-reference-layers (insert / entity layer-name unlocked)
  ;; Attributes may have an explicitly assigned layer, so inspect every entity
  ;; in the INSERT sequence rather than only the INSERT itself.
  (setq unlocked nil)
  (foreach entity (r2:insert-sequence insert)
    (setq layer-name (cdr (assoc 8 (entget entity))))
    (if (and layer-name
             (r2:layer-locked-p layer-name)
             (r2:set-layer-lock layer-name nil)
             (not (member layer-name unlocked)))
      (setq unlocked (cons layer-name unlocked))))
  unlocked
)

(defun r2:reference-layout (insert-data)
  (if (assoc 410 insert-data)
    (cdr (assoc 410 insert-data))
    "Model")
)

(defun r2:recreate-reference (old-insert block-name / old-data values layout
                                                          before new-insert x-scale
                                                          y-scale rotation point)
  (setq old-data (entget old-insert)
        values (r2:attribute-values old-insert)
        layout (r2:reference-layout old-data)
        point (cdr (assoc 10 old-data))
        x-scale (if (assoc 41 old-data) (cdr (assoc 41 old-data)) 1.0)
        y-scale (if (assoc 42 old-data) (cdr (assoc 42 old-data)) x-scale)
        rotation (if (assoc 50 old-data) (cdr (assoc 50 old-data)) 0.0))
  (setvar "CTAB" layout)
  (setq before (entlast))
  (command-s "_.-INSERT" block-name point x-scale y-scale rotation)
  (if (setq new-insert (r2:find-insert-after before block-name))
    (progn
      (r2:preserve-insert-properties old-insert new-insert)
      (r2:apply-attribute-values new-insert values)
      (r2:delete-insert old-insert)
      T)
    nil)
)

(defun r2:synchronize-references (block-name / old-tab old-attreq old-attdia
                                             inserts insert synchronized failed
                                             unlocked-layers reference-layers layer-name)
  (setq old-tab (getvar "CTAB")
        old-attreq (getvar "ATTREQ")
        old-attdia (getvar "ATTDIA")
        inserts (r2:drawing-inserts block-name)
        synchronized 0
        failed nil
        unlocked-layers nil)
  (setvar "ATTREQ" 0)
  (setvar "ATTDIA" 0)
  (foreach insert inserts
    (setq reference-layers (r2:unlock-reference-layers insert))
    (foreach layer-name reference-layers
      (if (not (member layer-name unlocked-layers))
        (setq unlocked-layers (cons layer-name unlocked-layers))))
    (if (r2:recreate-reference insert block-name)
      (setq synchronized (1+ synchronized))
      (setq failed T)))
  ;; Restore only layers that were locked before the routine changed them.
  (foreach layer-name unlocked-layers
    (r2:set-layer-lock layer-name T))
  (setvar "ATTREQ" old-attreq)
  (setvar "ATTDIA" old-attdia)
  (setvar "CTAB" old-tab)
  (if failed
    "Failed: one or more block references could not be recreated"
    (strcat "Redefined and synchronized " (itoa synchronized)
            " existing reference(s)"))
)

(defun r2:redefine-one (block-name source-file / block-record source-path before
                                                   inserted old-attreq old-attdia)
  (cond
    ((= block-name "") "Failed: empty block name")
    ((= source-file "") "Failed: empty source DWG path")
    ((not (setq source-path (findfile source-file))) "Failed: source DWG not found")
    ((not (setq block-record (tblsearch "BLOCK" block-name))) "Skipped: target block not found")
    ((r2:xref-p block-record) "Skipped: target block is an Xref")
    (T
      (setq before (entlast)
            old-attreq (getvar "ATTREQ")
            old-attdia (getvar "ATTDIA"))
      (setvar "ATTREQ" 0)
      (setvar "ATTDIA" 0)
      (command-s "_.-INSERT"
                 (strcat block-name "=" source-path)
                 "0,0,0" 1 1 0)
      (setvar "ATTREQ" old-attreq)
      (setvar "ATTDIA" old-attdia)
      (if (setq inserted (r2:find-insert-after before block-name))
        (progn
          (entdel inserted)
          (r2:synchronize-references block-name))
        "Failed: redefinition could not be verified")))
)

(defun R2-RedefineBlocksFromCsv (csv-file output-folder / stream header header-fields
                                                          line fields line-number records
                                                          invalid-input rows mapping block-name
                                                          source-file result)
  (setq rows nil)
  (cond
    ((or (null csv-file) (= csv-file ""))
     (setq rows (list (list "" "" "Failed: input CSV path is required"))))
    ((or (null output-folder) (= output-folder ""))
     (princ "\nR2: report output folder is required."))
    ((not (setq stream (open csv-file "r")))
     (setq rows (list (list "" "" "Failed: input CSV could not be opened"))))
    (T
     (setq header (read-line stream)
           header-fields (if header (r2:parse-csv-row header)))
     (if (not (and header-fields
                   (= (length header-fields) 2)
                   (= (strcase (r2:trim (car header-fields))) "BLOCK NAME")
                   (= (strcase (r2:trim (cadr header-fields))) "SOURCE DWG PATH")))
       (setq rows (list (list "" "" "Failed: expected CSV header Block Name,Source DWG Path")))
       (progn
         (setq line-number 1
               records nil
               invalid-input nil)
         (while (setq line (read-line stream))
           (setq line-number (1+ line-number))
           (if (/= (r2:trim line) "")
             (progn
               (setq fields (r2:parse-csv-row line))
               (if (and fields
                        (= (length fields) 2)
                        (/= (r2:trim (car fields)) "")
                        (/= (r2:trim (cadr fields)) ""))
                 (setq records
                   (cons (list (r2:trim (car fields))
                               (r2:trim (cadr fields))) records))
                 (setq invalid-input T
                       rows (cons (list "" ""
                           (strcat "Failed: invalid CSV row at line "
                                   (itoa line-number))) rows))))))
         (if invalid-input
           (setq rows (cons (list "" ""
                           "No blocks were changed because the input CSV has invalid rows.") rows))
           (foreach mapping (reverse records)
             (setq block-name (car mapping)
                   source-file (cadr mapping)
                   result (r2:redefine-one block-name source-file)
                   rows (cons (list block-name source-file result) rows))))))
     (close stream)))
  (r2:write-report output-folder rows)
  (princ)
)

(defun c:R2REDEFINEBLOCKSFROMCSV (/ csv-file output-folder)
  (setq csv-file (getstring T "\nBlock mapping CSV path: ")
        output-folder (getstring T "\nReport output folder: "))
  (R2-RedefineBlocksFromCsv csv-file output-folder)
)

(princ "\nLoaded redefineBlockFromDWG_r2. Use R2-RedefineBlocksFromCsv.")
(princ)
