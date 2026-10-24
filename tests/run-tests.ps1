$ErrorActionPreference = 'Stop'

# Build DshShot.dll if needed, compile the self-contained test host, run it.
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads BOM-less UTF-8.

$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'DshShot.dll'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

if (-not (Test-Path $dll)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }
}

$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$testExe = Join-Path $out 'DshShot.Tests.exe'

$cscArgs = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/codepage:65001',
    ('/out:' + $testExe),
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll',
    ('/r:' + $dll),
    (Join-Path $PSScriptRoot 'DshShot.Tests.cs')
)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "test compile failed: $LASTEXITCODE" }

Copy-Item -LiteralPath $dll -Destination $out -Force
& $testExe
exit $LASTEXITCODE
