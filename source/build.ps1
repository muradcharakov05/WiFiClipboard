$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$destination = Join-Path $PSScriptRoot '..\WiFiClipboard.exe'
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$destination", "/win32icon:$PSScriptRoot\app.ico")
$references = @('System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
foreach ($reference in $references) { $arguments += "/reference:$reference" }
foreach ($resource in @('phone.html', 'app.js', 'transfer.js', 'file-view.js', 'app-icon.png')) { $arguments += "/resource:$PSScriptRoot\$resource,$resource" }
$arguments += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Output "Built: $destination"
