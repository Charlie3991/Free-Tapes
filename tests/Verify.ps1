$ErrorActionPreference = 'Stop'
$rootDirectory = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $rootDirectory 'tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $rootDirectory 'tools/cli'
$env:NUGET_PACKAGES = Join-Path $rootDirectory 'tools/packages'
& $sdk build (Join-Path $rootDirectory 'src/FreeTapes.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
$mediaToolsDirectory = Join-Path $rootDirectory 'dist/FreeTapes/tools'
if (Test-Path (Join-Path $mediaToolsDirectory 'ffmpeg.exe')) {
    & $sdk run --project (Join-Path $PSScriptRoot 'Verify.csproj') -c Release -- $mediaToolsDirectory
} else {
    & $sdk run --project (Join-Path $PSScriptRoot 'Verify.csproj') -c Release
}
if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
