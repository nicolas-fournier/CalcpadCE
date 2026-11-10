<#
  Signs a file with a LOCAL self-signed code-signing certificate.
  Creates the certificate on first use (CurrentUser\My) and exports the public .cer
  next to this script so you can trust it on other machines if you want.

  Usage:  .\sign.ps1 -File "path\to\file.exe"
#>
param([Parameter(Mandatory = $true)][string]$File)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) { throw "File not found: $File" }

$subject = 'CN=CalcpadCE Local Dev'
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating self-signed code-signing certificate ($subject)..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(5)
    Export-Certificate -Cert $cert -FilePath (Join-Path $PSScriptRoot 'CalcpadCE-local.cer') | Out-Null
    Write-Host "Public certificate exported to CalcpadCE-local.cer (import into Trusted Publishers to avoid warnings)."
}

# Prefer an x64 signtool, else any.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
if (-not $signtool) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)." }

Write-Host "Signing $File ..."
# Try with an RFC3161 timestamp; fall back to no timestamp if offline.
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 "$File"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Timestamping failed (offline?). Signing without a timestamp."
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint "$File"
}
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }
Write-Host "Signed OK."
