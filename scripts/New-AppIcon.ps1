$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path $PSScriptRoot '..\src\NetworkProfileManager.App\Assets'
New-Item -ItemType Directory -Path $assetPath -Force | Out-Null
$logoPath = Join-Path $assetPath 'logo.png'
if (!(Test-Path -LiteralPath $logoPath)) {
    throw "Missing brand logo at $logoPath"
}

$source = [System.Drawing.Bitmap]::new($logoPath)
$sizes = @(16, 32, 48, 64, 256)
$frames = @()

try {
    foreach ($size in $sizes) {
        $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += [PSCustomObject]@{
            Size = $size
            Bytes = $ms.ToArray()
        }
        $ms.Dispose()
        $bmp.Dispose()
    }
}
finally {
    $source.Dispose()
}

$icoPath = Join-Path $assetPath 'app.ico'
$file = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = [uint32](6 + 16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimByte = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimByte)
        $writer.Write($dimByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += [uint32]$frame.Bytes.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Bytes)
    }
}
finally {
    $writer.Dispose()
}
