$procs = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "Aviassembly.exe" }
foreach ($p in $procs) {
    Write-Output ("PID=" + $p.ProcessId + " EXE=" + $p.ExecutablePath)
}
