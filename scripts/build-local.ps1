<#
.SYNOPSIS
  Builds ArtGander on this computer for local testing (no GitHub Actions needed).

.DESCRIPTION
  Runs the tests, then publishes the app the same way the release build does.
  Windows: an optimized Release build in artifacts\local\win-<arch>\ArtGander.App.exe, plus an
           installer in artifacts\local\installers if Inno Setup 6 is installed (or with -Installer).
  macOS:   a Mac Catalyst app (needs the Xcode version that .NET requires) and, with -Installer, a DMG.

.EXAMPLE
  ./scripts/build-local.ps1               # tests + optimized Release app
  ./scripts/build-local.ps1 -Fast         # quick Debug build, no tests, no trimming
  ./scripts/build-local.ps1 -Installer    # also build the installer (Windows: needs Inno Setup 6)
  ./scripts/build-local.ps1 -Run          # start the app after building
#>
param(
    [switch]$Fast,
    [switch]$Installer,
    [switch]$SkipTests,
    [switch]$Run,
    [string]$Arch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/ArtGander.App/ArtGander.App.csproj'
$out = Join-Path $root 'artifacts/local'
$config = if ($Fast) { 'Debug' } else { 'Release' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Invoke-Step([string]$Name, [scriptblock]$Block) {
    Write-Host "`n== $Name ==" -ForegroundColor Cyan
    & $Block
    if ($LASTEXITCODE) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

if (-not ($Fast -or $SkipTests)) {
    Invoke-Step 'Tests' { dotnet test --project (Join-Path $root 'tests/ArtGander.Core.Tests') -c Release }
}

if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    if (-not $Arch) { $Arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' } }
    $publish = Join-Path $out "win-$Arch"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    $extra = if ($Fast) { @('-p:PublishTrimmed=false', '-p:PublishReadyToRun=false') } else { @() }
    Invoke-Step "Publish ($config, win-$Arch)" {
        dotnet publish $project -c $config -f net11.0-windows10.0.19041.0 -r "win-$Arch" -o $publish @extra
    }
    $exe = Get-ChildItem $publish -Filter 'ArtGander*.exe' | Select-Object -First 1
    Write-Host "App: $($exe.FullName)" -ForegroundColor Green

    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($Installer -and -not $iscc) { throw 'Inno Setup 6 is not installed. Install it with: winget install JRSoftware.InnoSetup' }
    if ($iscc -and -not $Fast) {
        Invoke-Step 'Installer' {
            & $iscc /Q "/DAppVersion=$version" "/DArch=$Arch" "/DSourceDir=$publish" "/DOutputDir=$out\installers" (Join-Path $root 'installers/windows/ArtGander.iss')
        }
        Write-Host "Installer: $out\installers" -ForegroundColor Green
    }

    if ($Run) { Start-Process $exe.FullName }
}
elseif ($IsMacOS) {
    Invoke-Step "Publish ($config, Mac Catalyst)" { dotnet publish $project -c $config -f net11.0-maccatalyst }
    $app = Get-ChildItem (Join-Path $root "src/ArtGander.App/bin/$config/net11.0-maccatalyst") -Recurse -Directory -Filter 'ArtGander.app' | Select-Object -First 1
    Write-Host "App: $($app.FullName)" -ForegroundColor Green
    if ($Installer) {
        Invoke-Step 'DMG' { bash (Join-Path $root 'installers/macos/make-dmg.sh') $app.FullName $version (Join-Path $out 'installers') }
    }
    if ($Run) { open $app.FullName }
}
else {
    throw 'ArtGander builds on Windows and macOS only.'
}
