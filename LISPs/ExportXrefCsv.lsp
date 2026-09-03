;;; ExportXrefCsv.lsp
;;;
;;; Non-interactive XREF inventory for AutoCAD Core Console.
;;; Uses only standard AutoLISP functions: no COM, ActiveX, dialogs, or prompts.
;;; Output: <drawing folder><drawing name without extension>.xrefs.csv

(defun xref:csv-field (value / index character result)
  ;; XREF names and properties are assumed not to contain embedded quotes.
  (strcat "\"" value "\"")
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

(defun xref:is-xref-p (name / record flags)
  (setq record (tblsearch "BLOCK" name))
  (if record
    (progn
      (setq flags (cdr (assoc 70 record)))
      (or (= 4 (logand 4 flags))
          (= 8 (logand 8 flags)))
    )
  )
)

(defun xref:status (name / entity data flags)
  ;; Same status test proven in RefRepathCSV_v3.2.lsp:
  ;; group 71 means unloaded; bit 32 means loaded; otherwise not found.
  (setq entity (tblobjname "BLOCK" name))
  (if entity
    (progn
      (setq data (entget entity))
      (if (assoc 71 data)
        "Unloaded"
        (progn
          (setq flags (cdr (assoc 70 (tblsearch "BLOCK" name))))
          (if (= 32 (logand 32 flags))
            "Loaded"
            "Not Found"
          )
        )
      )
    )
    "Not Found"
  )
)

(defun xref:type (name / flags)
  (setq flags (cdr (assoc 70 (tblsearch "BLOCK" name))))
  (if (= 8 (logand 8 flags)) "Overlay" "Attach")
)

(defun xref:number (value)
  (rtos value 2 8)
)

(defun xref:write-insert-row (stream drawing-path block-record insert-data / point xref-name)
  (setq point (cdr (assoc 10 insert-data)))
  (setq xref-name (cdr (assoc 2 block-record)))
  (xref:write-row stream
    (list
      drawing-path
      xref-name
      (cdr (assoc 1 block-record))
      (xref:status xref-name)
      (xref:type xref-name)
      (cdr (assoc 8 insert-data))
      (xref:number (car point))
      (xref:number (cadr point))
      (xref:number (caddr point))
      (xref:number (cdr (assoc 41 insert-data)))
      (xref:number (cdr (assoc 42 insert-data)))
      (xref:number (cdr (assoc 43 insert-data)))
    )
  )
)

(defun xref:write-definition-row (stream drawing-path block-record / xref-name)
  ;; Keep an unloaded, missing, or unused XREF visible in the inventory.
  (setq xref-name (cdr (assoc 2 block-record)))
  (xref:write-row stream
    (list
      drawing-path
      xref-name
      (cdr (assoc 1 block-record))
      (xref:status xref-name)
      (xref:type xref-name)
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

(defun c:EXPORTXREFCSV (/ output-path stream drawing-path inserts table-record row-count)
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
        (if (xref:is-xref-p (cdr (assoc 2 table-record)))
          (progn
            (xref:write-xref-rows stream drawing-path table-record inserts)
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
