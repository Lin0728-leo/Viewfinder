param([switch]$Silent)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Join-Path $dir 'DshShot.dll')))
$type = $asm.GetType('DshShot.Program')
$type.GetField('BaseDir').SetValue($null, $dir)
if ($Silent) { $type.GetField('Silent').SetValue($null, $true) }
$type.GetMethod('Run').Invoke($null, @())
