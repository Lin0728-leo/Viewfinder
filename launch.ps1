param([switch]$Silent)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $dir 'DshShot.dll'

# 允许直接 clone 后运行：没有编译产物就先编译一次（Win10/11 自带 .NET Framework 4.x）
if (-not (Test-Path $dll)) {
    try { & (Join-Path $dir 'build.ps1') } catch { }
}
if (-not (Test-Path $dll)) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            "DshShot.dll is missing and auto-build failed.`r`nRun build.ps1 manually, then start again.",
            "DSH") | Out-Null
    } catch { }
    exit 1
}

$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll))
$type = $asm.GetType('DshShot.Program')
$type.GetField('BaseDir').SetValue($null, $dir)
if ($Silent) { $type.GetField('Silent').SetValue($null, $true) }
$type.GetMethod('Run').Invoke($null, @())
