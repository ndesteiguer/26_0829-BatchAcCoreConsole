;;; Batch AcCoreConsole-compatible block redefinition and attribute synchronization.
;;;
;;; Input CSV columns: Block Name,Source DWG Path
;;; Batch entry point:
;;;   (R2-RedefineBlocksFromCsv "C:\\path\\blocks.csv" "C:\\path\\output")
;;;
;;; This file is intentionally limited to core AutoLISP: no Visual LISP, COM,
;;; dialogs, anonymous error handlers, or user input prompts.

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

;;; A named error handler is required here. Core Console can invoke *error*
;;; after an ATTSYNC command failure; an anonymous lambda masks that failure.
(defun r2:attsync-error (message)
  (setq r2:attsync-message message
        *error* r2:attsync-old-error)
  (princ (strcat "\nR2: ATTSYNC failed: " message))
  (princ)
)

(defun r2:synchronize-attributes (block-name / command-result)
  (setq r2:attsync-message nil
        r2:attsync-old-error *error*
        *error* r2:attsync-error)
  ; command-s receives the complete prompt sequence, including the default Yes.
  (setq command-result (command-s "_.ATTSYNC" "_Name" block-name ""))
  (setq *error* r2:attsync-old-error)
  (if r2:attsync-message
    (strcat "Failed: ATTSYNC: " r2:attsync-message)
    "Redefined and synchronized")
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
          (r2:synchronize-attributes block-name))
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
