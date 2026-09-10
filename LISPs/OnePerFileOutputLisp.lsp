;===============================================================
;	One-File-Per-File-Output Lisp
;   	Purges unused materials, plot styles, and registered applications, then
;   	writes the number removed from each collection to outputPath.
;==============================================================

(defun BC:CountTableRecords (tableName / record count)
	(setq count 0)
	(setq record (tblnext tableName T))
	(while record
		(setq count (1+ count))
		(setq record (tblnext tableName))
	)
	count
)

(defun BC:NamedDictionary (dictionaryName / dictionaryData)
	(setq dictionaryData (dictsearch (namedobjdict) dictionaryName))
	(if dictionaryData
		(cdr (assoc -1 dictionaryData))
	)
)

(defun BC:CountDictionaryEntries (dictionaryName / dictionary entry count)
	(setq count 0)
	(setq dictionary (BC:NamedDictionary dictionaryName))
	(if dictionary
		(progn
			(setq entry (dictnext dictionary T))
			(while entry
				(setq count (1+ count))
				(setq entry (dictnext dictionary))
			)
		)
	)
	count
)

(defun BC:NonNegativeDifference (before after)
	(if (> before after)
		(- before after)
		0
	)
)

(defun BC:DwgBaseName (dwgName)
	(if (and (> (strlen dwgName) 4)
			 (= (strcase (substr dwgName (- (strlen dwgName) 3) 4)) ".DWG")
		)
		(substr dwgName 1 (- (strlen dwgName) 4))
		dwgName
	)
)

(defun BC:PurgeOutputPath (outputDirectory / separator)
	(setq separator "/")
	(if (= (substr outputDirectory (strlen outputDirectory) 1) "\\")
		(setq separator "")
	)
	(if (= (substr outputDirectory (strlen outputDirectory) 1) "/")
		(setq separator "")
	)
	(strcat
		outputDirectory
		separator
		(BC:DwgBaseName (getvar "DWGNAME"))
		".PurgeResources.json"
	)
)

(defun BC:WritePurgeJson (outputPath fileName materials plotStyles regApps / outputFile)
	(if (setq outputFile (open outputPath "w"))
		(progn
			(write-line "{" outputFile)
			(write-line (strcat "    \"Filename\" : \"" fileName "\",") outputFile)
			(write-line (strcat "    \"Materials\" : " (itoa materials) ",") outputFile)
			(write-line (strcat "    \"PlotStyles\" : " (itoa plotStyles) ",") outputFile)
			(write-line (strcat "    \"RegApps\" : " (itoa regApps)) outputFile)
			(write-line "}" outputFile)
			(close outputFile)
			T
		)
		 nil
	)
)

(defun BC:PurgeResources (outputDirectory / outputPath materialsBefore materialsAfter
		plotStylesBefore plotStylesAfter regAppsBefore regAppsAfter)
	(setq outputPath (BC:PurgeOutputPath outputDirectory))
	(setq materialsBefore (BC:CountDictionaryEntries "ACAD_MATERIAL"))
	(setq plotStylesBefore (BC:CountDictionaryEntries "ACAD_PLOTSTYLENAME"))
	(setq regAppsBefore (BC:CountTableRecords "APPID"))

	(command "_.-PURGE" "_MA" "*" "_N")
	(command "_.-PURGE" "_P" "*" "_N")
	(command "_.-PURGE" "_R" "*" "_N")

	(setq materialsAfter (BC:CountDictionaryEntries "ACAD_MATERIAL"))
	(setq plotStylesAfter (BC:CountDictionaryEntries "ACAD_PLOTSTYLENAME"))
	(setq regAppsAfter (BC:CountTableRecords "APPID"))

	(if (BC:WritePurgeJson
			outputPath
			(getvar "DWGNAME")
			(BC:NonNegativeDifference materialsBefore materialsAfter)
			(BC:NonNegativeDifference plotStylesBefore plotStylesAfter)
			(BC:NonNegativeDifference regAppsBefore regAppsAfter)
		)
		(princ (strcat "\nPurge results written to: " outputPath))
		(princ (strcat "\nUnable to write purge results to: " outputPath))
	)
	(princ)
)

(princ)
