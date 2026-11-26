using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace KillSwitch.Companion;

internal static class StartupRegistration
{
	private const string TaskName = "Kill Switch Companion";

	public static async Task<bool> IsEnabledAsync()
	{
		const string script = "if (Get-ScheduledTask -TaskName 'Kill Switch Companion' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }";
		return await RunPowerShellAsync(script) == 0;
	}

	public static async Task SetEnabledAsync(bool enabled)
	{
		if (!enabled)
		{
			await RunPowerShellAsync($"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue");
			return;
		}

		var executablePath = Environment.ProcessPath
			?? throw new InvalidOperationException("Could not determine the Kill Switch executable path.");
		var escapedPath = executablePath.Replace("'", "''", StringComparison.Ordinal);
		var userId = WindowsIdentity.GetCurrent().Name.Replace("'", "''", StringComparison.Ordinal);
		var script = $"$action = New-ScheduledTaskAction -Execute '{escapedPath}'; " +
			$"$trigger = New-ScheduledTaskTrigger -AtLogOn -User '{userId}'; " +
			"$principal = New-ScheduledTaskPrincipal -UserId '" + userId + "' -LogonType Interactive -RunLevel Highest; " +
			"$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Priority 4; " +
			"$ErrorActionPreference = 'Stop'; " +
			$"Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Shows Kill Switch protection state at sign-in.' -Force | Out-Null";
		if (await RunPowerShellAsync(script) != 0)
			throw new InvalidOperationException("Windows Task Scheduler could not update the sign-in task.");
	}

	private static async Task<int> RunPowerShellAsync(string script)
	{
		var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = "powershell.exe",
			Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
			UseShellExecute = false,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
		await process.WaitForExitAsync();
		return process.ExitCode;
	}
}
