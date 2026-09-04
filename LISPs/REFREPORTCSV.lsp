;;; REFREPORTCSV.lsp
;;;
;;; Writes external reference definitions to CSV.
;;; Compatible with AutoCAD Core Console; uses native AutoLISP/DXF functions only.
;;; Command: REFREPORTCSV
;;; Output: <output folder><drawing name without extension>.REFREPORTCSV.csv

(defun xrp:value (value)
  (if value value "")
)

(defun xrp:csv-field (value / index length character result)
  (setq value (xrp:value value)
        index 1
        length (strlen value)
        result "\"")
  (while (<= index length)
    (setq character (substr value index 1))
    (if (= character "\"")
      (setq result (strcat result "\"\""))
      (setq result (strcat result character))
    )
    (setq index (1+ index))
  )
  (strcat result "\"")
)

(defun xrp:write-row (stream values / line first-value)
  (setq line ""
        first-value T)
  (foreach value values
    (if first-value
      (setq first-value nil)
      (setq line (strcat line ","))
    )
    (setq line (strcat line (xrp:csv-field value)))
  )
  (write-line line stream)
)

(defun xrp:is-xref (block-name / record flags)
  (setq record (tblsearch "BLOCK" block-name))
  (if record
    (progn
      (setq flags (cdr (assoc 70 record)))
      (or (= 4 (logand 4 flags))
          (= 8 (logand 8 flags)))
    )
  )
)

(defun xrp:status (block-name / entity data record flags)
  (setq entity (tblobjname "BLOCK" block-name))
  (if entity
    (progn
      (setq data (entget entity))
      (if (assoc 71 data)
        "Unloaded"
        (progn
          (setq record (tblsearch "BLOCK" block-name)
                flags (cdr (assoc 70 record)))
          (if (= 32 (logand 32 flags)) "Found" "Not Found")
        )
      )
    )
    "Not Found"
  )
)

(defun xrp:reference-type (block-record / flags)
  (setq flags (cdr (assoc 70 block-record)))
  (if (= 8 (logand 8 flags)) "Overlay" "Attached")
)

(defun xrp:reference-filename (saved-path / index)
  (if saved-path
    (progn
      (setq index (strlen saved-path))
      (while (and (> index 0)
                  (/= (substr saved-path index 1) "\\")
                  (/= (substr saved-path index 1) "/"))
        (setq index (1- index))
      )
      (substr saved-path (1+ index))
    )
    ""
  )
)

(defun xrp:output-path (output-folder / drawing-name dot-position last-character)
  (setq drawing-name (getvar "DWGNAME")
        dot-position (strlen drawing-name))
  (while (and (> dot-position 0)
              (/= (substr drawing-name dot-position 1) "."))
    (setq dot-position (1- dot-position))
  )
  (if (> dot-position 0)
    (setq drawing-name (substr drawing-name 1 (1- dot-position)))
  )
  (setq last-character (substr output-folder (strlen output-folder) 1))
  (if (and (/= last-character "\\") (/= last-character "/"))
    (setq output-folder (strcat output-folder "\\"))
  )
  (strcat output-folder drawing-name ".REFREPORTCSV.csv")
)

(defun xrp:write-reference-row (stream host-path host-name block-record)
  (xrp:write-row stream
    (list
      host-path
      host-name
      (xrp:reference-type block-record)
      (cdr (assoc 2 block-record))
      (xrp:reference-filename (cdr (assoc 1 block-record)))
      (xrp:status (cdr (assoc 2 block-record)))
      (cdr (assoc 1 block-record))
    )
  )
)
(defun REFREPORTCSV (output-folder / output-path stream host-path host-name
                                   table-record row-count)
  (if (and output-folder (/= output-folder ""))
    (progn
      (setq output-path (xrp:output-path output-folder)
        host-path (getvar "DWGPREFIX")
        host-name (getvar "DWGNAME")
        row-count 0)
      (if (setq stream (open output-path "w"))
        (progn
          (xrp:write-row stream
            (list "Host File Path" "Host File Name" "XREF Type"
                  "Reference Name" "Reference Filename" "Status" "Saved Path")
          )
          (setq table-record (tblnext "BLOCK" T))
          (while table-record
            (if (xrp:is-xref (cdr (assoc 2 table-record)))
              (progn
                (xrp:write-reference-row stream host-path host-name table-record)
                (setq row-count (1+ row-count))
              )
            )
            (setq table-record (tblnext "BLOCK"))
          )
          (close stream)
          (princ (strcat "\nREFREPORTCSV: wrote " (itoa row-count)
                         " XREF definition(s) to " output-path))
        )
        (princ (strcat "\nREFREPORTCSV: unable to write " output-path))
      )
    )
    (princ "\nREFREPORTCSV: output folder is required.")
  )
  (princ)
)

(defun c:REFREPORTCSV (/ output-folder)
  (setq output-folder (getstring T "\nOutput folder: "))
  (REFREPORTCSV output-folder)
)

(princ)
