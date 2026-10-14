param(
    [string]$GamePath = (Join-Path $PSScriptRoot 'MapGame.exe')
)

$ErrorActionPreference = 'Stop'
$server = $null
$profiles = @()

function Open-TestPlayer {
    param([string]$Role, [string]$Url)
    $profile = Join-Path $sessionRoot $Role
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    $script:profiles += $profile
    $arguments = @("--user-data-dir=`"$profile`"", '--no-first-run', '--no-default-browser-check',
        "--app=`"$Url`"")
    Start-Process -FilePath $browser -ArgumentList $arguments | Out-Null
    Write-Host "Otvorene okno: $Role"
}

try {
    if (-not (Test-Path -LiteralPath $GamePath -PathType Leaf)) {
        throw 'MapGame.exe sa nenasiel. Spusti tento test z rozbaleneho balika hry.'
    }
    $GamePath = (Resolve-Path -LiteralPath $GamePath).Path
    $browser = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $browser) { throw 'Test potrebuje nainstalovany Microsoft Edge alebo Google Chrome.' }
    if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Port 5080 uz pouziva ina aplikacia. Najprv zatvor spustenu Map Game a potom spusti test znova.'
    }

    $sessionRoot = Join-Path ([IO.Path]::GetTempPath()) ("MapGame-Test-{0}" -f [Guid]::NewGuid().ToString('N'))
    $server = Start-Process -FilePath $GamePath -ArgumentList '--no-browser' `
        -WorkingDirectory (Split-Path -Parent $GamePath) -PassThru
    $ready = $false
    $lastConnectionError = ''
    $deadline = (Get-Date).AddSeconds(30)
    do {
        if ($server.HasExited) { throw "Server sa ukoncil (kod $($server.ExitCode)). Pozri jeho Logs." }
        try {
            $response = Invoke-WebRequest -Uri 'http://localhost:5080/' -UseBasicParsing -TimeoutSec 2
            $ready = $response.StatusCode -eq 200 -and $response.Content -match 'MAP <strong>GAME'
        } catch {
            $lastConnectionError = $_.Exception.Message
            Start-Sleep -Milliseconds 300
        }
    } while (-not $ready -and (Get-Date) -lt $deadline)
    if (-not $ready) { throw "Server neotvoril stranku hry do 30 sekund: $lastConnectionError. Pozri konzolu servera a Logs." }

    Write-Host "`nMAP GAME - TEST TROCH HRACOV NA JEDNOM PC" -ForegroundColor Green
    Write-Host 'Kazde okno ma vlastny cisty profil, nie tvoj bezny profil prehliadaca.'
    Write-Host 'Nie su to virtualne PC: tento test neoveri firewall ani izolaciu firemnej Wi-Fi medzi pocitacmi.'
    Write-Host "`n1. V prvom okne zadaj meno Hostitel a vytvor lobby. Zatial nespustaj zapas."
    Open-TestPlayer -Role '01-hostitel' -Url 'http://localhost:5080/'

    $link = (Read-Host 'Skopiruj webovy odkaz z cakarne alebo jej 6-miestne ID a vloz ho sem').Trim()
    if ($link -match '^[A-Za-z0-9]{6}$') {
        $link = "http://127.0.0.1:5080/?room=$($link.ToUpperInvariant())"
    }
    $invite = $null
    if (-not [Uri]::TryCreate($link, [UriKind]::Absolute, [ref]$invite) -or
        $invite.Scheme -ne 'http' -or $invite.Port -ne 5080 -or $invite.UserInfo -or
        $invite.AbsolutePath -ne '/' -or $invite.Query -notmatch '^\?room=([A-Z0-9]{6})$') {
        throw 'Pouzi ID hry alebo cely odkaz z cakarne v tvare http://IP:5080/?room=ABC234.'
    }
    $roomCode = $Matches[1]
    $network = Invoke-RestMethod 'http://localhost:5080/api/network' -TimeoutSec 5
    $allowed = @($network) + @('http://localhost:5080', 'http://127.0.0.1:5080')
    if ($allowed -notcontains $invite.GetLeftPart([UriPartial]::Authority)) {
        throw 'Odkaz nepatri serveru na tomto PC. Skopiruj odkaz z prave vytvorenej lokalnej lobby.'
    }

    Write-Host "`n2. V druhom okne zadaj meno Hrac-odkaz a klikni Pripojit. Adresu hostitela nechaj prazdnu."
    Open-TestPlayer -Role '02-hrac-odkaz' -Url $invite.AbsoluteUri
    Write-Host "`n3. V tretom okne zadaj meno Hrac-aplikacia, adresu a ID:"
    Write-Host "Adresa hostitela: $($invite.GetLeftPart([UriPartial]::Authority))"
    Write-Host "ID hry: $roomCode"
    Write-Host 'Tretie okno simuluje lokalne rozhranie stiahnuteho klienta; druhy server nespusta.'
    Open-TestPlayer -Role '03-hrac-aplikacia' -Url 'http://localhost:5080/'
    Write-Host "`nV cakarni hostitela musia byt tri rozne mena. Potom spusti hru."
    Write-Host 'Over: vsetci vidia mapu, spravne tipy farbia iba mapu daneho hraca a koniec zobrazi tri vysledky.'
    Write-Host 'Logy su v Logs pri hre; v kazdom okne mozes stiahnut aj klientsku diagnostiku.'
    Write-Host "`nPo teste zatvor VSETKY TRI testovacie okna prehliadaca."
    Read-Host 'Potom stlac Enter na ukoncenie testovacieho servera' | Out-Null
} catch {
    Write-Host "TEST ZLYHAL: $($_.Exception.Message)" -ForegroundColor Red
    throw
} finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id
        $server.WaitForExit()
        Write-Host 'Testovaci server ukonceny. Ostatne aplikacie sa neukoncuju.'
    }
    foreach ($profile in $profiles) {
        try {
            Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction Stop
        } catch [System.IO.IOException] {
            Write-Warning "Profil zostal otvoreny. Po zatvoreni testovacieho okna ho mozes odstranit: $profile"
        } catch [System.UnauthorizedAccessException] {
            Write-Warning "Profil sa nepodarilo odstranit: $profile"
        }
    }
}
