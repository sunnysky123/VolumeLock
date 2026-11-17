#!/usr/bin/env pwsh
<#
.SYNOPSIS
	重新生成 Assets 目录下的全部图片资源（磁贴、启动画面、商店徽标、任务栏/托盘图标）。

.DESCRIPTION
	所有资源都从 1024x1024 的品牌母版 Assets\AppIcon.png 派生，保证各尺寸、
	各资源限定符（scale / targetsize / altform）变体与品牌图标完全一致。

	生成内容：
	  * Square44x44Logo   —— scale-100/125/150/200/400 + targetsize-*
							 （各带默认、altform-unplated、altform-lightunplated）
	  * Square150x150Logo —— scale-100/125/150/200/400
	  * Wide310x150Logo   —— scale-100/125/150/200/400
	  * SplashScreen      —— scale-100/125/150/200/400
	  * StoreLogo         —— scale-100/125/150/200/400
	  * LockScreenLogo    —— scale-100/200/400
	  * AppIcon.ico / AppIconUnlocked.ico —— 16/20/24/32/40/48/64/128/256 多尺寸图标
	  * AppIconUnlocked.png —— 与锁形图标同源的"未锁定"品牌图

	母版本身（AppIcon.png）不会被覆盖。

.PARAMETER AssetsDirectory
	Assets 目录路径，默认 <仓库根>\Assets。
