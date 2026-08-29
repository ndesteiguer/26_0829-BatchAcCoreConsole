(defun LayoutsToLog ( / LayoutListOrdered cnt len name lays)
  ;Helper Function(s)
  (defun LayoutListOrdered ( / dLays lays oLays cnt)
    (setq dLays (cdr (assoc -1 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))
    (setq lays (mapcar '(lambda (l) (cons (cdr (assoc 71 (dictsearch dLays l))) l)) (layoutlist)))
    (repeat (1- (setq cnt (1+ (length lays))))
      (setq oLays (cons (cdr (assoc (setq cnt (1- cnt)) lays)) oLays))
    );repeat
  );defun
  ;Prep
  (setq cnt 0)
  (setq lays (LayoutListOrdered))
  (setq len (itoa (length lays)))
  (setvar 'CMDECHO 0)
  
  (setq timestamp (dateTime))
							; Update this file path
	(setq a '() fn (strcat "C:\\pwworking\\DRLE_SupportFiles\\Programming\\LayoutsToLog.csv"))
	(setq f (open fn "a+"))
  
  (foreach lay lays
	(princ lay)
	(princ "\n")
	(princ (strcat timestamp "," (getvar "DWGNAME") "," lay ) f)
	(princ "\n" f)
  );foreach
  (close f)
  ;finish up
  (setvar 'CMDECHO 1)
  (princ)
);defun

;;------------------------------------------------------------;;
;; Date Time Function										  ;;
;;------------------------------------------------------------;;
(defun dateTime ( / cdate_val YYYY M D HH MM SS)
	  ; Get the current date/time
	  (setq cdate_val (rtos (getvar "CDATE") 2 6))

	  ; Break up the string into its separate date and time parts
	  (setq YYYY (substr cdate_val 1 4)
			M    (substr cdate_val 5 2)
			D    (substr cdate_val 7 2)
			HH   (substr cdate_val 10 2)
			MM   (substr cdate_val 12 2)
			SS   (substr cdate_val 14 2)
	  )
	  (strcat M "/" D "/" YYYY " " HH ":" MM ":" SS)
)