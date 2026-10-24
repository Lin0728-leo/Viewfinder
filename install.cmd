@echo off
rem 首次使用：生成快捷方式并启动（快捷方式里存的是本目录的绝对路径，换机器重新跑一次即可）
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 pause
