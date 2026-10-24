@echo off
rem 通过 wscript 启动，避免留下 PowerShell 黑框；双击 .cmd 自身仍会闪一下命令行
start "" wscript.exe //nologo "%~dp0DshShot.vbs"
