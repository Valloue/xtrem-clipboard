param(
    [Parameter(Mandatory = $true)][string]$Csproj
)

$ErrorActionPreference = "Stop"
[xml]$xml = Get-Content -LiteralPath $Csproj
$app = @($xml.SelectNodes("//AppVersion") | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -and ($_ -notmatch '^\$\(') }) | Select-Object -First 1
if (-not $app) {
    $app = @($xml.SelectNodes("//Version") | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -and ($_ -notmatch '^\$\(') }) | Select-Object -First 1
}
if (-not $app) {
    throw "Version introuvable dans $Csproj"
}
Write-Output $app