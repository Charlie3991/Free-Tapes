param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'tools/cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'tools/packages'
& $sdk publish (Join-Path $PSScriptRoot 'src/FreeTapes.csproj') -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o (Join-Path $PSScriptRoot 'dist/FreeTapes')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item (Join-Path $PSScriptRoot 'Setup-Tools.ps1') (Join-Path $PSScriptRoot 'dist/FreeTapes') -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $PSScriptRoot 'dist/FreeTapes') -Force
