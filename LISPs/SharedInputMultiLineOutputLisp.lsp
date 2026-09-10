
;===============================================================
;	Shared-Input-Multiline-Output Lisp
;   	Repath Xrefs from a Reference Filename / Path CSV file.
; 		Run: (wsle-repathcsv "C:\\path\\repath.csv" "C:\\path\\output")
; 		Command: REPATHCSV
;==============================================================
; Remove spaces and tabs from a value.
(defun wsle-repath-trim (value / first last)
	(setq first 1)
	(setq last (strlen value))
	; Move past leading spaces and tabs.
	(while (and (<= first last)
							(member (substr value first 1) '(" " "\t")))
		(setq first (1+ first))
	)
	; Move back past trailing spaces and tabs.
	(while (and (>= last first)
							(member (substr value last 1) '(" " "\t")))
		(setq last (1- last))
	)
	(if (>= last first)
		(substr value first (1+ (- last first)))
		""
	)
)

; Split a CSV row into Reference Filename and Path.
(defun wsle-repath-csv-fields (line / index length comma)
	(setq index 1)
	(setq length (strlen line))
	(setq comma nil)
	; Find the first comma that separates the two fields.
	(while (and (<= index length) (null comma))
		(if (= (substr line index 1) ",")
			(setq comma index)
		)
		(setq index (1+ index))
	)
	; Return two fields only when a comma was found.
	(if comma
		(list
			(wsle-repath-trim (substr line 1 (1- comma)))
			(wsle-repath-trim (substr line (1+ comma)))
		)
	)
)

; Return a path's file name without its extension.
(defun wsle-repath-filename-base (path / start end)
	(setq end (strlen path))
	(while (and (> end 0) (/= (substr path end 1) "."))
		(setq end (1- end))
	)
	(if (= end 0)
		(setq end (1+ (strlen path)))
	)
	(setq start (strlen path))
	(while (and (> start 0)
					(/= (substr path start 1) "\\")
					(/= (substr path start 1) "/"))
		(setq start (1- start))
	)
	(substr path (1+ start) (- end start 1))
)

; Check whether a block is an Xref.
(defun wsle-repath-xref-p (name / record flags)
	(setq record (tblsearch "BLOCK" name))
	(if record
		(progn
			(setq flags (cdr (assoc 70 record)))
			(or (= 4 (logand 4 flags))
					(= 8 (logand 8 flags)))
		)
	)
)

; Find all Xrefs whose saved path has the given file name.
(defun wsle-repath-find-xrefs (file-name / record matches path match-name)
	(setq match-name (wsle-repath-filename-base file-name))
	(setq matches '())
	(setq record (tblnext "BLOCK" T))
	(while record
		(setq path (cdr (assoc 1 record)))
		(if (and path
					(wsle-repath-xref-p (cdr (assoc 2 record)))
					(= (strcase match-name)
						 (strcase (wsle-repath-filename-base path))))
			(setq matches (cons (list (cdr (assoc 2 record)) record) matches))
		)
		(setq record (tblnext "BLOCK"))
	)
	(reverse matches)
)

(defun wsle-repath-xref-status (name / entity data flags)
	(setq entity (tblobjname "BLOCK" name))
	(if entity
		(progn
			(setq data (entget entity))
			; Group 71 exists when the Xref is unloaded.
			(if (assoc 71 data)
				"UNLOADED"
				(progn
					; Without group 71, bit 32 distinguishes loaded from Not Found.
					(setq flags (cdr (assoc 70 (tblsearch "BLOCK" name))))
					(if (and flags (= 32 (logand 32 flags)))
						"LOADED"
						"NOT FOUND"
					)
				)
			)
		)
	)
)

(defun wsle-repath-available-name (desired-name current-name / candidate counter)
	(setq candidate desired-name)
	(setq counter 1)
	(if (and (/= (strcase candidate) (strcase current-name))
			 (tblsearch "BLOCK" candidate))
		(progn
			(setq candidate (strcat desired-name "_conflict" (itoa counter)))
			(while (tblsearch "BLOCK" candidate)
				(setq counter (1+ counter))
				(setq candidate (strcat desired-name "_conflict" (itoa counter)))
			)
		)
	)
	candidate
)

(defun wsle-repath-csv-field (value / index length character result)
	(if (null value)
		(setq value "")
	)
	(setq index 1)
	(setq length (strlen value))
	(setq result "\"")
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

(defun wsle-repath-write-row (stream values / line first-value)
	(setq line "")
	(setq first-value T)
	(foreach value values
		(if first-value
			(setq first-value nil)
			(setq line (strcat line ","))
		)
		(setq line (strcat line (wsle-repath-csv-field value)))
	)
	(write-line line stream)
)

(defun wsle-repath-output-path (output-folder / drawing-name dot-position last-character)
	(setq drawing-name (getvar "DWGNAME"))
	(setq dot-position (strlen drawing-name))
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
	(strcat output-folder drawing-name ".XREFRepath.csv")
)

(defun wsle-repath-write-report (output-folder rows / output-path stream)
	(if (and output-folder (/= output-folder ""))
		(progn
			(setq output-path (wsle-repath-output-path output-folder))
			(if (setq stream (open output-path "w"))
				(progn
					(wsle-repath-write-row stream
						(list "Host File Path" "Host File Name" "Original Reference Name"
							"Final Reference Name" "Old Path" "New Path"))
					(foreach row (reverse rows)
						(wsle-repath-write-row stream row)
					)
					(close stream)
					(princ (strcat "\nWrote " (itoa (length rows))
						" updated Xref reference(s) to " output-path))
				)
				(princ (strcat "\nUnable to write Xref report: " output-path))
			)
		)
		(princ "\nXref report output folder is required.")
	)
)

; Repath all matching Xrefs for one CSV row.
(defun wsle-repath-row (fields / ref-name new-path xref-list xref-info ref-block record old-path xref-status was-unloaded newname updated-rows)
	(setq ref-name (car fields))
	(setq new-path (cadr fields))
	(setq updated-rows '())
	(cond
		((or (= ref-name "") (= new-path ""))
			(princ "Skipped CSV row with an empty Reference Filename or Path.\n"))
		((null (setq xref-list (wsle-repath-find-xrefs ref-name)))
			(princ (strcat "Xref filename not found: " ref-name "\n")))
		(T
			(foreach xref-info xref-list
				(setq ref-block (car xref-info))
				(setq record (cadr xref-info))
				(setq old-path (cdr (assoc 1 record)))
				(if (and old-path (= (strcase old-path) (strcase new-path)))
					(princ (strcat "Xref already uses path: " ref-block "\n"))
					(progn
						(setq xref-status (wsle-repath-xref-status ref-block))
						(setq was-unloaded (= xref-status "UNLOADED"))
						(command "_.-XREF" "_P" ref-block new-path)
						(if was-unloaded
							(command "_.-XREF" "_R" ref-block)
						)
						(command "_.-XREF" "_PATHTYPE" ref-block "_RELATIVE")
						(if was-unloaded
							(command "_.-XREF" "_U" ref-block)
						)
						(setq newname (wsle-repath-available-name
							(wsle-repath-filename-base new-path)
							ref-block))
						(if (= (strcase newname) (strcase ref-block))
							(princ (strcat "\nXref name already matches filename: " ref-block "\n"))
							(progn
								(command "_.-RENAME" "_BLOCK" ref-block newname)
								(princ (strcat "\nRenamed Xref Block: " ref-block " -> " newname "\n"))
							)
						)
						(setq updated-rows (cons
							(list
								(getvar "DWGPREFIX")
								(getvar "DWGNAME")
								ref-block
								(if newname newname ref-block)
								old-path
								new-path
							)
							updated-rows))
						(princ (strcat "Repathed Xref and set Path Type to Relative: " ref-block " -> " new-path "\n"))
					)
				)
			)
		)
	)
	(reverse updated-rows)
)

; Read and process the CSV file.
(defun wsle-repathcsv (csv-file output-folder / handle line fields row-count updated-rows)
	(if (null csv-file)
		(princ "\nNo CSV file specified.")
		(progn
			(setq handle (open csv-file "r"))
			(if (null handle)
				(princ (strcat "Unable to open CSV file: " csv-file "\n"))
				(progn
					(setq row-count 0)
					(setq updated-rows '())
					(read-line handle)
					; Process each data row until the end of the file.
					(while (setq line (read-line handle))
						(setq fields (wsle-repath-csv-fields line))
						; Ignore lines that do not contain two CSV fields.
						(if fields
							(progn
								(setq updated-rows
									(append (wsle-repath-row fields) updated-rows))
								(setq row-count (1+ row-count))
							)
						)
					)
					(close handle)
					(wsle-repath-write-report output-folder updated-rows)
					(princ (strcat "\nFinished. Processed " (itoa row-count) " CSV row(s)."))
				)
			)
		)
	)
	(princ)
)

(defun c:WSLE_REPATHCSV ( / csv-file output-folder)
	(setq csv-file (getstring T "\nCSV file path: "))
	(setq output-folder (getstring T "\nXref report output folder: "))
	(wsle-repathcsv csv-file output-folder)
)

(defun c:REPATHCSV () (c:WSLE_REPATHCSV))
