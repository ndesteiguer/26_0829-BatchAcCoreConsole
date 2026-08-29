;;; ExportXrefCsv.lsp
;;;
;;; Non-interactive XREF inventory for AutoCAD Core Console.
;;; Uses only standard AutoLISP functions: no COM, ActiveX, dialogs, or prompts.
;;; Output: <drawing folder><drawing name without extension>.xrefs.csv

(defun xref:csv-field (value / index character result)
  ;; Quote every field and escape embedded quotes per RFC 4180.
  (if (null value) (setq value ""))
  (setq index 1
        result "")
  (repeat (strlen value)
    (setq character (substr value index 1))
    (if (= character "\"")
      (setq result (strcat result "\"\""))
      (setq result (strcat result character))
    )
    (setq index (1+ index))
  )
  (strcat "\"" result "\"")
)

(defun xref:write-row (stream values / line first-value)
  (setq line ""
        first-value T)
  (foreach value values
    (if first-value
      (setq first-value nil)
      (setq line (strcat line ","))
    )
    (setq line (strcat line (xref:csv-field value)))
  )
  (write-line line stream)
)

(defun xref:flag-set-p (flags bit)
  ;; A malformed/proxy block record must not abort the export.
  (and (numberp flags) (= bit (logand flags bit)))
)

(defun xref:status (block-record / flags)
  ;; An unloaded xref has group code 71.  Bit 32 denotes a resolved xref.
  (setq flags (cdr (assoc 70 block-record)))
  (cond
    ((assoc 71 block-record) "Unloaded")
    ((xref:flag-set-p flags 32) "Loaded")
    (T "Not Found")
  )
)

(defun xref:type (block-record / flags)
  ;; Bit 8 distinguishes overlay from an attached xref.
  (setq flags (cdr (assoc 70 block-record)))
  (if (xref:flag-set-p flags 8) "Overlay" "Attach")
)

(defun xref:number (value)
  (if (numberp value) (rtos value 2 8) "")
)

(defun xref:write-insert-row (stream drawing-path block-record insert-data / point)
  (setq point (cdr (assoc 10 insert-data)))
  (if (not (listp point)) (setq point (list nil nil nil)))
  (xref:write-row stream
    (list
      drawing-path
      (cdr (assoc 2 block-record))
      (or (cdr (assoc 1 block-record)) "")
      (xref:status block-record)
      (xref:type block-record)
      (or (cdr (assoc 8 insert-data)) "")
      (xref:number (car point))
      (xref:number (cadr point))
      (xref:number (caddr point))
      (xref:number (cdr (assoc 41 insert-data)))
      (xref:number (cdr (assoc 42 insert-data)))
      (xref:number (cdr (assoc 43 insert-data)))
    )
  )
)

(defun xref:write-definition-row (stream drawing-path block-record)
  ;; Keep an unloaded, missing, or unused XREF visible in the inventory.
  (xref:write-row stream
    (list
      drawing-path
      (cdr (assoc 2 block-record))
      (or (cdr (assoc 1 block-record)) "")
      (xref:status block-record)
      (xref:type block-record)
      "" "" "" "" "" "" ""
    )
  )
)

(defun xref:write-xref-rows (stream drawing-path block-record inserts / index insert-data found)
  (setq index 0
        found nil)
  (if inserts
    (repeat (sslength inserts)
      (setq insert-data (entget (ssname inserts index)))
      (if (= (cdr (assoc 2 insert-data)) (cdr (assoc 2 block-record)))
        (progn
          (xref:write-insert-row stream drawing-path block-record insert-data)
          (setq found T)
        )
      )
      (setq index (1+ index))
    )
  )
  (if (not found)
    (xref:write-definition-row stream drawing-path block-record)
  )
)

(defun xref:output-path (/ drawing-name dot-position)
  (setq drawing-name (getvar "DWGNAME")
        dot-position (strlen drawing-name))
  (while (and (> dot-position 0)
              (/= (substr drawing-name dot-position 1) "."))
    (setq dot-position (1- dot-position))
  )
  (if (> dot-position 0)
    (setq drawing-name (substr drawing-name 1 (1- dot-position)))
  )
  (strcat (getvar "DWGPREFIX") drawing-name ".xrefs.csv")
)

(defun c:EXPORTXREFCSV (/ output-path stream drawing-path inserts table-record block-record flags row-count)
  (setq output-path (xref:output-path)
        drawing-path (strcat (getvar "DWGPREFIX") (getvar "DWGNAME"))
        inserts (ssget "_X" '((0 . "INSERT")))
        row-count 0)
  (if (setq stream (open output-path "w"))
    (progn
      (xref:write-row stream
        (list "DrawingPath" "XrefName" "XrefPath" "Status" "ReferenceType" "Layer"
              "InsertionX" "InsertionY" "InsertionZ"
              "ScaleX" "ScaleY" "ScaleZ")
      )
      (setq table-record (tblnext "BLOCK" T))
      (while table-record
        ;; entget supplies the complete block-table-record data, including
        ;; group 71 when an XREF is unloaded.
        (setq block-record (entget (tblobjname "BLOCK" (cdr (assoc 2 table-record)))))
        (setq flags (cdr (assoc 70 block-record)))
        (if (xref:flag-set-p flags 4)
          (progn
            (xref:write-xref-rows stream drawing-path block-record inserts)
            (setq row-count (1+ row-count))
          )
        )
        (setq table-record (tblnext "BLOCK"))
      )
      (close stream)
      (princ (strcat "\nEXPORTXREFCSV: wrote " (itoa row-count)
                     " XREF definition(s) to " output-path))
    )
    (princ (strcat "\nEXPORTXREFCSV: unable to write " output-path))
  )
  (princ)
)

(princ)
