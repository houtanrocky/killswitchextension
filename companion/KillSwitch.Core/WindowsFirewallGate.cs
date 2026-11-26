using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace KillSwitch.Core;

/// <summary>
/// Makes the configured gateway the only remote destination allowed to the companion executable.
/// All other outbound connections use Windows Firewall's default outbound action while enabled.
/// </summary>
public static class WindowsFirewallGate
{
	private const string RuleName = "Kill Switch Companion - Gateway only";
	private const string IcsBlockRuleName = "Kill Switch Companion - Block Internet Connection Sharing egress";
	private static readonly string BackupPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitch", "firewall-backup.json");

	public static bool HasRecoveryState => File.Exists(BackupPath);

	public static Task EnableAsync(string executablePath, int remotePort, IEnumerable<IPAddress> gatewayAddresses, CancellationToken cancellationToken = default)
	{
		return RunPowerShellAsync(BuildEnableScript(executablePath, remotePort, gatewayAddresses), cancellationToken);
	}

	public static string BuildEnableScript(string executablePath, int remotePort, IEnumerable<IPAddress> gatewayAddresses, string? recoveryFilePath = null)
	{
		if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Run the installed Kill Switch executable to enable the Windows firewall gate.");
		if (remotePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));
		var addresses = gatewayAddresses.Distinct().Select(address => address.ToString()).ToArray();
		if (addresses.Length == 0) throw new InvalidOperationException("The gateway hostname did not resolve to any IP addresses.");
		var addressLiteral = string.Join(",", addresses.Select(PowerShellQuote));
		var script = """
$ErrorActionPreference = 'Stop'
$statePath = __STATE_PATH__
$tempStatePath = $statePath + '.tmp'
$stateDirectory = Split-Path -Parent $statePath
$ruleName = __RULE_NAME__
$icsBlockRuleName = __ICS_BLOCK_RULE_NAME__
$programPath = __PROGRAM_PATH__
$remoteAddresses = @(__REMOTE_ADDRESSES__)
$remotePort = __REMOTE_PORT__
if (Test-Path -LiteralPath $statePath) { throw 'A previous Windows firewall gate state needs recovery before a new gate can start.' }
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) { throw 'A firewall rule with the Kill Switch name already exists; refusing to replace it.' }
if (Get-NetFirewallRule -DisplayName $icsBlockRuleName -ErrorAction SilentlyContinue) { throw 'A firewall rule with the Kill Switch ICS block name already exists; refusing to replace it.' }
$profiles = @(Get-NetFirewallProfile -PolicyStore PersistentStore)
$effectiveProfiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
if ($profiles.Count -ne 3 -or $effectiveProfiles.Count -ne 3) { throw 'Could not read all Windows Firewall profiles.' }
if (@($effectiveProfiles | Where-Object { -not $_.Enabled }).Count -gt 0) { throw 'Windows Firewall must be enabled for Domain, Private, and Public profiles.' }
$activeAllows = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Outbound -Action Allow)
$sharedAccessAllows = @(
  foreach ($candidate in $activeAllows) {
    if ([string]$candidate.PolicyStoreSourceType -eq 'Dynamic') {
      $serviceFilter = $candidate | Get-NetFirewallServiceFilter
      if ($serviceFilter.Service -contains 'SharedAccess') { $candidate }
    }
  }
)
$sharedAccessRuleNames = @($sharedAccessAllows | ForEach-Object { $_.Name })
$unsupportedAllows = @($activeAllows | Where-Object { $_.DisplayName -ne $ruleName -and [string]$_.PolicyStoreSourceType -ne 'Local' -and $_.Name -notin $sharedAccessRuleNames })
if ($unsupportedAllows.Count -gt 0) {
  $unsupportedNames = ($unsupportedAllows | ForEach-Object { $_.DisplayName }) -join ', '
  throw "Other outbound allow rules remain active and cannot be disabled safely: $unsupportedNames"
}
$previousRules = @(Get-NetFirewallRule -PolicyStore PersistentStore -Enabled True -Direction Outbound -Action Allow)
$previous = [pscustomobject]@{
  Profiles = @($profiles | ForEach-Object { [pscustomobject]@{ Name = $_.Name; DefaultOutboundAction = [string]$_.DefaultOutboundAction } })
  Rules = @($previousRules | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Enabled = [bool]$_.Enabled } })
}
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$previous | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $tempStatePath -Encoding UTF8
Move-Item -LiteralPath $tempStatePath -Destination $statePath | Out-Null
try {
  if ($sharedAccessRuleNames.Count -gt 0) {
    New-NetFirewallRule -DisplayName $icsBlockRuleName -Group 'Kill Switch Companion' -Direction Outbound -Action Block -Service 'SharedAccess' -RemoteAddress Any -Protocol Any -Profile Domain,Private,Public -Enabled True | Out-Null
  }
  $previousRuleNames = @($previous.Rules | ForEach-Object { $_.Name })
  if ($previousRuleNames.Count -gt 0) { Set-NetFirewallRule -PolicyStore PersistentStore -Name $previousRuleNames -Enabled False }
  New-NetFirewallRule -DisplayName $ruleName -Group 'Kill Switch Companion' -Direction Outbound -Action Allow -Program $programPath -RemoteAddress $remoteAddresses -Protocol TCP -RemotePort $remotePort -Profile Domain,Private,Public -Enabled True | Out-Null
  Set-NetFirewallProfile -Profile Domain,Private,Public -DefaultOutboundAction Block
  $verified = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
  if (@($verified | Where-Object { -not $_.Enabled -or $_.DefaultOutboundAction -ne 'Block' }).Count -gt 0) { throw 'Windows Firewall did not apply the outbound block to every active profile.' }
  $otherAllows = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Outbound -Action Allow | Where-Object { $_.DisplayName -ne $ruleName -and $_.Name -notin $sharedAccessRuleNames })
  if ($otherAllows.Count -gt 0) { throw 'Other outbound allow rules remain active; strict gateway-only mode cannot be verified.' }
  if ($sharedAccessRuleNames.Count -gt 0) {
    $icsBlockRules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Outbound -Action Block | Where-Object { $_.DisplayName -eq $icsBlockRuleName })
    $blockedServices = @($icsBlockRules | Get-NetFirewallServiceFilter | ForEach-Object { $_.Service })
    if ($icsBlockRules.Count -ne 1 -or $blockedServices -notcontains 'SharedAccess') { throw 'The Internet Connection Sharing allow exception is not covered by an active service-scoped block rule.' }
  }
} catch {
  foreach ($entry in $previous.Profiles) { Set-NetFirewallProfile -Profile $entry.Name -DefaultOutboundAction $entry.DefaultOutboundAction }
  foreach ($group in @($previous.Rules | Group-Object Enabled)) {
    $enabledValue = if ([bool]::Parse($group.Name)) { 'True' } else { 'False' }
    $ruleNames = @($group.Group | ForEach-Object { $_.Name })
    if ($ruleNames.Count -gt 0) { Set-NetFirewallRule -PolicyStore PersistentStore -Name $ruleNames -Enabled $enabledValue }
  }
  Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
  Remove-NetFirewallRule -DisplayName $icsBlockRuleName -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $statePath -ErrorAction SilentlyContinue
  throw
}
""";
		var statePath = recoveryFilePath ?? BackupPath;
		if (!Path.IsPathFullyQualified(statePath)) throw new ArgumentException("The firewall recovery file path must be absolute.", nameof(recoveryFilePath));
		script = script.Replace("__STATE_PATH__", PowerShellQuote(statePath), StringComparison.Ordinal)
			.Replace("__RULE_NAME__", PowerShellQuote(RuleName), StringComparison.Ordinal)
			.Replace("__ICS_BLOCK_RULE_NAME__", PowerShellQuote(IcsBlockRuleName), StringComparison.Ordinal)
			.Replace("__PROGRAM_PATH__", PowerShellQuote(executablePath), StringComparison.Ordinal)
			.Replace("__REMOTE_ADDRESSES__", addressLiteral, StringComparison.Ordinal)
			.Replace("__REMOTE_PORT__", remotePort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
		return script;
	}

