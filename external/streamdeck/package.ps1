# makes the StreamDeck plugin bits into a .streamDeckPlugin file locally

Add-Type -AssemblyName System.Drawing

$pluginDir = Join-Path $PSScriptRoot "com.wolfwithsword.subathonmanager.sdPlugin"
$imagesDir = Join-Path $pluginDir "images"
$sourceIcon = Join-Path $PSScriptRoot "../../assets/icon.png"
$output = Join-Path $PSScriptRoot "SubathonManager_StreamDeck.streamDeckPlugin"

if (-not (Test-Path $sourceIcon)) { throw "Missing $sourceIcon" }
New-Item -ItemType Directory -Force $imagesDir | Out-Null

function New-ResizedIcon([string]$name, [int]$size) {
    $src = [System.Drawing.Image]::FromFile((Resolve-Path $sourceIcon))
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $src.Dispose()
    $bmp.Save((Join-Path $imagesDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

New-ResizedIcon "pluginIcon.png"      28
New-ResizedIcon "pluginIcon@2x.png"   56
New-ResizedIcon "categoryIcon.png"    28
New-ResizedIcon "categoryIcon@2x.png" 56
New-ResizedIcon "actionIcon.png"      20
New-ResizedIcon "actionIcon@2x.png"   40
New-ResizedIcon "keyIcon.png"         72
New-ResizedIcon "keyIcon@2x.png"      144

if (Test-Path $output) { Remove-Item $output -Force }

Compress-Archive -Path $pluginDir -DestinationPath "$output.zip"
Move-Item "$output.zip" $output -Force

Write-Host "Wrote $output"
