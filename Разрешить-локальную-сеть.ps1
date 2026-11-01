param([string]$ProgramPath, [string]$ResultPath)
$ErrorActionPreference = 'Stop'
try {
    if (-not $ProgramPath) {
        $ProgramPath = Join-Path $PSScriptRoot 'WiFiClipboard.exe'
        if (-not (Test-Path -LiteralPath $ProgramPath)) { $ProgramPath = Join-Path $PSScriptRoot 'dist\WiFiClipboard.exe' }
    }
    $ProgramPath = (Resolve-Path -LiteralPath $ProgramPath).Path
    if ([IO.Path]::GetFileName($ProgramPath) -ne 'WiFiClipboard.exe') { throw 'Select WiFiClipboard.exe.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'RemoteSigned', '-File', ('"' + $PSCommandPath + '"'), '-ProgramPath', ('"' + $ProgramPath + '"'))
        if ($ResultPath) { $arguments += @('-ResultPath', ('"' + $ResultPath + '"')) }
        $child = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
        $child.WaitForExit()
        exit $child.ExitCode
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pathId = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ProgramPath.ToLowerInvariant())))).Replace('-', '').Substring(0,12) } finally { $sha.Dispose() }
    foreach ($spec in @(@{ Protocol='TCP'; Port=8765 }, @{ Protocol='UDP'; Port=5353 })) {
        $name = 'WiFiClipboard-LAN-' + $spec.Protocol + '-' + $pathId
        $options = @{ Name=$name; Direction='Inbound'; Action='Allow'; Enabled='True'; Profile=@('Private','Public'); Program=$ProgramPath; Protocol=$spec.Protocol; LocalPort=$spec.Port; RemoteAddress='LocalSubnet'; EdgeTraversalPolicy='Block' }
        if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) { Set-NetFirewallRule @options | Out-Null }
        else { New-NetFirewallRule @options -DisplayName ('WiFiClipboard - local subnet ' + $spec.Protocol) -Description 'Only this executable and local-subnet devices; no edge traversal.' | Out-Null }
    }
    if ($ResultPath) { [IO.File]::WriteAllText($ResultPath, '{"ok":true}', [Text.Encoding]::UTF8) }
    else { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show('Local network access is enabled for WiFiClipboard. Restart the app and open its QR code on iPhone.', 'WiFiClipboard') | Out-Null }
} catch {
    if ($ResultPath) { [IO.File]::WriteAllText($ResultPath, (@{ok=$false;error=$_.Exception.Message} | ConvertTo-Json -Compress), [Text.Encoding]::UTF8) }
    else { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'WiFiClipboard') | Out-Null }
    exit 1
}
