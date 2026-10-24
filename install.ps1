param(
    [switch]$NoLaunch,
    [switch]$Desktop
)

# 在程序目录里生成快捷方式（目标/参数/图标全部按当前所在位置计算，不含任何固定盘符）
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$dll = Join-Path $dir 'DshShot.dll'

# 仓库不再提交 DLL：首次安装或源码比 DLL 新时先编译（Win10/11 自带 .NET Framework 4.x）
$needBuild = -not (Test-Path $dll)
if (-not $needBuild) {
    $src = Join-Path $dir 'src'
    if (Test-Path $src) {
        $newest = Get-ChildItem -LiteralPath $src -Filter '*.cs' |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($newest -and $newest.LastWriteTimeUtc -gt (Get-Item $dll).LastWriteTimeUtc) { $needBuild = $true }
    }
}
if ($needBuild) {
    Write-Host '正在编译 DshShot.dll（首次运行需要，约 1-2 秒）...'
    & (Join-Path $dir 'build.ps1')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) {
        throw '编译失败，请查看上面的错误信息。'
    }
}

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
