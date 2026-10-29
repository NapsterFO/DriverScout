# Подпись exe сертификатом разработчика (Authenticode, SHA-256, с меткой времени).
# Сертификат берётся из .pfx (DRIVERSCOUT_CERT_PFX + DRIVERSCOUT_CERT_PASSWORD)
# или из хранилища Windows по отпечатку (DRIVERSCOUT_CERT_THUMBPRINT) — так работают и USB-токены.
param(
    [Parameter(Mandatory, Position = 0)][string[]]$Files,
    [string]$Thumbprint = $env:DRIVERSCOUT_CERT_THUMBPRINT,
    [string]$PfxPath = $env:DRIVERSCOUT_CERT_PFX,
    [string]$PfxPassword = $env:DRIVERSCOUT_CERT_PASSWORD,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

if ($PfxPath) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword)
} elseif ($Thumbprint) {
    $tp = $Thumbprint -replace '\s', ''
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $tp | Select-Object -First 1
    if (-not $cert) { throw "Сертификат с отпечатком $tp не найден в хранилище" }
} else {
    throw 'Не задан сертификат: укажите DRIVERSCOUT_CERT_PFX или DRIVERSCOUT_CERT_THUMBPRINT'
}
if (-not $cert.HasPrivateKey) { throw "У сертификата '$($cert.Subject)' нет закрытого ключа" }

foreach ($f in $Files) {
    # Метка времени сохраняет подпись действительной и после окончания срока сертификата.
    $sig = $null
    for ($try = 1; $try -le 3 -and -not $sig.SignerCertificate; $try++) {
        $sig = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
        if (-not $sig.SignerCertificate) { Start-Sleep 3 }
    }
    if (-not $sig.SignerCertificate) { throw "Не удалось подписать $f : $($sig.StatusMessage)" }
    # Valid — сертификат доверенный; UnknownError — подпись есть, но корневой сертификат не доверенный (самоподписанный).
    $note = if ($sig.Status -eq 'Valid') { 'подпись действительна' } else { "подписано, но Windows не доверяет издателю ($($sig.Status))" }
    Write-Host "$([IO.Path]::GetFileName($f)): $note; метка времени: $([bool]$sig.TimeStamperCertificate)"
}
