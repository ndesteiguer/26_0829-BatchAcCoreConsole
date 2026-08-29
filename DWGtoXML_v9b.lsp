;;; MODIFIED: 2017/09/15 de Steiguer
;;;
;;; Output XML File containing critical file information for auditing
;;;

; #### !TODO handle special chars in layer or refernce names ####
; #### !TODO handle multi layout documents and uninitiated layouts
; #### !WISHLIST capture non-dwg reference properties ####

(vl-load-com)

; True Color conversion
; Convert True Color to ACI (AutoCAD Color Index) if there is an exact match or RGB if not
;	NOTE: Returns list or integer
(defun from-True-Color ( tcol / r g b aci )
	(setq r (lsh (lsh tcol 8) -24))
    (setq g (lsh (lsh tcol 16) -24))
    (setq b (lsh (lsh tcol 24) -24))
	
	; get ACI from RGB; NOTE: this function returns the closest ACI, may not be an exact match
	(setq aci (RGB-to-ACI r g b))
	(setq n_rgb (ACI-to-RGB aci))
	
	; Recheck the return ACI against the original RGB, if missmatched then ACI was approximate and should be discarded
	(if 
		(and
			(= (nth 0 n_rgb) r)
			(= (nth 1 n_rgb) g)
			(= (nth 2 n_rgb) b)
		)
		aci
		(list r g b)
	)
)

