
; Repath Xrefs from a Reference Filename / Path CSV file.
; Run: (wsle-repathcsv "C:\\path\\repath.csv" 0)
; Use 1 as the second argument to rename Xref blocks.
; Command: REPATHCSV

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

; Repath all matching Xrefs for one CSV row.
(defun wsle-repath-row (fields rename-block / ref-name new-path xref-list xref-info ref-block record old-path xref-status was-unloaded newname)
	(setq ref-name (car fields))
	(setq new-path (cadr fields))
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
						(if (= rename-block 1)
							(progn
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
							)
						)
						(princ (strcat "Repathed Xref and set Path Type to Relative: " ref-block " -> " new-path "\n"))
					)
				)
			)
		)
	)
)

; Read and process the CSV file.
(defun wsle-repathcsv (csv-file rename-block / handle line fields row-count)
	(if (null rename-block)
		(setq rename-block 0)
	)
	(if (null csv-file)
		(princ "\nNo CSV file specified.")
		(progn
			(setq handle (open csv-file "r"))
			(if (null handle)
				(princ (strcat "Unable to open CSV file: " csv-file "\n"))
				(progn
					(setq row-count 0)
					(read-line handle)
					; Process each data row until the end of the file.
					(while (setq line (read-line handle))
						(setq fields (wsle-repath-csv-fields line))
						; Ignore lines that do not contain two CSV fields.
						(if fields
							(progn
								(wsle-repath-row fields rename-block)
								(setq row-count (1+ row-count))
							)
						)
					)
					(close handle)
					(princ (strcat "\nFinished. Processed " (itoa row-count) " CSV row(s)."))
				)
			)
		)
	)
	(princ)
)

(defun c:WSLE_REPATHCSV ( / csv-file rename-block)
	(setq csv-file (getstring T "\nCSV file path: "))
	(setq rename-block (getint "\nRename Xref blocks to new filename? [0=No/1=Yes] <0>: "))
	(if (null rename-block)
		(setq rename-block 0)
	)
	(wsle-repathcsv csv-file rename-block)
)

(defun c:REPATHCSV () (c:WSLE_REPATHCSV))
