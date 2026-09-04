$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$out = 'D:\headtracker\HeadTracker.NET\assets\scrfd_500m_bnkps_shape640x640.onnx'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

$candidates = @(
    'https://github.com/Holasyb918/HeyGem-Linux-Python-Hack/releases/download/ckpts_and_onnx/scrfd_500m_bnkps_shape640x640.onnx',
    'https://github.com/deepinsight/insightface/releases/download/v0.7/scrfd_500m_bnkps_shape640x640.onnx',
    'https://bj.bcebos.com/fastdeploy/models/scrfd/scrfd_500m_bnkps_shape640x640.onnx'
)

foreach ($url in $candidates) {
    try {
        Write-Output ("trying " + $url)
        Invoke-WebRequest -Uri $url -OutFile $out -TimeoutSec 300
        $len = (Get-Item $out).Length
        Write-Output ("downloaded bytes=" + $len)
        if ($len -lt 1000000) { throw "file too small" }
        Write-Output 'DOWNLOAD_OK'
        exit 0
    } catch {
        Write-Output ("failed: " + $_.Exception.Message)
        if (Test-Path $out) { Remove-Item $out -Force }
    }
}
Write-Output 'ALL_FAILED'
exit 1
