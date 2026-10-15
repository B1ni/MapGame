$ErrorActionPreference = "Stop"

$publishDirectory = Join-Path $PSScriptRoot 'FullGame'

dotnet publish (Join-Path $PSScriptRoot 'MapGame\MapGame.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

foreach ($file in @('README.md', 'Test-LAN.ps1', 'Test-Multiplayer.ps1', 'Test-Multiplayer.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) `
        -Destination (Join-Path $publishDirectory $file) -Force
}

Write-Host "Spusti MapGame.exe z priečinka $publishDirectory."
Write-Host "Pri zdieľaní zachovaj celý priečinok vrátane wwwroot a Data."
