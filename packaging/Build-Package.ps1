<#
.SYNOPSIS
Builds the Blackwater Exchange MSIX packages and the bundle to upload to the Microsoft Store.

.DESCRIPTION
For each architecture: publishes the app self-contained (the Store does not install .NET), copies the logos, fills
in AppxManifest.xml, indexes the logos with makepri (so Windows picks the right size), and packs an .msix with
makeappx. Then bundles them into one .msixbundle for Partner Center, which signs it.

Use the identity from Partner Center (Product management, Product identity) for a Store build. The defaults make
a test package that installs only with -Register on a PC in Developer Mode.

.EXAMPLE
.\packaging\Build-Package.ps1
.\packaging\Build-Package.ps1 -IdentityName 12345Company.BlackwaterExchange -Publisher 'CN=ABCD1234-...' -PublisherDisplayName 'Company Ltd'
.\packaging\Build-Package.ps1 -Architectures x64 -Register
#>
[CmdletBinding()]
param(
    [string] $IdentityName = 'BlackwaterExchange.Test',
    [string] $Publisher = 'CN=Blackwater Exchange Test',
    [string] $PublisherDisplayName = 'Blackwater Exchange',
    [ValidateSet('x64', 'arm64')] [string[]] $Architectures = @('x64', 'arm64'),
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\package'),
    # Installs the x64 layout for this Windows user, unsigned. Needs Developer Mode (Settings, System, For developers).
    [switch] $Register
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\Blackwater.App\Blackwater.App.csproj'
$assets = Join-Path $root 'src\Blackwater.App\Assets'

# Newest Windows SDK tools.
$kit = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } | Sort-Object { [version] $_.Name } | Select-Object -Last 1
if (-not $kit) { throw 'The Windows SDK is not installed (makeappx.exe and makepri.exe come with it).' }
$makeappx = Join-Path $kit.FullName 'x64\makeappx.exe'
$makepri = Join-Path $kit.FullName 'x64\makepri.exe'

# Store versions are four numbers and the last must be 0.
$version = ([xml] (Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Blackwater.App.csproj's Version '$version' must look like 1.2.3." }
$packageVersion = "$version.0"
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'IdentityName is not a valid package name.' }

$output = (New-Item -ItemType Directory -Force -Path $Output).FullName
$stage = Join-Path $output ('build-' + [Guid]::NewGuid().ToString('N'))
$bundleInput = Join-Path $stage 'bundle-input'
New-Item -ItemType Directory -Force -Path $bundleInput | Out-Null
$template = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw

function Escape([string] $text) { [Security.SecurityElement]::Escape($text) }

foreach ($arch in $Architectures) {
    Write-Host "== $arch"
    $layout = Join-Path $stage "layout-$arch"
    & dotnet publish $project -c Release -r "win-$arch" --self-contained true -p:DebugType=none -p:SatelliteResourceLanguages=en -p:GenerateDocumentationFile=false -o $layout --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch." }

    New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets') | Out-Null
    Copy-Item (Join-Path $assets '*.png') (Join-Path $layout 'Assets')

    $manifest = $template.Replace('$IdentityName$', (Escape $IdentityName)).Replace('$Publisher$', (Escape $Publisher)).
        Replace('$PublisherDisplayName$', (Escape $PublisherDisplayName)).Replace('$Version$', $packageVersion).
        Replace('$Architecture$', $arch)
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    # resources.pri maps Assets\Square44x44Logo.png to the scale and size variants.
    $config = Join-Path $stage "priconfig-$arch.xml"
    & $makepri createconfig /cf $config /dq en-GB /pv 10.0.0 /o | Out-Null
    # The default config splits other scales into resource packs, which a single package never installs. Keep
    # every scale in the one resources.pri.
    $priConfig = [xml] (Get-Content $config -Raw)
    $packaging = $priConfig.SelectSingleNode('//packaging')
    if ($packaging) { [void] $packaging.ParentNode.RemoveChild($packaging) }
    $priConfig.Save($config)
    & $makepri new /pr $layout /cf $config /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri failed for $arch." }

    $msix = Join-Path $bundleInput "BlackwaterExchange_${packageVersion}_$arch.msix"
    & $makeappx pack /d $layout /p $msix /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch. Run it again without '| Out-Null' to see why." }
    Write-Host ("   {0} ({1:N1} MB)" -f (Split-Path $msix -Leaf), ((Get-Item $msix).Length / 1MB))
}

$bundleName = "BlackwaterExchange_$packageVersion.msixbundle"
$stagedBundle = Join-Path $stage $bundleName
& $makeappx bundle /d $bundleInput /p $stagedBundle /bv $packageVersion /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }
$bundle = Join-Path $output $bundleName
Copy-Item -LiteralPath $stagedBundle -Destination $bundle -Force
Write-Host ("Bundle: {0} ({1:N1} MB)" -f $bundle, ((Get-Item $bundle).Length / 1MB))
Write-Host "Upload the .msixbundle in Partner Center (Packages). The Store signs it."

if ($Register) {
    $unlock = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
    if ($unlock.AllowDevelopmentWithoutDevLicense -ne 1) {
        throw 'Installing an unsigned test package needs Developer Mode: Settings, System, For developers.'
    }
    $layout = Join-Path $stage 'layout-x64'
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceUpdateFromAnyVersion
    Write-Host "Installed for this user from $layout. Start it from the Start menu; remove it with:"
    Write-Host "  Get-AppxPackage $IdentityName | Remove-AppxPackage"
}