#>
[CmdletBinding()]
param(
	[string] $AssetsDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

if (-not $AssetsDirectory) {
	$AssetsDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
}
if (-not (Test-Path -LiteralPath (Join-Path $AssetsDirectory 'AppIcon.png'))) {
	throw "找不到品牌母版：$(Join-Path $AssetsDirectory 'AppIcon.png')"
}

# ---------------------------------------------------------------------------
# 母版几何参数（单位：母版像素，坐标系原点为母版左上角）
# ---------------------------------------------------------------------------
$BrandFile   = Join-Path $AssetsDirectory 'AppIcon.png'
$PanelCrop   = [System.Drawing.Rectangle]::new(40, 40, 944, 944)
$PanelRadius = 160.0 / 944.0      # 圆角半径与面板边长之比
$BodyCrop    = [System.Drawing.Rectangle]::new(250, 440, 524, 420)
$ArchCrop    = [System.Drawing.Rectangle]::new(250, 195, 524, 215)
$GlyphLeft   = 250.0
$GlyphTop    = 203.0
$GlyphWidth  = 524.0
$GlyphHeight = 647.0
$BodyTop     = 467.0              # 锁体顶边（母版坐标）
$BodyHeight  = 383.0
$BodyRadius  = 45.0
$GradientFrom = [System.Drawing.Color]::FromArgb(255, 0x30, 0x7C, 0xE7)
$GradientTo   = [System.Drawing.Color]::FromArgb(255, 0x4B, 0x29, 0x8A)
$HoleColor    = [System.Drawing.Color]::FromArgb(255, 0x2B, 0x3A, 0x80)

# 从"亮度→alpha"的线性映射：alpha = A*avg + B，avg 为 R/G/B 均值（0..1）
# 母版中背景最亮处 avg≈0.48，字形为纯白 avg=1，用 140/115 保证两侧都有余量。
$MaskA = 255.0 / 115.0
$MaskB = -140.0 / 115.0
$WhiteFactor  = $MaskA / 3.0
$WhiteBias    = $MaskB
$InvertFactor = -$WhiteFactor
$InvertBias   = 1.0 - $MaskB

$script:Brand = [System.Drawing.Image]::FromFile($BrandFile)

# ---------------------------------------------------------------------------
# 基础绘图工具
# ---------------------------------------------------------------------------
function New-CanvasBitmap {
	param([int] $Width, [int] $Height)
	return [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
}

function New-CanvasGraphics {
	param([System.Drawing.Bitmap] $Bitmap)
	$g = [System.Drawing.Graphics]::FromImage($Bitmap)
	$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)
	return $g
}

function New-RoundedPath {
	param([single] $X, [single] $Y, [single] $Width, [single] $Height, [single] $Radius)
	$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
	$limit = [Math]::Min($Width, $Height) / 2
	if ($Radius -gt $limit) { $Radius = $limit }
	if ($Radius -gt 0) {
		# AddRoundedRectangle 的 Size 参数是圆角椭圆的直径，不是半径
		$path.AddRoundedRectangle([System.Drawing.RectangleF]::new($X, $Y, $Width, $Height),
			[System.Drawing.SizeF]::new($Radius * 2, $Radius * 2))
	}
	else {
		$path.AddRectangle([System.Drawing.RectangleF]::new($X, $Y, $Width, $Height))
	}
	return $path
}

function New-MaskBitmap {
	param(
		[System.Drawing.Rectangle] $Crop,
		[double] $ChannelFactor,
		[double] $AlphaBias,
		[System.Drawing.Color] $Rgb,
		[System.Drawing.Drawing2D.GraphicsPath] $ClipPath
	)
	$bmp = New-CanvasBitmap $Crop.Width $Crop.Height
	$g = New-CanvasGraphics $bmp
	if ($ClipPath) { $g.SetClip($ClipPath) }

	$matrix = [System.Drawing.Imaging.ColorMatrix]::new()
	$matrix.Matrix00 = 0; $matrix.Matrix10 = 0; $matrix.Matrix20 = 0; $matrix.Matrix30 = 0
	$matrix.Matrix40 = $Rgb.R / 255.0
	$matrix.Matrix01 = 0; $matrix.Matrix11 = 0; $matrix.Matrix21 = 0; $matrix.Matrix31 = 0
	$matrix.Matrix41 = $Rgb.G / 255.0
	$matrix.Matrix02 = 0; $matrix.Matrix12 = 0; $matrix.Matrix22 = 0; $matrix.Matrix32 = 0
	$matrix.Matrix42 = $Rgb.B / 255.0
	$matrix.Matrix03 = $ChannelFactor; $matrix.Matrix13 = $ChannelFactor; $matrix.Matrix23 = $ChannelFactor
	$matrix.Matrix33 = 0
	$matrix.Matrix43 = $AlphaBias

	$attributes = [System.Drawing.Imaging.ImageAttributes]::new()
	$attributes.SetColorMatrix($matrix)
	$dest = [System.Drawing.Rectangle]::new(0, 0, $Crop.Width, $Crop.Height)
	$g.DrawImage($script:Brand, $dest, $Crop.X, $Crop.Y, $Crop.Width, $Crop.Height,
		[System.Drawing.GraphicsUnit]::Pixel, $attributes)

	$attributes.Dispose()
	$g.Dispose()
	return $bmp
}

function Fill-DiagonalGradient {
	param([System.Drawing.Graphics] $Graphics, [int] $Width, [int] $Height)
	$rect = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
	$brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
		$rect, $GradientFrom, $GradientTo, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
	$Graphics.FillRectangle($brush, $rect)
	$brush.Dispose()
}

# 圆角矩形渐变面板（FillPath 受 AntiAlias 影响，圆角边缘平滑）
function Add-GradientPanel {
	param([System.Drawing.Graphics] $Graphics, [single] $Width, [single] $Height, [single] $Radius)
	$rect = [System.Drawing.RectangleF]::new(0, 0, $Width, $Height)
	$brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
		$rect, $GradientFrom, $GradientTo, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
	$path = New-RoundedPath 0 0 $Width $Height $Radius
	$Graphics.FillPath($brush, $path)
	$path.Dispose()
	$brush.Dispose()
}

# ---------------------------------------------------------------------------
# 字形遮罩（白色锁体 + 深色音量镂空 + 锁梁），只生成一次
# ---------------------------------------------------------------------------
# 镂空必须限制在锁体内部：向内收缩 1.5px，避免锁体描边处出现一圈暗边。
$script:HoleClip = New-RoundedPath 1.5 ($BodyCrop.Y - $BodyTop + 1.5) ($GlyphWidth - 3) ($BodyHeight - 3) ($BodyRadius - 1.5)

$script:BodyMask = New-MaskBitmap -Crop $BodyCrop -ChannelFactor $WhiteFactor -AlphaBias $WhiteBias `
	-Rgb ([System.Drawing.Color]::White)
$script:HoleMask = New-MaskBitmap -Crop $BodyCrop -ChannelFactor $InvertFactor -AlphaBias $InvertBias `
	-Rgb $HoleColor -ClipPath $script:HoleClip
$script:ArchMask = New-MaskBitmap -Crop $ArchCrop -ChannelFactor $WhiteFactor -AlphaBias $WhiteBias `
	-Rgb ([System.Drawing.Color]::White)

# ---------------------------------------------------------------------------
# 组合绘制
# ---------------------------------------------------------------------------
function Add-Glyph {
	param(
		[System.Drawing.Graphics] $Graphics,
		[single] $CenterX,
		[single] $CenterY,
		[single] $Height,
		[single] $ArchOffsetX = 0,
		[single] $ArchOffsetY = 0,
		[switch] $Silhouette
	)
	$scale = $Height / $GlyphHeight
	$width = $GlyphWidth * $scale
	$left = $CenterX - $width / 2
	$top = $CenterY - $Height / 2

	$source = [System.Drawing.RectangleF]::new(0, 0, $BodyCrop.Width, $BodyCrop.Height)
	$bodyRect = [System.Drawing.RectangleF]::new(
		$left, $top + ($BodyCrop.Y - $GlyphTop) * $scale, $width, $BodyCrop.Height * $scale)
	$Graphics.DrawImage($script:BodyMask, $bodyRect, $source, [System.Drawing.GraphicsUnit]::Pixel)
	# 纯色剪影时音量镂空保持透明，符合系统徽标的单色要求
	if (-not $Silhouette) {
		$Graphics.DrawImage($script:HoleMask, $bodyRect, $source, [System.Drawing.GraphicsUnit]::Pixel)
	}

	$archSource = [System.Drawing.RectangleF]::new(0, 0, $ArchCrop.Width, $ArchCrop.Height)
	$archRect = [System.Drawing.RectangleF]::new(
		$left + $ArchOffsetX,
		$top + ($ArchCrop.Y - $GlyphTop) * $scale + $ArchOffsetY,
		$width, $ArchCrop.Height * $scale)
	$Graphics.DrawImage($script:ArchMask, $archRect, $archSource, [System.Drawing.GraphicsUnit]::Pixel)
}

# 品牌圆角面板图标（用于磁贴 / 商店徽标 / .ico）
# 把品牌面板（含圆角）画到目标 Graphics 的指定方框内。
# 母版面板四角是纯白，必须靠圆角路径裁掉，否则会出现白角。
function Add-PanelIcon {
	param([System.Drawing.Graphics] $Graphics, [single] $X, [single] $Y, [int] $Size)
	$flat = New-CanvasBitmap $Size $Size
	$fg = [System.Drawing.Graphics]::FromImage($flat)
	$fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$fg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
	$fg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
	$fg.DrawImage($script:Brand, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size), $PanelCrop,
		[System.Drawing.GraphicsUnit]::Pixel)
	$fg.Dispose()

	$path = New-RoundedPath $X $Y $Size $Size ([single]($Size * $PanelRadius))
	$brush = [System.Drawing.TextureBrush]::new($flat, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
	$brush.WrapMode = [System.Drawing.Drawing2D.WrapMode]::Clamp
	# FillPath 受 SmoothingMode.AntiAlias 影响，边缘比 SetClip 平滑
	$Graphics.FillPath($brush, $path)
	$brush.Dispose(); $path.Dispose(); $flat.Dispose()
}

function New-PanelIcon {
	param([int] $Size)
	$out = New-CanvasBitmap $Size $Size
	$g = New-CanvasGraphics $out
	Add-PanelIcon -Graphics $g -X 0 -Y 0 -Size $Size
	$g.Dispose()
	return $out
}

# 纯字形图标（锁屏徽标用白色字形 + 透明背景）
function New-GlyphIcon {
	param([int] $Size)
	$bmp = New-CanvasBitmap $Size $Size
	$g = New-CanvasGraphics $bmp
	Add-Glyph -Graphics $g -CenterX ($Size / 2) -CenterY ($Size / 2) -Height ([single]($Size * 0.94)) -Silhouette
	$g.Dispose()
	return $bmp
}

# 整块渐变 + 居中字形的画布（宽磁贴）
function New-BannerArt {
	param([int] $Width, [int] $Height, [single] $GlyphRatio)
	$bmp = New-CanvasBitmap $Width $Height
	$g = New-CanvasGraphics $bmp
	Fill-DiagonalGradient -Graphics $g -Width $Width -Height $Height
	Add-Glyph -Graphics $g -CenterX ($Width / 2) -CenterY ($Height / 2) -Height ([single]($Height * $GlyphRatio))
	$g.Dispose()
	return $bmp
}

# 启动画面：透明底 + 居中白字形，背景色由清单的 SplashScreen BackgroundColor 提供。
# 若这里也画不透明渐变，会与背景色形成一圈可见的矩形边界。
function New-SplashArt {
	param([int] $Width, [int] $Height, [single] $GlyphRatio)
	$bmp = New-CanvasBitmap $Width $Height
	$g = New-CanvasGraphics $bmp
	Add-Glyph -Graphics $g -CenterX ($Width / 2) -CenterY ($Height / 2) -Height ([single]($Height * $GlyphRatio))
	$g.Dispose()
	return $bmp
}

# 未解锁状态：锁梁右移上抬，与锁体脱开
function New-UnlockedPanelIcon {
	param([int] $Size)
	$bmp = New-CanvasBitmap $Size $Size
	$g = New-CanvasGraphics $bmp
	Add-GradientPanel -Graphics $g -Width $Size -Height $Size -Radius ([single]($Size * $PanelRadius))
	$path = New-RoundedPath 0 0 $Size $Size ([single]($Size * $PanelRadius))
	$g.SetClip($path)
	$glyphScale = ($Size * 0.6843) / $GlyphHeight
	Add-Glyph -Graphics $g -CenterX ($Size / 2) -CenterY ($Size * 0.5149) -Height ([single]($Size * 0.6843)) `
		-ArchOffsetX ([single]($GlyphWidth * $glyphScale * 0.085)) `
		-ArchOffsetY ([single](-$GlyphHeight * $glyphScale * 0.055))
	$path.Dispose(); $g.Dispose()
	return $bmp
}

# ---------------------------------------------------------------------------
# 写盘
# ---------------------------------------------------------------------------
$script:Written = [System.Collections.Generic.List[string]]::new()

function Save-Asset {
	param([System.Drawing.Bitmap] $Bitmap, [string] $FileName)
	$target = Join-Path $AssetsDirectory $FileName
	$Bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
	$Bitmap.Dispose()
	$script:Written.Add($FileName)
	Write-Host ("  {0,-62} {1}" -f $FileName, (Get-Item -LiteralPath $target).Length)
}

function Get-IcoDibBytes {
	param([System.Drawing.Bitmap] $Bitmap)
	$w = $Bitmap.Width
	$h = $Bitmap.Height
	$flat = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($flat)
	$g.Clear([System.Drawing.Color]::Transparent)
	$g.DrawImage($Bitmap, 0, 0, $w, $h)
	$g.Dispose()

	$locked = $flat.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h),
		[System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$stride = $locked.Stride
	$buffer = New-Object byte[] ($stride * $h)
	[System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $buffer, 0, $buffer.Length)
	$flat.UnlockBits($locked)
	$flat.Dispose()

	$andStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
	$andSize = $andStride * $h
	$pixelSize = $w * $h * 4

	$ms = [System.IO.MemoryStream]::new()
	$bw = [System.IO.BinaryWriter]::new($ms)
	$bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
	$bw.Write([int16]1); $bw.Write([int16]32)
	$bw.Write([int]0); $bw.Write([int]($pixelSize + $andSize))
	$bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
	for ($y = $h - 1; $y -ge 0; $y--) {
		$bw.Write($buffer, $y * $stride, $w * 4)
	}
	$andRow = New-Object byte[] $andStride
	for ($y = $h - 1; $y -ge 0; $y--) {
		[Array]::Clear($andRow, 0, $andStride)
		$rowBase = $y * $stride
		for ($x = 0; $x -lt $w; $x++) {
			if ($buffer[$rowBase + $x * 4 + 3] -eq 0) {
				$index = [int]([Math]::Floor($x / 8))
				$andRow[$index] = [byte]($andRow[$index] -bor (0x80 -shr ($x - 8 * $index)))
			}
		}
		$bw.Write($andRow)
	}
	$bw.Flush()
	$bytes = $ms.ToArray()
	$bw.Dispose(); $ms.Dispose()
	return ,$bytes
}

function Save-IcoAsset {
	param([string] $FileName, [int[]] $Sizes, [scriptblock] $Factory)
	$entries = @()
	foreach ($size in $Sizes) {
		$bmp = & $Factory $size
		if ($size -ge 256) {
			$ms = [System.IO.MemoryStream]::new()
			$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
			$data = $ms.ToArray()
			$ms.Dispose()
		}
		else {
			$data = Get-IcoDibBytes -Bitmap $bmp
		}
		$bmp.Dispose()
		$entries += ,@{ Size = $size; Data = $data }
	}

	$target = Join-Path $AssetsDirectory $FileName
	$fs = [System.IO.File]::Create($target)
	$bw = [System.IO.BinaryWriter]::new($fs)
	$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
	$offset = 6 + 16 * $entries.Count
	foreach ($entry in $entries) {
		$dim = $entry.Size
		if ($dim -ge 256) { $dim = 0 }
		$bw.Write([byte]$dim); $bw.Write([byte]$dim)
		$bw.Write([byte]0); $bw.Write([byte]0)
		$bw.Write([int16]1); $bw.Write([int16]32)
		$bw.Write([int]$entry.Data.Length); $bw.Write([int]$offset)
		$offset += $entry.Data.Length
	}
	foreach ($entry in $entries) { $bw.Write($entry.Data) }
	$bw.Flush(); $bw.Dispose(); $fs.Dispose()

	$script:Written.Add($FileName)
	Write-Host ("  {0,-62} {1}" -f $FileName, (Get-Item -LiteralPath $target).Length)
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
Write-Host "从品牌母版生成资源：$BrandFile`n"

# 不带限定符的默认文件就是 scale-100，因此无需再生成 scale-100 副本。
$scales = @(
	@{ Q = 'scale-125'; F = 1.25 },
	@{ Q = 'scale-150'; F = 1.5 },
	@{ Q = 'scale-200'; F = 2.0 },
	@{ Q = 'scale-400'; F = 4.0 }
)

Write-Host 'Square44x44Logo:'
Save-Asset (New-PanelIcon 44) 'Square44x44Logo.png'
foreach ($s in $scales) {
	Save-Asset (New-PanelIcon ([int][Math]::Round(44 * $s.F))) "Square44x44Logo.$($s.Q).png"
}
$targetSizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256
foreach ($ts in $targetSizes) {
	foreach ($form in 'unplated', 'lightunplated') {
		Save-Asset (New-PanelIcon $ts) "Square44x44Logo.targetsize-$ts`_altform-$form.png"
	}
}

Write-Host "`nSquare150x150Logo:"
Save-Asset (New-PanelIcon 150) 'Square150x150Logo.png'
foreach ($s in $scales) {
	Save-Asset (New-PanelIcon ([int][Math]::Round(150 * $s.F))) "Square150x150Logo.$($s.Q).png"
}

Write-Host "`nWide310x150Logo:"
Save-Asset (New-BannerArt 310 150 0.60) 'Wide310x150Logo.png'
foreach ($s in $scales) {
	Save-Asset (New-BannerArt ([int][Math]::Round(310 * $s.F)) ([int][Math]::Round(150 * $s.F)) 0.60) `
		"Wide310x150Logo.$($s.Q).png"
}

Write-Host "`nSplashScreen:"
Save-Asset (New-SplashArt 620 300 0.52) 'SplashScreen.png'
foreach ($s in $scales) {
	Save-Asset (New-SplashArt ([int][Math]::Round(620 * $s.F)) ([int][Math]::Round(300 * $s.F)) 0.52) `
		"SplashScreen.$($s.Q).png"
}

Write-Host "`nStoreLogo:"
Save-Asset (New-PanelIcon 50) 'StoreLogo.png'
foreach ($s in $scales) {
	Save-Asset (New-PanelIcon ([int][Math]::Round(50 * $s.F))) "StoreLogo.$($s.Q).png"
}

Write-Host "`nLockScreenLogo:"
foreach ($s in @(@{ Q = 'scale-100'; P = 24 }, @{ Q = 'scale-200'; P = 48 }, @{ Q = 'scale-400'; P = 96 })) {
	Save-Asset (New-GlyphIcon $s.P) "LockScreenLogo.$($s.Q).png"
}
Write-Host "`n应用与托盘图标:"
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
Save-IcoAsset -FileName 'AppIcon.ico' -Sizes $icoSizes -Factory { param($n) New-PanelIcon $n }
Save-IcoAsset -FileName 'AppIconUnlocked.ico' -Sizes $icoSizes -Factory { param($n) New-UnlockedPanelIcon $n }
Save-Asset (New-UnlockedPanelIcon 256) 'AppIconUnlocked.png'

$script:Brand.Dispose()

# 清理本脚本历史版本产出、但本次未再生成的文件（例如已并入无后缀默认文件的 scale-100 变体）
$ownedPrefixes = 'Square44x44Logo', 'Square150x150Logo', 'Wide310x150Logo', 'SplashScreen', 'StoreLogo', 'LockScreenLogo', 'AppIconUnlocked'
$stale = @(Get-ChildItem -LiteralPath $AssetsDirectory -File |
	Where-Object { $_.Extension -in '.png', '.ico' -and $_.Name -ne 'AppIcon.png' } |
	Where-Object { -not ($script:Written -ccontains $_.Name) } |
	Where-Object { $name = $_.Name; @($ownedPrefixes | Where-Object { $name.StartsWith($_, 'Ordinal') }).Count -gt 0 })
if ($stale) {
	Write-Host "`n清理过时资源:"
	foreach ($file in $stale) {
		Remove-Item -LiteralPath $file.FullName -Force
		Write-Host ("  已删除 {0}" -f $file.Name)
	}
}

Write-Host "`n共生成 $($script:Written.Count) 个资源文件 → $AssetsDirectory"
