param(
    [Parameter(Mandatory = $true)][string]$VersionsPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$FileName,
    [Parameter(Mandatory = $true)][string]$ProductName,
    [Parameter(Mandatory = $true)][string]$SetupPrefix,
    [Parameter(Mandatory = $false)][string]$SetupPath
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $SetupPath = Join-Path (Split-Path -Parent $VersionsPath) "files\$FileName"
}

if (-not (Test-Path -LiteralPath $SetupPath)) {
    throw "Setup introuvable pour SHA-256 : $SetupPath"
}

$expected = "$SetupPrefix-Setup-"
if (-not $FileName.StartsWith($expected) -or -not $FileName.EndsWith(".exe")) {
    throw "Nom de setup inattendu : $FileName"
}

$sha256 = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$date = Get-Date -Format "yyyy-MM-dd"
$entry = [ordered]@{
    version     = $Version
    label       = $ProductName
    date        = $date
    file        = "downloads/files/$FileName"
    available   = $true
    recommended = $true
    notes       = "Installateur Windows x64 (Inno)"
    sha256      = $sha256
}

$catalog = [ordered]@{
    product  = $ProductName
    latest   = $Version
    platform = "Windows 10/11 (x64)"
    versions = @($entry)
}

$dir = Split-Path -Parent $VersionsPath
if (-not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$json = $catalog | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($VersionsPath, $json + "`r`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "      versions.json mis a jour ($Version, sha256=$sha256)"