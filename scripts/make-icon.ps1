# Regenerates src/GmailWatcher.Desktop/Assets/GmailWatcher.ico (multi-size, PNG-compressed entries).
# The design, a sibling of Repo Watch's: a deep-space rounded square, a cyan orbit ring and an envelope (the mail) at its center.
# Windows only (System.Drawing). Run from the repository root:  pwsh scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 64.0

    # Rounded square background with a vertical navy gradient.
    $radius = 14 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [System.Drawing.RectangleF]::new(2 * $s, 2 * $s, 60 * $s, 60 * $s)
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90); $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0x14, 0x24, 0x40)), ([System.Drawing.Color]::FromArgb(255, 0x06, 0x0A, 0x14)), 90.0
    $g.FillPath($fill, $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 0x3F, 0xE0, 0xFF)), ([Math]::Max(1, 1.5 * $s))), $path)

    # Orbit ring.
    $cyan = [System.Drawing.Color]::FromArgb(255, 0x3F, 0xE0, 0xFF)
    $ring = New-Object System.Drawing.Pen $cyan, ([Math]::Max(1.2, 3.0 * $s))
    $g.DrawEllipse($ring, 11 * $s, 11 * $s, 42 * $s, 42 * $s)

    # Envelope: a filled rounded body with the flap drawn as a V in the background color.
    $env = [System.Drawing.RectangleF]::new(19 * $s, 22 * $s, 26 * $s, 20 * $s)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $cyan), $env)
    $flap = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x13, 0x22)), ([Math]::Max(1, 2.2 * $s))
    $flap.LineJoin = 'Round'
    $g.DrawLines($flap, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(20.5 * $s, 24 * $s), [System.Drawing.PointF]::new(32 * $s, 33.5 * $s), [System.Drawing.PointF]::new(43.5 * $s, 24 * $s)))

    # A small satellite on the ring, in the status green.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3D, 0xFF, 0xA2))), 45 * $s, 10 * $s, 8 * $s, 8 * $s)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))); $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
$target = Join-Path (Get-Location) 'src/GmailWatcher.Desktop/Assets/GmailWatcher.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
# A PNG preview next to it makes the design easy to review (not part of the build).
$previewPath = Join-Path (Get-Location) 'artifacts/icon-preview.png'
New-Item -ItemType Directory -Force (Split-Path $previewPath) | Out-Null
[System.IO.File]::WriteAllBytes($previewPath, $images[-1])
"Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', ')); preview: $previewPath"
