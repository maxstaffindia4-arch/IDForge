# Downloads the ONNX models into assets\models (not stored in git — too large).
$ErrorActionPreference = "Stop"
$dir = Join-Path $PSScriptRoot "..\assets\models"
New-Item -ItemType Directory -Force $dir | Out-Null

$models = @(
    @{
        Name = "u2net.onnx"
        Url  = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx"
    },
    @{
        Name = "face_detection_yunet_2023mar.onnx"
        Url  = "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx"
    }
)

foreach ($m in $models) {
    $dest = Join-Path $dir $m.Name
    if (Test-Path $dest) { Write-Host "skip $($m.Name) (exists)"; continue }
    Write-Host "downloading $($m.Name) ..."
    Invoke-WebRequest -Uri $m.Url -OutFile $dest -UseBasicParsing
    Write-Host "  -> $dest"
}
Write-Host "Done."
