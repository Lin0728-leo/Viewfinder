param(
    [switch]$NoLaunch,
    [switch]$Desktop
)

# 在程序目录里生成快捷方式（目标/参数/图标全部按当前所在位置计算，不含任何固定盘符）
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'

function New-Shortcut([string]$path) {
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($path)
    $s.TargetPath = $wscript
    $s.Arguments = '"' + (Join-Path $dir 'DshShot.vbs') + '"'
    $s.WorkingDirectory = $dir
    $s.IconLocation = (Join-Path $dir 'app.ico') + ',0'
    $s.WindowStyle = 1
    $s.Description = 'DSH 取景器'
    $s.Save()
}

$lnk = Join-Path $dir 'DSH 取景器.lnk'
New-Shortcut $lnk
Write-Host "已创建快捷方式：$lnk"

if ($Desktop) {
    $desk = [Environment]::GetFolderPath('Desktop')
    $deskLnk = Join-Path $desk 'DSH 取景器.lnk'
    New-Shortcut $deskLnk
    Write-Host "已创建桌面快捷方式：$deskLnk"
}

if (-not $NoLaunch) {
    Start-Process $lnk
}