	public static Task RestoreAsync(CancellationToken cancellationToken = default)
	{
		return RunPowerShellAsync(BuildRestoreScript(), cancellationToken);
	}

	public static string BuildRestoreScript(string? recoveryFilePath = null)
	{
		var script = """
$ErrorActionPreference = 'Stop'
$statePath = __STATE_PATH__
$ruleName = __RULE_NAME__
$icsBlockRuleName = __ICS_BLOCK_RULE_NAME__
if (-not (Test-Path -LiteralPath $statePath)) {
  if ((Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) -or (Get-NetFirewallRule -DisplayName $icsBlockRuleName -ErrorAction SilentlyContinue)) { throw 'Kill Switch firewall rule exists but its recovery record is missing; refusing to claim the gate is stopped.' }
  return
}
$previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if (@($previous.Profiles).Count -ne 3 -or $null -eq $previous.Rules) { throw 'Saved firewall recovery data is invalid; outbound traffic remains blocked.' }
foreach ($entry in $previous.Profiles) {
  if ($entry.Name -notin @('Domain', 'Private', 'Public') -or $entry.DefaultOutboundAction -notin @('Allow', 'Block', 'NotConfigured')) { throw 'Saved firewall recovery data contains an unsupported value.' }
}
foreach ($entry in $previous.Rules) {
  if ([string]::IsNullOrWhiteSpace($entry.Name) -or $entry.Enabled -isnot [bool]) { throw 'Saved firewall recovery data contains an invalid rule record.' }
}
foreach ($entry in $previous.Profiles) { Set-NetFirewallProfile -Profile $entry.Name -DefaultOutboundAction $entry.DefaultOutboundAction }
foreach ($group in @($previous.Rules | Group-Object Enabled)) {
  $enabledValue = if ([bool]::Parse($group.Name)) { 'True' } else { 'False' }
  $ruleNames = @($group.Group | ForEach-Object { $_.Name })
  if ($ruleNames.Count -gt 0) { Set-NetFirewallRule -PolicyStore PersistentStore -Name $ruleNames -Enabled $enabledValue }
}
Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName $icsBlockRuleName -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $statePath
""";
		var statePath = recoveryFilePath ?? BackupPath;
		if (!Path.IsPathFullyQualified(statePath)) throw new ArgumentException("The firewall recovery file path must be absolute.", nameof(recoveryFilePath));
		script = script.Replace("__STATE_PATH__", PowerShellQuote(statePath), StringComparison.Ordinal)
			.Replace("__RULE_NAME__", PowerShellQuote(RuleName), StringComparison.Ordinal);
		script = script.Replace("__ICS_BLOCK_RULE_NAME__", PowerShellQuote(IcsBlockRuleName), StringComparison.Ordinal);
		return script;
	}

	private static async Task RunPowerShellAsync(string script, CancellationToken cancellationToken)
	{
		var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
		if (!File.Exists(powershellPath)) throw new FileNotFoundException("Windows PowerShell was not found in the Windows system directory.", powershellPath);
		var start = new ProcessStartInfo(powershellPath)
		{
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add("-NoProfile");
		start.ArgumentList.Add("-NonInteractive");
		start.ArgumentList.Add("-EncodedCommand");
		start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
		var errorOutput = process.StandardError.ReadToEndAsync(cancellationToken);
		var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
			try { await process.WaitForExitAsync(); } catch { }
			try { await Task.WhenAll(errorOutput, standardOutput); } catch { }
			throw;
		}
		var errors = await errorOutput;
		var output = await standardOutput;
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"Windows Firewall update failed. Run Kill Switch as administrator. {errors.Trim()} {output.Trim()}".Trim());
	}

	private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
