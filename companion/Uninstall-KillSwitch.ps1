$ErrorActionPreference = 'Stop'
$taskName = 'Kill Switch Companion'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KillSwitchCompanion'
$installDirectory = Join-Path $env:ProgramFiles 'Kill Switch Companion'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
	$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
	Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
	exit
}

$runningApps = @(Get-Process -Name 'KillSwitch.Companion' -ErrorAction SilentlyContinue)
foreach ($runningApp in $runningApps) {
	if ($runningApp.MainWindowHandle -eq 0 -or -not $runningApp.CloseMainWindow()) {
		throw 'Close Kill Switch Companion from its window before uninstalling. This lets the app restore the firewall and proxy safely.'
	}
	if (-not $runningApp.WaitForExit(30000)) {
		throw 'Kill Switch Companion did not close. The uninstaller stopped to preserve its active firewall state.'
	}
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Kill Switch Companion.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installDirectory -Recurse -Force
Write-Host 'Kill Switch Companion has been removed.' -ForegroundColor Green
