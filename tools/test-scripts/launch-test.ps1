# Launch the game and wait for GVision self-test screenshots.
# ASCII-only; paths derived from $PSScriptRoot. Output -> launch_log.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$shots = "C:\Users\16857\AppData\Local\Temp\gvision_shots"
$logPath = Join-Path $root "launch_log.txt"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

# fresh shots dir
if (Test-Path $shots) { Remove-Item $shots -Recurse -Force | Out-Null }
New-Item -ItemType Directory -Force -Path $shots | Out-Null

# make sure nothing is already running
Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object {
    Say ("killing existing pid " + $_.Id)
    $_.Kill()
}
Start-Sleep -Seconds 2

Say ("exe exists = " + (Test-Path $exe))
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $true
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Say ("started pid = " + $p.Id)
} catch {
    Say ("launch failed: " + $_.Exception.Message)
    [System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit 1
}

# wait up to 180s for the self-test captures to appear
$want = 30
$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $n = (Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue | Measure-Object).Count
    Say ("t=" + [int]((Get-Date) - $p.StartTime).TotalSeconds + "s pngs=" + $n)
    if ($n -ge $want) { break }
}

Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object {
    Say ("shot " + $_.Name + " " + $_.Length + " bytes")
}
$alive = -not $p.HasExited
Say ("game alive = " + $alive)
[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
