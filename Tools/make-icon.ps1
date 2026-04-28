# Generates Resources\app.ico from a procedurally-drawn folder + arrow glyph.
# Sizes: 16, 24, 32, 48, 64, 128, 256.
# Run from repo root:  pwsh .\Tools\make-icon.ps1
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot "..\Resources\app.ico")
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-IconBitmap {
    param([int] $size)

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Background: rounded square in accent blue
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 212))
    $r  = [Math]::Max(2, [int]($size / 8))
    $rect = New-Object System.Drawing.Rectangle 0, 0, ($size - 1), ($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bg, $path)

    # Folder shape (white)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $pad = [int]($size * 0.18)
    $folderTop = [int]($size * 0.34)
    $folderBottom = $size - $pad
    $folderLeft = $pad
    $folderRight = $size - $pad
    $tabRight = $folderLeft + [int](($folderRight - $folderLeft) * 0.45)
    $tabHeight = [int]($size * 0.10)

    $fp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fp.AddLine($folderLeft, ($folderTop - $tabHeight), $tabRight, ($folderTop - $tabHeight))
    $fp.AddLine($tabRight, ($folderTop - $tabHeight), ($tabRight + $tabHeight), $folderTop)
    $fp.AddLine(($tabRight + $tabHeight), $folderTop, $folderRight, $folderTop)
    $fp.AddLine($folderRight, $folderTop, $folderRight, $folderBottom)
    $fp.AddLine($folderRight, $folderBottom, $folderLeft, $folderBottom)
    $fp.CloseFigure()
    $g.FillPath($white, $fp)

    # Arrow inside folder (down-arrow glyph)
    if ($size -ge 24) {
        $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 212))
        $cx = ($folderLeft + $folderRight) / 2
        $cy = ($folderTop + $folderBottom) / 2
        $w  = [int](($folderRight - $folderLeft) * 0.16)
        $h  = [int](($folderBottom - $folderTop) * 0.55)
        $shaft = New-Object System.Drawing.Rectangle ($cx - $w / 2), ($cy - $h / 2), $w, ($h * 2 / 3)
        $g.FillRectangle($accent, $shaft)
        $tip = New-Object 'System.Drawing.Point[]' 3
        $tip[0] = New-Object System.Drawing.Point ($cx - $w * 1.6), ($cy + $h / 6)
        $tip[1] = New-Object System.Drawing.Point ($cx + $w * 1.6), ($cy + $h / 6)
        $tip[2] = New-Object System.Drawing.Point $cx, ($cy + $h / 2)
        $g.FillPolygon($accent, $tip)
    }

    $g.Dispose()
    return $bmp
}

# Build the .ico container by hand (Windows ICO format)
$pngStreams = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,@{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose()
    $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out

# ICONDIR
$bw.Write([uint16]0)                     # reserved
$bw.Write([uint16]1)                     # type = 1 (icon)
$bw.Write([uint16]$pngStreams.Count)     # count

# Compute offsets
$dirSize = 6 + 16 * $pngStreams.Count
$offset = $dirSize

foreach ($p in $pngStreams) {
    $w = $p.Size; $h = $p.Size
    $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
    $bw.Write([byte]($(if ($h -ge 256) { 0 } else { $h })))
    $bw.Write([byte]0)   # color count
    $bw.Write([byte]0)   # reserved
    $bw.Write([uint16]1) # planes
    $bw.Write([uint16]32) # bpp
    $bw.Write([uint32]$p.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngStreams) { $bw.Write($p.Bytes) }

$bw.Flush()

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $dir | Join-Path -ChildPath (Split-Path -Leaf $OutputPath)), $out.ToArray())

Write-Host "Wrote icon to $OutputPath ($($out.Length) bytes, $($pngStreams.Count) sizes)"
