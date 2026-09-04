$p = Start-Process -FilePath 'D:\headtracker\HeadTracker.NET\src\HeadTracker.App\bin\Debug\net8.0-windows\HeadTracker.exe' -PassThru
Start-Sleep -Seconds 6
if ($p.HasExited) {
    Write-Output ('EXITED code=' + $p.ExitCode)
} else {
    Write-Output ('RUNNING pid=' + $p.Id + ' title=' + $p.MainWindowTitle)
    $p.Kill()
}
