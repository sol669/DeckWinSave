param(
    [Parameter(Mandatory)] [string] $Iscc,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\release'),
    [string] $BuildDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('deckwinsave-release-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\DeckWinSave.csproj'
$installer = Join-Path $PSScriptRoot 'DeckWinSave-Online.iss'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$build = (New-Item -ItemType Directory -Path $BuildDirectory).FullName
$isccPath = (Get-Item -LiteralPath $Iscc).FullName

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($variant in @(
    @{ Name = 'Light'; SelfContained = 'false'; InstallerFlag = @() },
    @{ Name = 'Offline'; SelfContained = 'true'; InstallerFlag = @('/DOffline=1') }
)) {
    $payload = Join-Path $build $variant.Name
    dotnet publish $project -c Release -o $payload "-p:SelfContained=$($variant.SelfContained)" `
        "-p:WindowsAppSDKSelfContained=$($variant.SelfContained)" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($variant.Name)." }

    $archive = Join-Path $build "DeckWinSave-Portable-$($variant.Name)-v$version-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $flags = $variant.InstallerFlag
    & $isccPath /Qp "/O$build" "/DPublishDir=$payload" @flags $installer
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed for $($variant.Name)." }
}

$artifacts = @(Get-ChildItem -LiteralPath $build -File -Filter "DeckWinSave-*-v$version*")
if ($artifacts.Count -ne 4) { throw 'Expected two installers and two ZIP archives.' }
$artifacts | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $build 'SHA256SUMS.txt') -Encoding ascii
$artifacts | Copy-Item -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $build 'SHA256SUMS.txt') -Destination $output -Force
Write-Output "Build directory: $build"
Write-Output "Release directory: $output"
