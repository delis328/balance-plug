param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\BalancePlug.exe'),
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\outputs\perf-data'),
    [string]$Report = (Join-Path $PSScriptRoot '..\outputs\performance.json'),
    [int]$StartupRuns = 5,
    [int]$SampleSeconds = 60,
    [int]$SoakMinutes = 0
)
$ErrorActionPreference = 'Stop'
$Exe = [IO.Path]::GetFullPath($Exe)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$Report = [IO.Path]::GetFullPath($Report)
if (-not (Test-Path -LiteralPath $Exe)) { throw 'Publish the Release executable first.' }
if (Get-Process BalancePlug -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }) { throw 'Close an existing instance of this executable before isolated measurement.' }
New-Item -ItemType Directory -Force $DataDirectory,([IO.Path]::GetDirectoryName($Report)) | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BalancePlugProbe {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr param);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 public static bool HasVisibleWindow(int processId) {
  bool found = false;
  EnumWindows((h,p) => { uint pid; GetWindowThreadProcessId(h,out pid); if(pid==processId && IsWindowVisible(h)) {found=true; return false;} return true; }, IntPtr.Zero);
  return found;
 }
}
'@
$oldData = $env:BALANCEPLUG_DATA_DIR
$env:BALANCEPLUG_DATA_DIR = $DataDirectory
$times = @(); $process = $null; $samples = @()
try {
    for ($i=0; $i -lt $StartupRuns; $i++) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $Exe -PassThru
        while (-not [BalancePlugProbe]::HasVisibleWindow($process.Id)) {
            if ($process.HasExited -or $clock.Elapsed.TotalSeconds -gt 10) { throw 'Window did not become visible within 10 seconds.' }
            Start-Sleep -Milliseconds 10
        }
        $clock.Stop(); $times += [Math]::Round($clock.Elapsed.TotalMilliseconds,2)
        Write-Output ('Visible startup ' + ($i+1) + ': ' + $times[-1] + ' ms')
        if ($i -lt $StartupRuns-1) { Start-Sleep -Seconds 2; Stop-Process -Id $process.Id; $process.WaitForExit(); $process=$null }
    }
    $duplicate = Start-Process -FilePath $Exe -PassThru
    if (-not $duplicate.WaitForExit(3000)) { Stop-Process -Id $duplicate.Id; throw 'Duplicate startup did not exit.' }
    if ($process.HasExited) { throw 'Original process exited during duplicate startup.' }
    Write-Output 'Single-instance check: passed'
    Start-Sleep -Seconds 10
    $process.Refresh(); $startCpu = $process.TotalProcessorTime.TotalSeconds
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $end = [Math]::Max($SampleSeconds, $SoakMinutes * 60)
    while ($clock.Elapsed.TotalSeconds -lt $end) {
        if ($process.HasExited) { throw 'Measured process exited unexpectedly.' }
        $process.Refresh()
        $samples += [PSCustomObject]@{
            elapsedSeconds=[Math]::Round($clock.Elapsed.TotalSeconds,2)
            workingSetMB=[Math]::Round($process.WorkingSet64 / 1MB,2)
            privateMB=[Math]::Round($process.PrivateMemorySize64 / 1MB,2)
            cpuSeconds=$process.TotalProcessorTime.TotalSeconds
            visible=[BalancePlugProbe]::HasVisibleWindow($process.Id)
            handles=$process.HandleCount
        }
        if (-not $samples[-1].visible) { throw 'Measured floating window disappeared.' }
        Start-Sleep -Seconds ([Math]::Min(10,[Math]::Max(1,$end-$clock.Elapsed.TotalSeconds)))
    }
    $process.Refresh(); $cpu = ($process.TotalProcessorTime.TotalSeconds-$startCpu)/$clock.Elapsed.TotalSeconds*100
    $cpuTotal = $cpu/[Environment]::ProcessorCount
    $maxMb = ($samples | Measure-Object workingSetMB -Maximum).Maximum
    $firstMb = $samples[0].workingSetMB; $lastMb = $samples[-1].workingSetMB
    $result = [PSCustomObject]@{
        status='completed'; singleInstancePass=$true; completedAt=(Get-Date).ToString('o'); durationSeconds=[Math]::Round($clock.Elapsed.TotalSeconds,2)
        startupMilliseconds=$times; startupWorstMilliseconds=($times | Measure-Object -Maximum).Maximum
        maxWorkingSetMB=$maxMb; workingSetChangeMB=[Math]::Round($lastMb-$firstMb,2)
        averageCpuSingleCorePercent=[Math]::Round($cpu,3); averageCpuMachinePercent=[Math]::Round($cpuTotal,3)
        targetsPass=(($times | Measure-Object -Maximum).Maximum -le 2000 -and $maxMb -le 100 -and $cpuTotal -le 1)
        samples=$samples
        notes='Isolated app data. Repeated startup, not cold OS boot. CPU machine percent normalized across logical processors. Forced termination is only for this measurement process; graceful exit is covered by UI smoke.'
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding utf8
    $result | Select-Object status,durationSeconds,startupWorstMilliseconds,maxWorkingSetMB,averageCpuSingleCorePercent,averageCpuMachinePercent,targetsPass | Format-List
} catch {
    [PSCustomObject]@{status='failed'; error=$_.Exception.Message; startupMilliseconds=$times; samples=$samples} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding utf8
    throw
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($null -eq $oldData) { Remove-Item Env:BALANCEPLUG_DATA_DIR -ErrorAction SilentlyContinue } else { $env:BALANCEPLUG_DATA_DIR=$oldData }
}


