# Build DshShot.exe with the .NET Framework 4.x C# compiler (no SDK needed).
# Generates app.ico first, then compiles DshShot.cs.
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads BOM-less UTF-8.

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------- bitmap
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [double]$size * 0.04
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ([double]$size - 2 * $pad), ([double]$size - 2 * $pad)
    $radius = [double]$size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]($radius * 2)
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 62, 118, 255),
        [System.Drawing.Color]::FromArgb(255, 24, 64, 200),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    # viewfinder corners
    $w = [single]([double]$size * 0.085)
    if ($w -lt 1) { $w = 1 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $o = [double]$size * 0.22
    $len = [double]$size * 0.15
    $segments = @(
        @($o, $o, ($o + $len), $o), @($o, $o, $o, ($o + $len)),
        @(($size - $o), $o, ($size - $o - $len), $o), @(($size - $o), $o, ($size - $o), ($o + $len)),
        @($o, ($size - $o), ($o + $len), ($size - $o)), @($o, ($size - $o), $o, ($size - $o - $len)),
        @(($size - $o), ($size - $o), ($size - $o - $len), ($size - $o)), @(($size - $o), ($size - $o), ($size - $o), ($size - $o - $len))
    )
    foreach ($s in $segments) {
        $g.DrawLine($pen, [single]$s[0], [single]$s[1], [single]$s[2], [single]$s[3])
    }
    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Get-IconPixels([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $row = New-Object byte[] ($size * 4)
    $all = New-Object byte[] ($size * $size * 4)
    for ($y = 0; $y -lt $size; $y++) {
        $src = [IntPtr]::Add($data.Scan0, $y * $stride)
        [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $size * 4)
        # ICO stores rows bottom-up
        [Array]::Copy($row, 0, $all, ($size - 1 - $y) * $size * 4, $size * 4)
    }
    $bmp.UnlockBits($data)
    return , $all
}

# ---------------------------------------------------------------- ico
$sizes = @(16, 32, 48, 64, 128, 256)
$images = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    [byte[]]$px = Get-IconPixels $bmp
    if ($px.Length -ne ($s * $s * 4)) { throw "pixel buffer size mismatch for $s" }
    $null = $images.Add(@($s, $px))
    $bmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type = icon
$bw.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $pixels = $img[1]
    $andRowBytes = [int]([Math]::Ceiling($s / 8.0))
    if ($andRowBytes % 4 -ne 0) { $andRowBytes += 4 - ($andRowBytes % 4) }
    $andBytes = New-Object byte[] ($andRowBytes * $s)
    $bytesInRes = 40 + $pixels.Length + $andBytes.Length

    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]0)               # colors
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bpp
    $bw.Write([uint32]$bytesInRes)
    $bw.Write([uint32]$offset)
    $offset += $bytesInRes
}

foreach ($img in $images) {
    $s = $img[0]; $pixels = $img[1]
    $andRowBytes = [int]([Math]::Ceiling($s / 8.0))
    if ($andRowBytes % 4 -ne 0) { $andRowBytes += 4 - ($andRowBytes % 4) }
    $andBytes = New-Object byte[] ($andRowBytes * $s)

    $bw.Write([uint32]40)            # biSize
    $bw.Write([int32]$s)             # biWidth
    $bw.Write([int32]($s * 2))       # biHeight (XOR + AND)
    $bw.Write([uint16]1)             # biPlanes
    $bw.Write([uint16]32)            # biBitCount
    $bw.Write([uint32]0)             # BI_RGB
    $bw.Write([uint32]($pixels.Length + $andBytes.Length))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write([byte[]]$pixels)
    $bw.Write($andBytes)
}
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host ("icon: app.ico ({0} bytes)" -f (Get-Item 'app.ico').Length)

# sanity check: does the OS accept it?
$ico = New-Object System.Drawing.Icon (Join-Path $PSScriptRoot 'app.ico')
Write-Host ("icon ok: {0}x{1}" -f $ico.Width, $ico.Height)
$ico.Dispose()

# ---------------------------------------------------------------- compile
$cscArgs = @(
    '/nologo', '/target:library', '/platform:anycpu', '/optimize+',
    '/codepage:65001',
    ('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')),
    ('/out:' + (Join-Path $PSScriptRoot 'DshShot.dll')),
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll',
    (Join-Path $PSScriptRoot 'src\DshShot.cs')
)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed: $LASTEXITCODE" }

$exe = Get-Item (Join-Path $PSScriptRoot 'DshShot.dll')
Write-Host ("exe : {0} ({1:N0} bytes)" -f $exe.FullName, $exe.Length)
