;; ============================================================
;;  X输入法 自动测试脚本（AutoCAD 2014）
;;  用法：acad.exe /b xime_autotest.scr
;;  作用：自动 NETLOAD 插件 → 执行诊断 → 结果写入文件
;; ============================================================

(defun c:XimeAutoTest ( / dll out f result)
  (setq dll "C:/Users/XJH/Desktop/X输入法测试_v2/Xime.dll")
  (setq out "C:/Users/XJH/Desktop/xime_autotest_result.txt")

  (setq f (open out "w"))
  (write-line "===== X输入法 AutoCAD 自动测试 =====" f)
  (write-line (strcat "时间: " (menucmd "M=$(edtime,$(getvar,date),YYYY-MO-DD HH:MM:SS)")) f)

  ;; 1) 加载插件
  (write-line "[1] NETLOAD..." f)
  (if (findfile dll)
    (progn
      (command "_.NETLOAD" dll)
      (write-line "  NETLOAD 已执行" f)
    )
    (write-line "  ✗ 找不到 DLL" f)
  )

  ;; 2) 执行诊断命令
  (write-line "[2] XIMETESTKEY..." f)
  (command "_.XIMETESTKEY")

  (write-line "[3] XIMETESTCN..." f)
  (command "_.XIMETESTCN")

  (write-line "[4] XIMESTATUS..." f)
  (command "_.XIMESTATUS")

  (write-line "===== 完成 =====" f)
  (close f)
  (princ "\n[X输入法] 自动测试完成，结果见 xime_autotest_result.txt")
  (princ)
)
(c:XimeAutoTest)
(princ)
