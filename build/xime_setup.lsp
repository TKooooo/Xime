;; ============================================================
;;  X输入法 自动测试 — 第一步：配置环境
;;  acad.exe /b xime_setup.scr
;;  作用：开启命令历史日志，设置日志路径，然后退出
;; ============================================================

(setvar "LOGFILEMODE" 1)
(setq logfile "C:/Users/XJH/Desktop/xime_acad_history.log")
(setvar "LOGFILEPATH" "C:/Users/XJH/Desktop/")
(princ (strcat "\n日志文件: " logfile))
(princ)
