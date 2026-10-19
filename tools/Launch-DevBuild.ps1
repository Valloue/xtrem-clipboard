# Launch-DevBuild.ps1
# Copie le build Debug vers LocalAppData, auto-signe CN=valloue.fr (comme Xtrem Rec), Start-Process.
# ASCII + UTF-8 BOM pour Windows PowerShell 5.1.
param(
    [Parameter(Mandatory = $true)][string]$BuildDir,
    [Parameter(Mandatory = $true)][string]$RunDir
)

$ErrorActionPreference = "Stop"
$subject = "CN=valloue.fr"
$codeSignOid = "1.3.6.1.5.5.7.3.3"

if (-not (Test-Path -LiteralPath $BuildDir)) {
    Write-Host "[ERREUR] BuildDir manquant: $BuildDir"
    exit 1
}

if (-not (Test-Path -LiteralPath $RunDir)) {
    New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
}

# L'exe stage verrouille ses DLL : robocopy code 9 si on copie par-dessus un process vivant.
Write-Host "      Fermeture de XtremClipboard deja ouvert..."
$running = @(Get-Process -Name "XtremClipboard" -ErrorAction SilentlyContinue)
foreach ($p in $running) {
    Write-Host "      Stop PID $($p.Id)"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
if ($running.Count -gt 0) {
    Start-Sleep -Milliseconds 600
}

Write-Host "      Sync stage LocalAppData..."
& robocopy.exe $BuildDir $RunDir /MIR /NFL /NDL /NJH /NJS /nc /ns /np /R:1 /W:1 | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Host "[ERREUR] robocopy echec code $rc (8 = fichier inaccessible, souvent l'exe encore ouvert)"
    & robocopy.exe $BuildDir $RunDir /MIR /R:1 /W:1 /NJH /NJS
    exit 1
}

$exe = Join-Path $RunDir "XtremClipboard.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "[ERREUR] EXE stage introuvable: $exe"
    exit 1
}

function Get-DevCodeSigningCert {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
    $store.Open("ReadOnly")
    try {
        $now = Get-Date
        $fallback = $null
        foreach ($c in $store.Certificates) {
            if ($c.Subject -ne $script:subject) { continue }
            if ($c.NotAfter -le $now) { continue }
            if (-not $c.HasPrivateKey) { continue }
            $hasEku = $false
            foreach ($ext in $c.Extensions) {
                if ($ext -isnot [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
                    continue
                }
                foreach ($oid in $ext.EnhancedKeyUsages) {
                    if ($oid.Value -eq $script:codeSignOid) { $hasEku = $true; break }
                }
            }
            if ($hasEku) { return $c }
            if (-not $fallback) { $fallback = $c }
        }
        return $fallback
    }
    finally {
        $store.Close()
    }
}

function Ensure-SecurityModule {
    if (Get-Module -Name Microsoft.PowerShell.Security -ErrorAction SilentlyContinue) { return $true }
    $i = 0
    while ($i -lt 3) {
        $i++
        try {
            Import-Module Microsoft.PowerShell.Security -Force -ErrorAction Stop | Out-Null
            if (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue) { return $true }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    return $false
}

function New-DevCodeSigningCertDotNet {
    $rsa = New-Object System.Security.Cryptography.RSACng(2048)
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        $script:subject,
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $ku = New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
        $true)
    [void]$req.CertificateExtensions.Add($ku)

    $oids = New-Object System.Security.Cryptography.OidCollection
    [void]$oids.Add((New-Object System.Security.Cryptography.Oid($script:codeSignOid, "Code Signing")))
    $eku = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oids, $false)
    [void]$req.CertificateExtensions.Add($eku)

    $notBefore = [datetime]::UtcNow.AddDays(-1)
    $notAfter = [datetime]::UtcNow.AddYears(2)
    $ephemeral = $req.CreateSelfSigned($notBefore, $notAfter)

    $tmp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), [guid]::NewGuid().ToString("N") + ".pfx")
    $plain = [guid]::NewGuid().ToString("N")
    $pfx = $ephemeral.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $plain)
    [System.IO.File]::WriteAllBytes($tmp, $pfx)

    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $persisted = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($tmp, $plain, $flags)
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue

    $my = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
    $my.Open("ReadWrite")
    try { $my.Add($persisted) }
    finally { $my.Close() }
    return $persisted
}

$cert = Get-DevCodeSigningCert
if (-not $cert) {
    Write-Host "      Creation certificat $subject"
    $hasCertDrive = [bool](Get-PSDrive -Name Cert -ErrorAction SilentlyContinue)
    if ($hasCertDrive) {
        try {
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
                -KeyExportPolicy Exportable -KeySpec Signature -KeyLength 2048 `
                -HashAlgorithm SHA256 -CertStoreLocation "Cert:\CurrentUser\My" `
                -NotAfter (Get-Date).AddYears(2)
        }
        catch {
            Write-Host "      New-SelfSignedCertificate indisponible: $($_.Exception.Message)"
            $cert = $null
        }
    }
    if (-not $cert) {
        Write-Host "      Fallback .NET CertificateRequest"
        $cert = New-DevCodeSigningCertDotNet
    }
}

if (-not $cert) {
    Write-Host "[ERREUR] Impossible d'obtenir le certificat $subject"
    exit 1
}

foreach ($name in @("TrustedPeople", "TrustedPublisher", "Root")) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, "CurrentUser")
    $store.Open("ReadWrite")
    $hit = $false
    foreach ($c in $store.Certificates) {
        if ($c.Thumbprint -eq $cert.Thumbprint) { $hit = $true; break }
    }
    if (-not $hit) { $store.Add($cert) }
    $store.Close()
}

$signedOk = $false
if (-not (Ensure-SecurityModule)) {
    Write-Host "[AVERTISSEMENT] Module Microsoft.PowerShell.Security indisponible - signature ignoree."
}
else {
    try {
        $current = Get-AuthenticodeSignature -FilePath $exe
        $sameSigner = $false
        if ($current.SignerCertificate -and $current.SignerCertificate.Thumbprint -eq $cert.Thumbprint -and $current.Status -eq "Valid") {
            $sameSigner = $true
        }
        if ($sameSigner) {
            Write-Host "      Signature deja valide (skip)"
            $signedOk = $true
        }
        else {
            Write-Host "      Signature Authenticode..."
            $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
            Write-Host "      Status=$($sig.Status)"
            $signedOk = ($sig.Status -eq "Valid")
        }
    }
    catch {
        Write-Host "[AVERTISSEMENT] Signature: $($_.Exception.Message)"
    }
}
if (-not $signedOk) {
    Write-Host "      Poursuite du lancement sans signature garantie."
}

Write-Host "      Start-Process..."
try {
    Start-Process -FilePath $exe | Out-Null
    Start-Sleep -Milliseconds 1200
    $alive = Get-Process -Name "XtremClipboard" -ErrorAction SilentlyContinue
    if (-not $alive) {
        Write-Host "[ERREUR] Processus non demarre (bloque par Smart App Control ?)."
        Write-Host "      Exe: $exe"
        exit 1
    }
    Write-Host ("      OK PID {0}" -f $alive[0].Id)
    exit 0
}
catch {
    Write-Host "[ERREUR] Start-Process: $($_.Exception.Message)"
    exit 1
}
