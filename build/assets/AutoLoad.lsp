<?xml version="1.0" encoding="utf-8"?>
<!--
  X输入法 自动加载配置（LISP 方式）

  用法：把本文件所在目录加入 AutoCAD 支持文件搜索路径，
  或直接在本文件中把路径改为实际安装路径后，在 acaddoc.lsp 中引用。

  加载后每次启动 AutoCAD 会自动 NETLOAD 插件。

  注意：这里不再打印「已自动加载」之类的提示 —— NETLOAD 之后插件自身会输出
  一行「[X输入法 v1.0.0] 已加载。…」，本文件再打印一句就是重复内容。
-->
(defun c:XimeLoad ( / dllPath)
  (setq dllPath (findfile "Xime.dll"))
  (if dllPath
    (command "_.NETLOAD" dllPath)
    (princ "\n[X输入法] 未找到 Xime.dll，请确认插件目录已在支持路径中。")
  )
  (princ)
)

;; 立即执行一次
(c:XimeLoad)
(princ)
