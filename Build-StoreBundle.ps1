# 宿主脚本：默认发布自包含 + 裁剪版（.NET 运行时进包并裁剪），
# 捆绑包约 16 MB，用户无需预装 .NET。
# 加 -FullRuntime 后改为自包含但不裁剪（原生完整运行时），捆绑包约 88 MB。
# 不再提供框架依赖方式：.NET 5+ 没有 MSIX 框架包，商店不会代为安装。
param(
    [switch]$FullRuntime
)

$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Configuration = 'Release'
$StorePackages = Join-Path $ProjectDir 'StorePackages'

# 图标资源全部由 Tools\Generate-Assets.ps1 从 Assets\AppIcon.png 派生。
# 必须在打包前重新生成，否则磁贴/托盘会沿用旧的浅色或缺失尺寸的图。
Write-Host 'Generating icon assets...'
& (Join-Path $ProjectDir 'Tools\Generate-Assets.ps1') -AssetsDirectory (Join-Path $ProjectDir 'Assets')

# 校验：清单引用的每个图标都必须存在，且 .ico 必须覆盖小尺寸（否则托盘图标会模糊）。
$manifestPath = Join-Path $ProjectDir 'Package.appxmanifest'
$manifest = [xml](Get-Content $manifestPath -Raw)
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$Version = $manifest.SelectSingleNode('/p:Package/p:Identity', $ns).Version

$requiredAssets = @(
    $manifest.SelectNodes('//uap:VisualElements/@Square150x150Logo | //uap:VisualElements/@Square44x44Logo | //uap:DefaultTile/@Wide310x150Logo | //uap:SplashScreen/@Image', $ns)
) | ForEach-Object { $_.Value }
$requiredAssets += $manifest.SelectSingleNode('/p:Package/p:Properties/p:Logo', $ns).InnerText
$requiredAssets += 'Assets\AppIcon.ico', 'Assets\AppIconUnlocked.ico'

foreach ($asset in $requiredAssets) {
    $path = Join-Path $ProjectDir $asset
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Manifest asset missing: $asset"
    }
}

foreach ($ico in 'Assets\AppIcon.ico', 'Assets\AppIconUnlocked.ico') {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $ProjectDir $ico))
    $count = [BitConverter]::ToUInt16($bytes, 4)
    if ($count -eq 0) {
        throw "$ico contains no image entries."
    }
    $sizes = @(for ($i = 0; $i -lt $count; $i++) {
            $dimension = $bytes[6 + 16 * $i]
            if ($dimension -eq 0) { 256 } else { $dimension }
        })
    if ($sizes -notcontains 16) {
        throw "$ico has no 16x16 entry; small shell icons will be blurry."
    }
    Write-Host "  $ico -> $($sizes -join '/') px"
}

$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter MakeAppx.exe -ErrorAction Stop |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object { if ($_.FullName -match '\\(\d+\.\d+\.\d+\.\d+)\\') { [version]$matches[1] } else { [version]'0.0.0.0' } } -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $makeappx) {
    throw 'MakeAppx.exe not found in NuGet package cache.'
}

& dotnet restore (Join-Path $ProjectDir 'VolumeLock.csproj')
if ($LASTEXITCODE -ne 0) {
    throw 'Restore failed.'
}

$architectures = @(
    @{ Name = 'x64';   Platform = 'x64' },
    @{ Name = 'arm64'; Platform = 'ARM64' }
)

foreach ($arch in $architectures) {
    $name = $arch.Name

    Write-Host "Publishing $name..."

    & dotnet clean (Join-Path $ProjectDir 'VolumeLock.csproj') `
        -c $Configuration `
        "-p:Platform=$($arch.Platform)" `
        "-p:RuntimeIdentifier=win-$name" | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Clean failed for $name."
    }

    # 默认由 csproj 产出自包含 + 裁剪版（按架构约 8 MB，捆绑包约 16 MB）。
    # -FullRuntime 改为自包含但不裁剪（按架构约 45 MB），发布时不需要显式传
    # SelfContained/PublishTrimmed，避免覆盖 csproj 里的联动逻辑。
    $runtimeArg = if ($FullRuntime) { '-p:FullRuntime=true' } else { '-p:FullRuntime=false' }
    & dotnet publish (Join-Path $ProjectDir 'VolumeLock.csproj') `
        -c $Configuration `
        "-p:Platform=$($arch.Platform)" `
        "-p:RuntimeIdentifier=win-$name" `
        $runtimeArg `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageSigningEnabled=false `
        "-p:AppxPackageDir=$StorePackages\$name\"

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $name."
    }
}

$bundleInput = Join-Path $StorePackages 'bundle-input'
New-Item -ItemType Directory -Force $bundleInput | Out-Null
Get-ChildItem $bundleInput -Filter *.msix -File | ForEach-Object {
    cmd /c del /q "$($_.FullName)" | Out-Null
}

foreach ($arch in $architectures) {
    $name = $arch.Name
    $autoBundle = Join-Path $StorePackages "$name\VolumeLock_${Version}_Test\VolumeLock_${Version}_${name}.msixbundle"
    $unbundleDir = Join-Path $StorePackages "payload-$name"

    & $makeappx unbundle /o /p $autoBundle /d $unbundleDir | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unbundle failed for $name."
    }

    Copy-Item -Force (Join-Path $unbundleDir '*.msix') $bundleInput
}

$outputBundle = Join-Path $StorePackages "VolumeLock_${Version}_x64_arm64.msixbundle"
& $makeappx bundle /o /bv $Version /d $bundleInput /p $outputBundle | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Bundle creation failed.'
}

Write-Host "Created: $outputBundle"
