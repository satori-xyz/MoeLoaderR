[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repo 'MoeLoaderR.Wpf\bin\installer-payload'
$outputDir = Join-Path $PSScriptRoot 'bin'
[xml]$properties = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $repo '.local-work\installer-tools\Inno Setup\ISCC.exe')
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Install Inno Setup 6.5 or later, or supply -IsccPath with the full path to ISCC.exe.'
}

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repo 'MoeLoaderR.Wpf\MoeLoaderR.Wpf.csproj') '-p:PublishProfile=FolderProfile' "-p:PublishDir=$publishDir"
    if ($LASTEXITCODE -ne 0) { throw 'Publishing MoeLoaderR failed.' }
}
$payload = Join-Path $publishDir 'MoeLoaderR.exe'
if (-not (Test-Path -LiteralPath $payload)) { throw "Published executable missing: $payload" }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($payload).FileVersion
if ([version]$fileVersion -ne [version]"$version.0") {
    throw "Payload version $fileVersion does not match project version $version. Rebuild without -SkipPublish."
}
New-Item -ItemType Directory -Force $outputDir | Out-Null
& $IsccPath "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" (Join-Path $PSScriptRoot 'MoeLoaderR.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }
Get-Item -LiteralPath (Join-Path $outputDir "MoeLoaderR-$version-Setup.exe")
