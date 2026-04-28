# Sign one or more FileSorter binaries with a self-signed code-signing certificate.
#
# Usage:
#   .\Tools\sign-build.ps1 -Targets "publish\win-x64\FileSorter.exe","publish\win-x86\FileSorter.exe"
#
# On first run this creates a self-signed cert in CurrentUser\My, exports a public-cert .cer
# next to the script, and signs every -Targets path. SmartScreen will still warn the first
# time the EXE runs on another machine — to clear that, install the .cer into
# "Trusted Root Certification Authorities" on the target machine, or buy a real cert.
#
# Re-runs find the existing cert by Subject and reuse it.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Targets,

    [string]$Subject = "CN=FileSorter Self-Signed (chupep)",

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [string]$CertExportDir = $null
)

$ErrorActionPreference = "Stop"

if (-not $CertExportDir) {
    $CertExportDir = Join-Path $PSScriptRoot ".."
    $CertExportDir = (Resolve-Path $CertExportDir).Path
}

# 1. Find or create the cert.
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if (-not $cert) {
    Write-Host "[sign-build] Creating self-signed cert: $Subject" -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(3) `
        -HashAlgorithm SHA256
}
else {
    Write-Host "[sign-build] Reusing existing cert: $($cert.Thumbprint)" -ForegroundColor Cyan
}

# 2. Export public .cer next to the artifacts so end-users can install it manually.
$cerPath = Join-Path $CertExportDir "FileSorter.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "[sign-build] Public cert exported -> $cerPath" -ForegroundColor DarkGray

# 3. Locate signtool.exe (Windows SDK).
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        $signtool = Get-ChildItem -Path $sdkRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -match "x64\\signtool\.exe$" } |
                    Sort-Object FullName -Descending |
                    Select-Object -First 1
    }
}
if (-not $signtool) {
    throw "signtool.exe not found. Install the Windows 10/11 SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/)."
}
$signtoolPath = if ($signtool -is [System.Management.Automation.ApplicationInfo]) { $signtool.Path } else { $signtool.FullName }
Write-Host "[sign-build] signtool: $signtoolPath" -ForegroundColor DarkGray

# 4. Sign each target.
$failed = @()
foreach ($t in $Targets) {
    if (-not (Test-Path $t)) {
        Write-Warning "[sign-build] Skipping (not found): $t"
        $failed += $t
        continue
    }
    Write-Host "[sign-build] Signing: $t" -ForegroundColor Green
    & $signtoolPath sign `
        /sha1 $cert.Thumbprint `
        /fd SHA256 `
        /td SHA256 `
        /tr $TimestampUrl `
        $t
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "[sign-build] FAILED: $t (exit $LASTEXITCODE)"
        $failed += $t
    }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "[sign-build] $($failed.Count) target(s) failed:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host ""
Write-Host "[sign-build] All targets signed successfully." -ForegroundColor Green
Write-Host "Verify with: signtool verify /pa /v <path-to-exe>" -ForegroundColor DarkGray