; RGB to ACI color conversion
;	NOTE: Returns integer
(defun RGB-to-ACI ( r g b / color obj )
    (if 
		(setq obj (vla-getinterfaceobject (vlax-get-acad-object) (strcat "autocad.accmcolor." (substr (getvar 'acadver) 1 2))))
        (progn
            (setq color (vl-catch-all-apply '(lambda ( ) (vla-setrgb obj r g b) (vla-get-colorindex obj))))
            (vlax-release-object obj)
            (if 
				(vl-catch-all-error-p color)
                (prompt (strcat "\nError: " (vl-catch-all-error-message color)))
                color
            )
        )
    )
)

; ACI to RGB color conversion
; 	NOTE: Returns list
(defun ACI-to-RGB ( color / obj r )
    (if 
		(setq obj (vla-getinterfaceobject (vlax-get-acad-object) (strcat "autocad.accmcolor." (substr (getvar 'acadver) 1 2))))
        (progn
            (setq r
                (vl-catch-all-apply
                   '(lambda ( )
                        (vla-put-colorindex obj color)
                        (list (vla-get-red obj) (vla-get-green obj) (vla-get-blue obj))
                    )
                )
            )
            (vlax-release-object obj)
            (if 
				(vl-catch-all-error-p r)
                (prompt (strcat "\nError: " (vl-catch-all-error-message r)))
                r
            )
        )
    )
)

; OLE color conversion
; Convert OLE Color to ACI (AutoCAD Color Index) if there is an exact match or RGB if not
;	NOTE: Returns string
(defun from-OLE ( c / aci n_rgb )
    (setq rgb (reverse (mapcar '(lambda ( x ) (lsh (lsh (fix c) x) -24)) '(24 16 8))))
	
	(setq aci (RGB-to-ACI (nth 0 rgb) (nth 1 rgb) (nth 2 rgb)))
	(setq n_rgb (ACI-to-RGB aci))
	
	; Recheck the return ACI against the original RGB, if missmatched then ACI was approximate and should be discarded
	(if 
		(and
			(= (nth 0 n_rgb) (nth 0 rgb))
			(= (nth 1 n_rgb) (nth 1 rgb))
			(= (nth 2 n_rgb) (nth 2 rgb))
		)
		(itoa aci)
		(strcat "(" (itoa (nth 0 rgb)) " " (itoa (nth 1 rgb)) " " (itoa (nth 2 rgb)) ")")
	)
)

; Build a LAYER PROPERTIES table
(defun get-layer-dataset (/ lst o_lst layer Transparency dict LayerOverride fcolor )
	(vlax-for layer (vla-get-Layers(vla-get-ActiveDocument(vlax-get-acad-object)))
		; Get layer Transparency value and convert to normalized number
		; #### !TODO Needs vetting, observed strange results ####
		(setq Transparency (cdr (assoc 1071 (cdar (cdr (assoc -3 (entget (vlax-vla-object->ename layer) '("AcCmTransparency"))))))))
		(if (= Transparency nil)
			(setq Transparency 0)
			(progn 
				; Get the lower byte of the value 0..255, Convert the value to a percentage 
				(setq Transparency (lsh (lsh Transparency 24) -24)) 
				(setq Transparency (fix (- 100 (/ Transparency 2.55))))    
			)
		)
		; Get if layer override is present. NOTE: does not include VP Freezes; does include Color, Linetype, Lineweight, Alpha, PlotStyleName
		; !TODO is this if statment redundent? always returns true?
		(if (setq dict (cdr (assoc 360 (entget (vlax-vla-object->ename layer)))))
			(progn
				(if (/= nil (setq cod (dictsearch dict "ADSK_XREC_LAYER_COLOR_OVR")))
					(foreach v cod
						(if (= (car v) 335)
							(progn
								(setq p (vl-position v cod))
								(setq o_lst 
									(cons 
										(list
											"Color"
											(cdr (assoc 69 (entget (cdr v))))
											(from-True-Color (cdr (nth (+ p 1) cod)))
										)
										o_lst
									)
								)
							)
						)
					)
				)
				(if (/= nil (setq cod (dictsearch dict "ADSK_XREC_LAYER_LINETYPE_OVR")))
					(foreach v cod
						(if (= (car v) 335)
							(progn
								(setq p (vl-position v cod))
								(setq o_lst 
									(cons 
										(list
											"Linetype"
											(cdr (assoc 69 (entget (cdr v))))
											(cdr (assoc 2 (entget (cdr (nth (+ p 1) cod)))))
										)
										o_lst
									)
								)
							)
						)
					)
				)
				(if (/= nil (setq cod (dictsearch dict "ADSK_XREC_LAYER_LINEWT_OVR")))
					(foreach v cod
						(if (= (car v) 335)
							(progn
								(setq p (vl-position v cod))
								(setq o_lst 
									(cons 				
										(list
											"Linewt"
											(cdr (assoc 69 (entget (cdr v))))
											(cdr (nth (+ p 1) cod))
										)
										o_lst
									)
								)
							)
						)
					)				
				)
				(if (/= nil (setq cod (dictsearch dict "ADSK_XREC_LAYER_ALPHA_OVR")))
					(foreach v cod
						(if (= (car v) 335)
							(progn
								(setq p (vl-position v cod))
								(setq o_lst 
									(cons 				
										(list
											"Alpha"
											(cdr (assoc 69 (entget (cdr v))))
											(fix (- 100 (/ (lsh (lsh (cdr (nth (+ p 1) cod)) 24) -24) 2.55)))
										)
										o_lst
									)
								)
							)
						)
					)			
				)
			; !TODO add plotstyleoverride "ADSK_XREC_LAYER_PLOTSTYLE_OVR"
				; Remove nil entries from the list
				(setq o_lst (apply 'append (subst nil (list nil) (mapcar 'list o_lst))))
			)
		)
		; if RGB code 62 returns nearest ACI and code 420 appears
		;	Note: vla-get-color also returns nearest ACI
		;	Note: code 420 is OLE color
		(if 
			(/= nil (cdr (assoc 420 (entget (vlax-vla-object->ename layer)))))
			(setq fcolor (from-OLE (cdr (assoc 420 (entget (vlax-vla-object->ename layer))))))
			(setq fcolor (itoa (vla-get-color layer)))
		)
		; List of layer properties for return
		(setq lst (cons
			(list
				(list
					"<Layer "
					(strcat "Layer=\"" (vla-get-name layer) "\" ")
					(strcat "LayerOn=\"" (if (= (vla-get-layeron layer) :vlax-true) "On" "Off") "\" ")
					(strcat "Freeze=\"" (if (= (vla-get-freeze layer) :vlax-true) "Frozen" "Thawed") "\" ")
					(strcat "Lock=\"" (if (= (vla-get-lock layer) :vlax-true) "Locked" "NotLocked") "\" ")
					(strcat "Color=\"" fcolor "\" ")
					(strcat "Linetype=\"" (vla-get-linetype layer) "\" ")
					(strcat "Lineweight=\"" (itoa (vla-get-lineweight layer)) "\" ")
					(strcat "Transparency=\"" (itoa Transparency) "\" ")
					(strcat "PlotStyleName=\"" (vla-get-plotstylename layer) "\" ")
					(strcat "Plottable=\"" (if (= (vla-get-plottable layer) :vlax-true) "Plottable" "NotPlottable") "\" ")
					(strcat "ViewPortDefault=\"" (if (= (vla-get-viewportdefault layer) :vlax-true) "Frozen" "Thawed") "\" ")
					(strcat "XREFLayer=\"" (if (/= nil (vl-string-search "|" (vla-get-name layer))) "True" "False") "\" ")
					(strcat "ViewPortOverride=\"" (if (/= nil (car o_lst)) "True\">" "False\" />"))
				)
				o_lst
			)
			lst)
		)
	(setq o_lst nil)
	)
	(setq lst (vl-sort lst (function (lambda (e1 e2)(< (strcase (nth 1 (nth 0 e1))) (strcase (nth 1 (nth 0 e2))))))))
	(append 
		(vl-remove-if '(lambda (e3) (/= nil (vl-string-search "|" (nth 1 (car e3))))) lst)
		(vl-remove-if-not '(lambda (e4) (/= nil (vl-string-search "|" (nth 1 (car e4))))) lst) 
	) ; <<< RETURN
)
; Find all parent child relationships
(defun find-child-refs ( object / x y c cP cC CL PL rData parent child reName)
	(setq y (member '(102 . "{BLKREFS") (entget (vlax-vla-object->ename object))))
	(setq refName (assoc 2 (entget (vlax-vla-object->ename object))))
	(foreach x y
		(cond
			((equal (car x) 331)
				;Is Parent
				(setq PL (cons (cdr (assoc 2 (entget (cdr x)))) PL))
			)
			((equal (car x) 332)
				;Is Child
				(setq CL (cons (cdr (assoc 2 (entget (cdr x)))) CL))
			)
		)
	)
	(foreach c CL
		(if (null (car PL))
			(setq rData (cons (list c (cdr refName)) rData))
			(setq rData (cons (list c (car PL)) rData))
		)
	)
	rData 
)
; Build a REFERENCE PROPERTIES table
(defun get-refs-dataset ( / lst status type parent PCList )
	(vlax-for block (vla-get-Blocks(vla-get-ActiveDocument(vlax-get-acad-object)))
		(if (eq (vla-get-IsXref block) :vlax-true)
			(progn
			; XREF STATUS
				; Check Xdata for entry 71; only exists when reference is unloaded
				(if 
					(null (assoc 71 (setq data (entget (tblobjname "block" (setq name(vlax-get-property block 'Name)))))))
				; Either loaded or not found
					; Check 6th bit; 1 = Loaded, 0 = Unloaded
					(if 
						(eq 32 (logand 32 (cdr (assoc 70 (tblsearch "block" (vlax-get-property block 'Name))))))
						(setq status "Loaded")
						(setq status "Not Found")
					)
				; Is unloaded
					(setq status "Unloaded")
				)
			; IS XREF ATTACHED OR OVERLAID
				; Check 3rd bit; if = 1 then Overlay, if = 0 then Attached
				(if 
					(eq 8 (logand 8 (cdr (assoc 70 (tblsearch "block" (vlax-get-property block 'Name))))))
					(setq type "Overlay")
					(setq type "Attach")
				)
			; If parent or child ref found store relationship
				(if 
					(not (null (find-child-refs block)))
					(setq PCList (append (find-child-refs block) PCList))
					(if 
						(null (assoc (vla-get-Name block) PCList))
						(setq parent "None")
						(setq parent (car (cdr (assoc (vla-get-Name block) PCList))))
					)
				)
				; !TODO rework the parent child process to eliminate the need for this cleanup
				(if (or (null parent) (= "None" parent)) (setq parent "None"))
			
			; ename of OBJ block
			; Nested XREFs do not have insertion/rotation/scale values, inherited from parent
			(if (setq blockEname (cdr (assoc 331 (entget (vlax-vla-object->ename block)))))
				(progn
				; XREF Insertion point "X, Y, Z"
					(setq xrefInsPt (strcat
						(rtos (nth 0 (cdr (assoc 10 (entget blockEname)))) 2 4)
						", "
						(rtos (nth 1 (cdr (assoc 10 (entget blockEname)))) 2 4)
						", "
						(rtos (nth 2 (cdr (assoc 10 (entget blockEname)))) 2 4)))
				; XREF Scale "X, Y, Z"
					(setq xrefScale (strcat
						(rtos (cdr (assoc 41 (entget blockEname))) 2 4)
						", "
						(rtos (cdr (assoc 42 (entget blockEname))) 2 4)
						", "
						(rtos (cdr (assoc 43 (entget blockEname))) 2 4)))
				; XREF Rotation angle in radians from 0 degrees north counterclockwise
					(setq xrefRot (rtos (cdr (assoc 50 (entget blockEname))) 2 4))
				)
				(progn
					(setq xrefInsPt "Child")
					(setq xrefScale "Child")
					(setq xrefRot "Child")
				)
			)
			; List of reference properties for return
				(setq lst (cons
					(list
						"<XREF "
						(strcat "Name=\"" (vla-get-Name block) "\" ")
						(strcat "Path=\"" (vla-get-Path block) "\" ")
						(strcat "Status=\"" status "\" ")
						(strcat "Type=\"" type "\" ")
						(strcat "Parent=\"" parent "\" ")
						(strcat "Insertion=\"" xrefInsPt "\" ")
						(strcat "Scale=\"" xrefScale "\" ")
						(strcat "Rotation=\"" xrefRot "\" ")
						"/>"
				) lst))
			)
		)
	)
	(setq PCList nil)
	(setq lst (vl-sort lst(function (lambda (e1 e2)(< (strcase (nth 1 e1)) (strcase (nth 1 e2)))))))
)
; Build a VIEWPORT PROPERTIES table
(defun get-vp-dataset ( / vlist i vp vpfreezes lst)
	; gen list of all VPs, including layout
	(setq vlist (ssget "X" '((0 . "VIEWPORT"))))
	(setq lst nil)
	(setq i 0)
	(repeat (sslength vlist)
		(setq vp (entget (ssname vlist i)))
		; for each vp frozen layer in each vp add to list
		(while (vl-position (assoc 331 vp) vp)
			(setq vpfreezes
				(cons 
					(strcat "\"" (cdr (assoc 2 (entget (cdr (assoc 331 vp))))) "\"")
					vpfreezes
				)
			)
			(setq vp (vl-remove (assoc 331 vp) vp))
		)
		(setq lst (cons
			(list 
				(list
					"<Viewport "
					; get vp ID
					(strcat "ID=\"" (itoa (cdr (assoc 69 vp))) "\" ")
					; get viewport status (has it been deleted)
					(strcat "Status=\"" (if (= -1 (cdr (assoc 68 vp))) "Off" "On") "\" ")
					; get vp locked T = locked, nil = unlocked [check 15th bit]
					(strcat "Locked=\"" (if (= 16384 (logand 16384 (cdr (assoc 90 (entget (ssname vlist i)))))) "True" "False") "\" ")
					; get vp paper-units/drawing-units
					(strcat "Scale=\"" (rtos (if 
						(/= (ssname vlist i) nil)
						(vla-get-customscale (vlax-ename->vla-object (ssname vlist i)))
					) 2 6) "\" ")
					; get vp rotation in radians
					(strcat "Rotation=\"" (angtos (cdr (assoc 51 vp)) 0 4)  "\" ")
					; get vp freezes
					(strcat "VPFreezes=\"" (if (/= nil vpfreezes) "True\">" "False\" />"))
				)
				vpfreezes
			)
			lst)
		)
		(setq i (+ i 1))
		(setq vpfreezes nil)
	)
	lst
)
; Write XML to specified file
(defun write-xml (fn / f row col)
	(if (setq f (open fn "w+"))
		(progn
			(princ "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" f)
			(princ "<Report>\n" f)
		; Write Layers Section
			(princ "<Layers>\n" f)
			(foreach row (get-layer-dataset)
				(foreach col (car row)
					(princ col f)
				)
				(princ "\n" f)
				; Nested section to write vp overrides
				(if (/= nil (car (cdr row)))
					(progn
						(foreach vpo (car (cdr row))
							(princ (strcat "<VPOverride " "Type=\"" (nth 0 vpo) "\"" " Viewport=\"" (itoa (nth 1 vpo)) "\">") f)
							(princ (nth 2 vpo) f) 
							(princ "</VPOverride>" f)
							(princ "\n" f)
						)
					(princ "</Layer>\n" f)
					)
				)
			)
			(princ "</Layers>\n" f)
		; Write References Section
			(princ "<References>\n" f)
			(foreach row (get-refs-dataset)
				(foreach col row
					(princ col f)
				)
				(princ "\n" f)
			)
			(princ "</References>\n" f)
		; Write Viewports Section
			(princ "<Viewports>\n" f)
			(foreach row (get-vp-dataset)
				(foreach col (car row)
					(princ col f)
				)
				(princ "\n" f)
				; Nested section to write vp freezes
				(if (/= nil (car (cdr row)))
					(progn 
						(foreach vpl (car (cdr row))
							(princ (strcat "<VPFreeze>" (vl-string-trim "\"" vpl) "</VPFreeze>") f)
							(princ "\n" f)
						)
					(princ "</Viewport>\n" f)
					)
				)
			)
			(princ "</Viewports>\n" f)
			(princ "</Report>" f)
			(close f)
			T
		)
	nil
	)
)
; RUN FUNCTION
(defun c:DWGtoXML (/ a fn)
	(if (null (vl-file-directory-p (strcat (getvar "dwgprefix") "XML\\")))
		(vl-mkdir (strcat (getvar "dwgprefix") "XML\\"))
	)
	(setq a '() fn (strcat (getvar "dwgprefix") "XML\\" (vl-string-right-trim ".dwg" (getvar "dwgname")) ".xml")) 
    (if (write-xml fn)
		(princ "\nXML output successful.")
		(princ "\nError: XML output failed!")
    )
	(princ)
)