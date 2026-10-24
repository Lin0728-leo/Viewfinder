' DSH 取景器启动器：用 wscript 起 PowerShell（隐藏窗口，不会闪黑框）
' 带任意参数运行时视为 -Silent（开机自启，不弹设置窗）
Dim fso, sh, dir, extra
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
extra = ""
If WScript.Arguments.Count > 0 Then extra = " -Silent"
sh.Run "powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & dir & "\launch.ps1""" & extra, 0, False
