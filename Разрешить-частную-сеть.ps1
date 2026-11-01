# Run manually as Administrator only if the Windows Firewall prompt did not appear.
$ErrorActionPreference = 'Stop'
$program = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'WiFiClipboard.exe')).Path
$ruleName = 'WiFiClipboard-Private-8765'
if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
    Write-Output 'Rule already exists. If you moved the program, remove the old rule first.'
} else {
    New-NetFirewallRule -Name $ruleName -DisplayName 'WiFiClipboard - Private LAN' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8765 -Program $program -Profile Private -RemoteAddress LocalSubnet | Out-Null
    Write-Output 'Allowed TCP 8765 for this program on the private local subnet.'
}

# Optional stable .local address (mDNS); private LAN only.
$mdnsRuleName = 'WiFiClipboard-mDNS-Private'
if (-not (Get-NetFirewallRule -Name $mdnsRuleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $mdnsRuleName -DisplayName 'WiFiClipboard - mDNS Private LAN' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5353 -Program $program -Profile Private -RemoteAddress LocalSubnet | Out-Null
    Write-Output 'Allowed mDNS UDP 5353 for the stable .local address on the private LAN.'
}
