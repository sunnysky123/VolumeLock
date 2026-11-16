$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Configuration = 'Release'
$StorePackages = Join-Path $ProjectDir 'StorePackages'

$manifestPath = Join-Path $ProjectDir 'Package.appxmanifest'
$manifest = [xml](Get-Content $manifestPath -Raw)
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$Version = $manifest.SelectSingleNode('/p:Package/p:Identity', $ns).Version

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

    & dotnet publish (Join-Path $ProjectDir 'VolumeLock.csproj') `
        -c $Configuration `
        "-p:Platform=$($arch.Platform)" `
        "-p:RuntimeIdentifier=win-$name" `
        -p:SelfContained=false `
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

    Copy-Item -Force (Join-Path $unbundleDir "VolumeLock_${Version}_${name}.msix") $bundleInput
}

$outputBundle = Join-Path $StorePackages "VolumeLock_${Version}_x64_arm64.msixbundle"
& $makeappx bundle /o /bv $Version /d $bundleInput /p $outputBundle | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Bundle creation failed.'
}

Write-Host "Created: $outputBundle"
