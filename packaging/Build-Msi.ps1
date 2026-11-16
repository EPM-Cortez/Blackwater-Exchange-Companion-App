[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')] [string[]] $Architectures = @('x64', 'arm64'),
    [string] $Wix = 'wix',
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\msi')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$app = Join-Path $root 'src\Blackwater.App'
$project = Join-Path $app 'Blackwater.App.csproj'
$version = ([xml] (Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The app Version must have three numeric parts.' }
$msiVersion = [version] $version
if ($msiVersion.Major -gt 255 -or $msiVersion.Minor -gt 255 -or $msiVersion.Build -gt 65535) {
    throw 'The app Version exceeds the Windows Installer version limits.'
}

$wixCommand = (Get-Command $Wix -CommandType Application).Source
$outputFolder = (New-Item -ItemType Directory -Force -Path $Output).FullName
$stage = Join-Path $outputFolder ('build-' + [Guid]::NewGuid().ToString('N'))
$authoring = Join-Path $PSScriptRoot 'BlackwaterExchange.wxs'
$appIcon = Join-Path $app 'Assets\app.ico'

New-Item -ItemType Directory -Path $stage | Out-Null
$built = foreach ($arch in ($Architectures | Select-Object -Unique)) {
    $payload = Join-Path $stage "win-$arch"
    $msi = Join-Path $stage "BlackwaterExchange-$version-$arch.msi"
    & dotnet publish $project -c Release -r "win-$arch" --self-contained true `
        -p:DebugType=none -p:SatelliteResourceLanguages=en -p:NuGetAudit=true -o $payload --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch." }
    & $wixCommand build $authoring -arch $arch -d "Version=$version" -d "PublishDirectory=$payload" `
        -d "AppIcon=$appIcon" -pdbtype none -out $msi | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WiX build failed for $arch." }
    Get-Item -LiteralPath $msi
}

$hashes = foreach ($file in $built) {
    $msi = Join-Path $outputFolder $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $msi -Force
    Write-Host $msi
    '{0}  {1}' -f (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name
}
$hashes | Set-Content -LiteralPath (Join-Path $outputFolder 'SHA256SUMS.txt') -Encoding ascii
