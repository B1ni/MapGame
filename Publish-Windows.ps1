$ErrorActionPreference = "Stop"

dotnet publish "MapGame\MapGame.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-LAN.ps1') `
    -Destination (Join-Path $PSScriptRoot 'MapGame\bin\Release\net10.0\win-x64\publish\Test-LAN.ps1') -Force
foreach ($file in @('Test-Multiplayer.ps1', 'Test-Multiplayer.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) `
        -Destination (Join-Path $PSScriptRoot "MapGame\bin\Release\net10.0\win-x64\publish\$file") -Force
}

Write-Host "Spusti MapGame.exe z priečinka MapGame\bin\Release\net10.0\win-x64\publish."
Write-Host "Pri zdieľaní zachovaj celý priečinok vrátane wwwroot a Data."
