# Downloads the ONNX models into assets\models (not stored in git — too large).
# Also runs automatically on first build via the EnsureOnnxModels MSBuild target.
$ErrorActionPreference = "Stop"
$dir = Join-Path $PSScriptRoot "..\assets\models"
New-Item -ItemType Directory -Force $dir | Out-Null

$models = @(
    @{
        Name = "u2net.onnx"
        Url  = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx"
        MinBytes = 100MB
    },
    @{
        Name = "face_detection_yunet_2023mar.onnx"
        Url  = "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx"
        MinBytes = 100KB
    }
)

foreach ($m in $models) {
    $dest = Join-Path $dir $m.Name
    if ((Test-Path $dest) -and ((Get-Item $dest).Length -ge $m.MinBytes)) {
        Write-Host "skip $($m.Name) (exists)"
        continue
    }
    Write-Host "downloading $($m.Name) ..."
    try {
        Invoke-WebRequest -Uri $m.Url -OutFile $dest -UseBasicParsing
    } catch {
        Remove-Item $dest -ErrorAction SilentlyContinue   # drop partial download
        throw
    }
    $len = (Get-Item $dest).Length
    if ($len -lt $m.MinBytes) {
        Remove-Item $dest
        throw "Downloaded $($m.Name) looks truncated ($len bytes)."
    }
    Write-Host "  -> $dest"
}
Write-Host "Done."
