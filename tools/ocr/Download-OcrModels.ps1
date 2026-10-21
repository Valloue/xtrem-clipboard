# Xtrem Clipboard - telechargement des modeles OCR PP-OCRv6 Medium (ONNX officiels PaddlePaddle)
# Source : https://huggingface.co/PaddlePaddle (collection pp-ocrv6, Apache-2.0) - D-OCR-LOCAL.
# Aucun telechargement au runtime de l'app : ce script tourne uniquement au developpement,
# les fichiers deviennent des Content du projet et sont embarques dans le Setup.
# ASCII pur, sans BOM (PS 5.1 lit les fichiers sans BOM en ANSI).

$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot "..\..\src\XtremClipboard\Models\OCR"
$expected = @(
    @{
        Url    = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/resolve/main/inference.onnx'
        Out    = 'PP-OCRv6_medium_det\inference.onnx'
        Sha256 = 'eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1'
    },
    @{
        Url    = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/resolve/main/inference.onnx'
        Out    = 'PP-OCRv6_medium_rec\inference.onnx'
        Sha256 = '9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba'
    },
    @{
        Url    = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/resolve/main/inference.yml'
        Out    = 'PP-OCRv6_medium_rec\inference.yml'
        Sha256 = '991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129'
    }
)

foreach ($file in $expected) {
    $out = Join-Path $root $file.Out
    $dir = Split-Path $out -Parent
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    if (Test-Path $out) {
        $hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
        if ($hash -eq $file.Sha256) {
            Write-Output ("OK (cache)  : {0}" -f $file.Out)
            continue
        }
        Write-Output ("Re-download (hash mismatch) : {0}" -f $file.Out)
        Remove-Item $out -Force
    }

    Write-Output ("Downloading : {0}" -f $file.Url)
    Invoke-WebRequest -Uri $file.Url -OutFile $out -UserAgent 'XtremClipboard-Setup/0.12.0'

    $hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $file.Sha256) {
        Remove-Item $out -Force
        throw ("SHA-256 mismatch for {0}: expected {1}, got {2}" -f $file.Out, $file.Sha256, $hash)
    }
    Write-Output ("OK          : {0} ({1} bytes)" -f $file.Out, (Get-Item $out).Length)
}

Write-Output 'All PP-OCRv6 Medium ONNX models are in place.'
