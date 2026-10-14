param(
    [string]$HostAddress
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($HostAddress)) {
    $HostAddress = Read-Host "Zadaj adresu hostitela (napr. http://192.168.1.25:5080)"
}
if ($HostAddress -notmatch '^\w+://') { $HostAddress = "http://$HostAddress" }
$uri = [UriBuilder]::new($HostAddress)
if ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') { throw "Pouzi HTTP alebo HTTPS adresu." }
if ($uri.Port -eq 80 -or $uri.Port -eq 443) { $uri.Port = 5080 }
$uri.Path = "/api/network"
$uri.Query = ""
$uri.Fragment = ""
$uri.UserName = ""
$uri.Password = ""

$logRoot = Join-Path $PSScriptRoot 'Logs'
try { New-Item -ItemType Directory -Path $logRoot -Force | Out-Null }
catch {
    Write-Warning "Do priecinka hry sa neda zapisovat: $($_.Exception.Message)"
    $logRoot = Join-Path $env:LOCALAPPDATA 'MapGame\Logs'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
}
$reportPath = Join-Path $logRoot ("Test-LAN-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Transcript -Path $reportPath | Out-Null
try {
    Write-Output "UTC: $([DateTime]::UtcNow.ToString('o'))"
    Write-Output "Target: $($uri.Uri.AbsoluteUri)"
    Write-Output "`nLocal network addresses and gateways:"
    Get-NetIPConfiguration | Format-List InterfaceAlias,IPv4Address,IPv4DefaultGateway
    Write-Output "`nWindows network profiles:"
    Get-NetConnectionProfile | Format-Table InterfaceAlias,NetworkCategory,IPv4Connectivity
    Write-Output "`nTCP reachability (ping failure alone does not prove the host is unavailable):"
    Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -InformationLevel Detailed | Format-List
    Write-Output "`nHTTP without proxy:"
    try {
        $request = [System.Net.HttpWebRequest]::Create($uri.Uri)
        $request.Proxy = $null
        $request.Timeout = 10000
        $response = $request.GetResponse()
        try {
            Write-Output "HTTP $([int]$response.StatusCode)"
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            try { Write-Output $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $response.Dispose() }
    } catch {
        Write-Output "HTTP FAILED: $($_.Exception.ToString())"
    }
} finally {
    Stop-Transcript | Out-Null
    Write-Host "Diagnostika ulozena: $reportPath"
}
