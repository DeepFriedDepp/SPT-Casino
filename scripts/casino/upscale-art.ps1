<#
.SYNOPSIS
    Resamples the casino's larger artwork up, for screens the originals do not reach.

.DESCRIPTION
    **Only the table photograph needs this, and only above 1080p.** Worked out rather
    than guessed, by comparing every shipped picture's stored size against the size it is
    actually drawn at:

        picture        stored       drawn (logical)   at 4K (physical)
        table.png      1614x975     1080 wide         2160  <- MAGNIFIED 1.34x
        cards/*.png     500x726       96 wide          192   <- minified 2.6x
        tile-*.png      320x320      104 wide          208   <- minified 1.5x
        chips/*.png     440x440       40 wide           80   <- minified 5.5x
        ball.png        256x256      ~35 wide          ~70   <- minified 3.6x

    Everything except the table is drawn smaller than it is stored even on a 4K screen,
    so giving those more pixels would make the download bigger, the minification steeper
    and the picture no better. The table is the one that runs out: the canvas scaler
    matches height against a 1920x1080 reference, so at 1440p it is break-even and at 4K
    the GPU is inventing a third of what you see.

    **This does not add detail, and cannot.** The information ceiling is whatever the
    original holds. What it buys is a better kernel than the GPU's: a high-quality
    bicubic resample offline, then ordinary minification at runtime, instead of the
    hardware stretching the original with a bilinear magnify. Sharper, not richer.

    The slot machine's symbols are NOT here and cannot be. They are the game's own item
    icons, fetched from EFT at runtime by `ItemArt.For` -- whatever resolution Tarkov
    ships is what the reels get. Trilinear filtering in Textures.FromFile is the only
    lever on those.

.PARAMETER Scale
    Multiplier. 2 is the default and takes the table to 3228x1950, which covers 4K with
    room to spare.

.PARAMETER WhatIf
    Report what would change, and by how much, without writing anything.

.EXAMPLE
    .\upscale-art.ps1 -WhatIf

.EXAMPLE
    .\upscale-art.ps1 -Scale 2
#>
param(
    [double]$Scale = 2.0,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Two levels up: this sits in scripts/<mod>/.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# The list is explicit rather than a wildcard, and that is the point of the script.
# Sweeping every PNG would upscale the cards and the chips as well, which costs megabytes
# and makes them worse -- see the description.
$targets = @(
    'src\Blackjack.Client\assets\table.png',
    'src\Poker.Client\assets\table.png'
)

if ($Scale -le 1.0) {
    Write-Host "Scale must be greater than 1. Nothing to do." -ForegroundColor Yellow
    exit 1
}

Write-Host "Upscaling the table artwork x$Scale" -ForegroundColor Cyan
Write-Host ""

$before = 0L
$after = 0L

foreach ($relative in $targets) {
    $path = Join-Path $root $relative

    if (-not (Test-Path $path)) {
        Write-Host "  missing: $relative" -ForegroundColor Red
        continue
    }

    $source = [System.Drawing.Image]::FromFile($path)

    $width = [int][Math]::Round($source.Width * $Scale)
    $height = [int][Math]::Round($source.Height * $Scale)
    $originalBytes = (Get-Item $path).Length
    $before += $originalBytes

    if ($WhatIf) {
        Write-Host ("  {0,-44} {1}x{2} -> {3}x{4}" -f $relative, $source.Width, $source.Height, $width, $height)
        $source.Dispose()
        continue
    }

    # 32bpp ARGB throughout: the table has rounded corners and a transparent surround, and
    # resampling into a format without an alpha channel turns those into black fringes.
    $target = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $target.SetResolution($source.HorizontalResolution, $source.VerticalResolution)

    $graphics = [System.Drawing.Graphics]::FromImage($target)

    # HighQualityBicubic is the best kernel in System.Drawing and the reason this is worth
    # doing at all -- the GPU's magnify is bilinear, which is the one it beats.
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Wrap mode matters at the edges. Without TileFlipXY the kernel reads transparent black
    # from beyond the border and leaves a dark halo all the way round the table.
    $wrap = New-Object System.Drawing.Imaging.ImageAttributes
    $wrap.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

    $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $graphics.DrawImage($source, $rect, 0, 0, $source.Width, $source.Height,
        [System.Drawing.GraphicsUnit]::Pixel, $wrap)

    $graphics.Dispose()
    $wrap.Dispose()
    $sourceWidth = $source.Width
    $sourceHeight = $source.Height
    $source.Dispose()

    # Written to a temporary file first: Image.FromFile holds a lock on the original until
    # it is disposed, and saving over a file still being read is how this loses artwork.
    $temporary = "$path.upscaled"
    $target.Save($temporary, [System.Drawing.Imaging.ImageFormat]::Png)
    $target.Dispose()

    Move-Item -Path $temporary -Destination $path -Force

    $newBytes = (Get-Item $path).Length
    $after += $newBytes

    Write-Host ("  {0,-44} {1}x{2} -> {3}x{4}  {5:N0} KB -> {6:N0} KB" -f `
            $relative, $sourceWidth, $sourceHeight, $width, $height, ($originalBytes / 1KB), ($newBytes / 1KB)) `
        -ForegroundColor Green
}

Write-Host ""

if ($WhatIf) {
    Write-Host "Nothing written." -ForegroundColor Yellow
    exit 0
}

Write-Host ("Artwork went from {0:N1} MB to {1:N1} MB on disk." -f ($before / 1MB), ($after / 1MB)) -ForegroundColor Cyan
Write-Host "Only one copy ships: pack.ps1 merges every game's assets into one plugin folder." -ForegroundColor DarkGray
