$log = 'D:\豆包的下载\Aviassembly_DEV\Machine\logs\Machine.log'
$c = Get-Content $log
Write-Output '--- underground count:'
($c | Select-String -Pattern 'underground').Count
Write-Output '--- spawned (first 6):'
$c | Select-String -Pattern 'Faction: spawned' | Select-Object -First 6 | ForEach-Object { $_.Line }
Write-Output '--- kills/crashes:'
$c | Select-String -Pattern 'destroyed by missile|crashed, retired' | Select-Object -First 8 | ForEach-Object { $_.Line }
Write-Output '--- firing:'
$c | Select-String -Pattern 'AAM: firing' | Select-Object -First 4 | ForEach-Object { $_.Line }
Write-Output '--- spawned total:'
($c | Select-String -Pattern 'Faction: spawned').Count
