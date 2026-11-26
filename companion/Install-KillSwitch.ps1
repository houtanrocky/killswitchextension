[CmdletBinding()]
param(
	[switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$taskName = 'Kill Switch Companion'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KillSwitchCompanion'
$installDirectory = Join-Path $env:ProgramFiles 'Kill Switch Companion'
$executableName = 'KillSwitch.Companion.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
	$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
	Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
	exit
}

$packageDirectory = Split-Path -Parent $PSCommandPath
$sourceExecutable = Join-Path $packageDirectory $executableName
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
	throw "Could not find $executableName beside the installer. Extract the full download before running setup."
}

$runningApps = @(Get-Process -Name 'KillSwitch.Companion' -ErrorAction SilentlyContinue)
foreach ($runningApp in $runningApps) {
	if ($runningApp.MainWindowHandle -eq 0 -or -not $runningApp.CloseMainWindow()) {
		throw 'Close Kill Switch Companion before installing the update. If protection is active, close it from its window so it can restore the firewall safely.'
	}
	if (-not $runningApp.WaitForExit(30000)) {
		throw 'Kill Switch Companion did not close. The installer stopped to preserve its active firewall state.'
	}
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceExecutable -Destination (Join-Path $installDirectory $executableName) -Force
Copy-Item -LiteralPath (Join-Path $packageDirectory 'Uninstall-KillSwitch.ps1') -Destination $installDirectory -Force

$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'Kill Switch Companion.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDirectory $executableName
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$($shortcut.TargetPath),0"
$shortcut.Description = 'Kill Switch network access protection'
$shortcut.Save()

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
if (-not $NoStartup) {
	$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
	$action = New-ScheduledTaskAction -Execute (Join-Path $installDirectory $executableName)
	$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
	$taskPrincipal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
	$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Priority 4
	Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Description 'Shows Kill Switch protection state at sign-in.' -Force | Out-Null
}

$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDirectory\Uninstall-KillSwitch.ps1`""
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Kill Switch Companion' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.1.4' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'Kill Switch' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDirectory -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Start-Process -FilePath (Join-Path $installDirectory $executableName)
Write-Host 'Kill Switch Companion is installed.' -ForegroundColor Green
if (-not $NoStartup) {
	Write-Host 'It will open visibly at sign-in and show whether protection is active.' -ForegroundColor Green
}
Write-Host 'The sign-in task opens the app but does not enable the egress gate. Turn on protection in the app when you are ready.' -ForegroundColor Yellow
