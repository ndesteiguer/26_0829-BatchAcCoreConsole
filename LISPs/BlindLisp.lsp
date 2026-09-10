;===============================================================
;	Blind Lisp
;		Move each external reference INSERT to a dedicated layer.
; 		Layer format: 0_[Reference Name]
; 		Run with: XREFLAYERS
;==============================================================
(defun BC:EnsureLayer (layerName / layerData)
	(if (not (tblsearch "LAYER" layerName))
		(entmake
			(list
				'(0 . "LAYER")
				'(100 . "AcDbSymbolTableRecord")
				'(100 . "AcDbLayerTableRecord")
				(cons 2 layerName)
				'(70 . 0)
				'(62 . 7)
				'(6 . "Continuous")
			)
		)
	)
	(tblsearch "LAYER" layerName)
)

(defun BC:MoveReferenceInserts (referenceName layerName / selectionSet index entityData)
	(if (setq selectionSet
						 (ssget "_X"
							 (list
								 '(0 . "INSERT")
								 (cons 2 referenceName)
							 )
						 )
			)
		(progn
			(setq index 0)
			(while (< index (sslength selectionSet))
				(setq entityData (entget (ssname selectionSet index)))
				(if (assoc 8 entityData)
					(entmod (subst (cons 8 layerName) (assoc 8 entityData) entityData))
				)
				(setq index (1+ index))
			)
			(sslength selectionSet)
		)
		0
	)
)

(defun c:XREFLAYERS ( / blockData referenceName layerName movedCount)
	(setq movedCount 0)
	(setq blockData (tblnext "BLOCK" T))
	(while blockData
		(if (= 4 (logand 4 (cdr (assoc 70 blockData))))
			(progn
				(setq referenceName (cdr (assoc 2 blockData)))
				(setq layerName (strcat "0_" referenceName))
				(if (BC:EnsureLayer layerName)
					(setq movedCount
								(+ movedCount
									 (BC:MoveReferenceInserts referenceName layerName)
								)
					)
				)
			)
		)
		(setq blockData (tblnext "BLOCK"))
	)
	(princ (strcat "\nMoved " (itoa movedCount) " external reference INSERT(s)."))
	(princ)
)

(princ)
