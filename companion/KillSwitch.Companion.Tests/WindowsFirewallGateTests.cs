using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class WindowsFirewallGateTests
{
	[TestMethod]
	public async Task FirewallScriptsParseAndScopeAllowRuleToGatewayOnly()
	{
		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
		var enable = WindowsFirewallGate.BuildEnableScript(executable, 9443, [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::7")]);
		var restore = WindowsFirewallGate.BuildRestoreScript();

		StringAssert.Contains(enable, "-DefaultOutboundAction Block");
		StringAssert.Contains(enable, "$programPath = '" + executable.Replace("'", "''", StringComparison.Ordinal) + "'");
		StringAssert.Contains(enable, "-Program $programPath");
		StringAssert.Contains(enable, "$remotePort = 9443");
		StringAssert.Contains(enable, "-RemotePort $remotePort");
		StringAssert.Contains(enable, "'203.0.113.7','2001:db8::7'");
		StringAssert.Contains(enable, "Get-NetFirewallProfile -PolicyStore ActiveStore");
		StringAssert.Contains(enable, "Windows Firewall did not apply the outbound block");
		StringAssert.Contains(enable, "Windows Firewall must be enabled for Domain, Private, and Public profiles");
		StringAssert.Contains(enable, "Other outbound allow rules remain active");
		StringAssert.Contains(enable, "-Service 'SharedAccess'");
		StringAssert.Contains(restore, "Remove-NetFirewallRule -DisplayName $icsBlockRuleName");
		StringAssert.Contains(enable, "-Enabled False");
		StringAssert.Contains(enable, "Set-NetFirewallProfile -Profile $entry.Name -DefaultOutboundAction $entry.DefaultOutboundAction");
		StringAssert.Contains(restore, "Remove-NetFirewallRule -DisplayName $ruleName");
		StringAssert.Contains(restore, "Remove-Item -LiteralPath $statePath");

		await ParseWithWindowsPowerShell(enable);
		await ParseWithWindowsPowerShell(restore);
	}

	[TestMethod]
	public async Task DynamicInternetConnectionSharingAllowIsCoveredByAServiceScopedBlock()
	{
		var folder = Path.Combine(Path.GetTempPath(), "killswitch-firewall-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		var statePath = Path.Combine(folder, "firewall.json");
		try
		{
			var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path unavailable.");
			var enable = WindowsFirewallGate.BuildEnableScript(executable, 9443, [IPAddress.Parse("203.0.113.7")], statePath);
			var restore = WindowsFirewallGate.BuildRestoreScript(statePath);
			var result = await RunFirewallSimulationAsync(enable, restore, statePath, includePolicyAllow: false, includeDynamicIcs: true);
			Assert.IsNull(result.RootElement.GetProperty("error").GetString(), $"Activation failed: {result.RootElement.GetProperty("error").GetString()}");
			var activeRules = result.RootElement.GetProperty("active").GetProperty("rules").EnumerateArray().ToArray();
			var gatewayRule = activeRules.Single(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Gateway only");
			Assert.IsTrue(gatewayRule.GetProperty("enabled").GetBoolean());
			var icsBlockRule = activeRules.Single(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Block Internet Connection Sharing egress");
			Assert.AreEqual("Block", icsBlockRule.GetProperty("action").GetString());
			Assert.AreEqual("SharedAccess", icsBlockRule.GetProperty("service").GetString());
			var restoredRules = result.RootElement.GetProperty("restored").GetProperty("rules").EnumerateArray().ToArray();
			Assert.IsTrue(restoredRules.Single(rule => rule.GetProperty("displayName").GetString() == "Internet Connection Sharing (SharedAccess-Out)").GetProperty("enabled").GetBoolean());
			Assert.IsFalse(restoredRules.Any(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Block Internet Connection Sharing egress"));
			Assert.IsFalse(result.RootElement.GetProperty("recoveryFileExists").GetBoolean());
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
		}
	}

	[TestMethod]
	public void FirewallScriptEscapesAnExecutablePathForPowerShell()
	{
		var executable = Path.Combine(Path.GetTempPath(), $"Kill Switch's {Guid.NewGuid():N}.exe");
		try
		{
			File.WriteAllText(executable, "test");
			var script = WindowsFirewallGate.BuildEnableScript(executable, 443, [IPAddress.Loopback]);
			StringAssert.Contains(script, "$programPath = '" + executable.Replace("'", "''", StringComparison.Ordinal) + "'");
		}
		finally
		{
			if (File.Exists(executable)) File.Delete(executable);
		}
	}

	[TestMethod]
	public async Task ActivationBlocksOtherEgressAndRestoresThePriorFirewallState()
	{
		var folder = Path.Combine(Path.GetTempPath(), "killswitch-firewall-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		var statePath = Path.Combine(folder, "firewall.json");
		try
		{
			var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
			var enable = WindowsFirewallGate.BuildEnableScript(executable, 9443, [IPAddress.Parse("203.0.113.7")], statePath);
			var restore = WindowsFirewallGate.BuildRestoreScript(statePath);
			var result = await RunFirewallSimulationAsync(enable, restore, statePath, includePolicyAllow: false);
			Assert.IsNull(result.RootElement.GetProperty("error").GetString(), $"Activation failed: {result.RootElement.GetProperty("error").GetString()}");
			var active = result.RootElement.GetProperty("active");
			Assert.AreEqual("Block", active.GetProperty("profiles")[0].GetProperty("defaultOutboundAction").GetString());
			var activeRules = active.GetProperty("rules").EnumerateArray().ToArray();
			Assert.IsTrue(activeRules.Any(rule => rule.GetProperty("displayName").GetString() == "Existing outbound allow" && !rule.GetProperty("enabled").GetBoolean()));
			var gatewayRule = activeRules.Single(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Gateway only");
			Assert.IsTrue(gatewayRule.GetProperty("enabled").GetBoolean());
			Assert.AreEqual("203.0.113.7", gatewayRule.GetProperty("remoteAddresses").GetString());
			Assert.IsTrue(active.GetProperty("recoveryFileExists").GetBoolean());

			var restored = result.RootElement.GetProperty("restored");
			Assert.AreEqual("Allow", restored.GetProperty("profiles")[0].GetProperty("defaultOutboundAction").GetString());
			var restoredRules = restored.GetProperty("rules").EnumerateArray().ToArray();
			Assert.IsTrue(restoredRules.Single(rule => rule.GetProperty("displayName").GetString() == "Existing outbound allow").GetProperty("enabled").GetBoolean());
			Assert.IsFalse(restoredRules.Any(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Gateway only"));
			Assert.IsFalse(restored.GetProperty("recoveryFileExists").GetBoolean());
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
		}
	}

	[TestMethod]
	public async Task ActivationRollsBackWhenPolicyLeavesAnotherOutboundAllowRule()
	{
		var folder = Path.Combine(Path.GetTempPath(), "killswitch-firewall-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		var statePath = Path.Combine(folder, "firewall.json");
		try
		{
			var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
			var enable = WindowsFirewallGate.BuildEnableScript(executable, 443, [IPAddress.Parse("203.0.113.7")], statePath);
			var result = await RunFirewallSimulationAsync(enable, restoreScript: null, statePath, includePolicyAllow: true);
			StringAssert.Contains(result.RootElement.GetProperty("error").GetString()!, "Other outbound allow rules remain active");
			Assert.AreEqual("Allow", result.RootElement.GetProperty("profiles")[0].GetProperty("defaultOutboundAction").GetString());
			Assert.IsTrue(result.RootElement.GetProperty("rules").EnumerateArray().Single(rule => rule.GetProperty("displayName").GetString() == "Existing outbound allow").GetProperty("enabled").GetBoolean());
			Assert.IsFalse(result.RootElement.GetProperty("rules").EnumerateArray().Any(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Gateway only"));
			Assert.IsFalse(result.RootElement.GetProperty("recoveryFileExists").GetBoolean());
			Assert.IsTrue(result.RootElement.GetProperty("rules").EnumerateArray().Single(rule => rule.GetProperty("displayName").GetString() == "Group policy allow").GetProperty("enabled").GetBoolean());
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
		}
	}

	private static async Task<JsonDocument> RunFirewallSimulationAsync(string enableScript, string? restoreScript, string statePath, bool includePolicyAllow, bool includeDynamicIcs = false)
	{
		var enableBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(enableScript));
		var restoreBase64 = restoreScript is null ? "" : Convert.ToBase64String(Encoding.Unicode.GetBytes(restoreScript));
		var recoveryPathBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(statePath));
		var policyFlag = includePolicyAllow ? "$true" : "$false";
		var dynamicIcsFlag = includeDynamicIcs ? "$true" : "$false";
		var harness = """
$script:firewallProfiles = [System.Collections.Generic.List[object]]::new()
foreach ($name in @('Domain', 'Private', 'Public')) {
  $script:firewallProfiles.Add([pscustomobject]@{ Name = $name; Enabled = $true; DefaultOutboundAction = 'Allow' })
}
$script:firewallRules = [System.Collections.Generic.List[object]]::new()
$script:firewallRules.Add([pscustomobject]@{ Name = 'LocalExisting'; DisplayName = 'Existing outbound allow'; Enabled = $true; Direction = 'Outbound'; Action = 'Allow'; PolicyStoreSourceType = 'Local'; ApplicationName = 'example.exe'; RemoteAddresses = 'Any'; RemotePort = 'Any' })
if (__POLICY_ALLOW__) { $script:firewallRules.Add([pscustomobject]@{ Name = 'GroupPolicyAllow'; DisplayName = 'Group policy allow'; Enabled = $true; Direction = 'Outbound'; Action = 'Allow'; PolicyStoreSourceType = 'GroupPolicy'; ApplicationName = 'other.exe'; RemoteAddresses = 'Any'; RemotePort = 'Any' }) }
if (__DYNAMIC_ICS_ALLOW__) { $script:firewallRules.Add([pscustomobject]@{ Name = 'SharedAccessDynamic'; DisplayName = 'Internet Connection Sharing (SharedAccess-Out)'; Enabled = $true; Direction = 'Outbound'; Action = 'Allow'; PolicyStoreSourceType = 'Dynamic'; Service = 'SharedAccess'; ApplicationName = 'svchost.exe'; RemoteAddresses = 'Any'; RemotePort = 'Any' }) }
function Get-NetFirewallProfile {
  [CmdletBinding()]
  param([string]$PolicyStore)
  return $script:firewallProfiles.ToArray()
}
function Get-NetFirewallRule {
  [CmdletBinding()]
  param([string]$PolicyStore, [object]$Enabled, [string]$Direction, [string]$Action, [string]$DisplayName, [string]$Name)
  $matches = @($script:firewallRules.ToArray())
  if ($PolicyStore -eq 'PersistentStore') { $matches = @($matches | Where-Object { $_.PolicyStoreSourceType -eq 'Local' }) }
  if ($DisplayName) { $matches = @($matches | Where-Object { $_.DisplayName -eq $DisplayName }) }
  if ($Name) { $matches = @($matches | Where-Object { $_.Name -eq $Name }) }
  if ($null -ne $Enabled) { $matches = @($matches | Where-Object { $_.Enabled -eq [bool]$Enabled }) }
  if ($Direction) { $matches = @($matches | Where-Object { $_.Direction -eq $Direction }) }
  if ($Action) { $matches = @($matches | Where-Object { $_.Action -eq $Action }) }
  return $matches
}
function Get-NetFirewallServiceFilter {
  [CmdletBinding()]
  param([Parameter(ValueFromPipeline)] [object]$InputObject)
  process { [pscustomobject]@{ Service = $InputObject.Service } }
}
function Set-NetFirewallRule {
  [CmdletBinding()]
  param([string]$PolicyStore, [string[]]$Name, [object]$Enabled)
  if ($Enabled -is [bool]) { throw 'Enabled must be passed using the Windows firewall enum string.' }
  foreach ($rule in $script:firewallRules) { if ($Name -contains $rule.Name) { $rule.Enabled = ($Enabled -eq 'True') } }
}
function New-NetFirewallRule {
  [CmdletBinding()]
  param([string]$DisplayName, [string]$Group, [string]$Direction, [string]$Action, [string]$Program, [string]$Service, [string[]]$RemoteAddress, [string]$Protocol, [string]$RemotePort, [string[]]$Profile, [string]$Enabled)
  $script:firewallRules.Add([pscustomobject]@{ Name = [guid]::NewGuid().ToString(); DisplayName = $DisplayName; Enabled = ($Enabled -eq 'True'); Direction = $Direction; Action = $Action; PolicyStoreSourceType = 'Local'; ApplicationName = $Program; Service = $Service; RemoteAddresses = ($RemoteAddress -join ','); RemotePort = $RemotePort })
}
function Set-NetFirewallProfile {
  [CmdletBinding()]
  param([string[]]$Profile, [string]$DefaultOutboundAction)
  foreach ($name in $Profile) { foreach ($item in $script:firewallProfiles) { if ($item.Name -eq $name) { $item.DefaultOutboundAction = $DefaultOutboundAction } } }
}
function Remove-NetFirewallRule {
  [CmdletBinding()]
  param([string]$DisplayName)
  for ($index = $script:firewallRules.Count - 1; $index -ge 0; $index--) { if ($script:firewallRules[$index].DisplayName -eq $DisplayName) { $script:firewallRules.RemoveAt($index) } }
}
$enableScript = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__ENABLE_SCRIPT__'))
$restoreScript = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__RESTORE_SCRIPT__'))
$statePath = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__STATE_PATH__'))
$failure = $null
try { [void]([scriptblock]::Create($enableScript)).Invoke() } catch { $failure = "$($_.Exception.Message) $($_.InvocationInfo.PositionMessage) $($_.ScriptStackTrace)" }
$active = [ordered]@{ profiles = @($script:firewallProfiles | ForEach-Object { [ordered]@{ name = $_.Name; defaultOutboundAction = $_.DefaultOutboundAction } }); rules = @($script:firewallRules | ForEach-Object { [ordered]@{ name = $_.Name; displayName = $_.DisplayName; enabled = $_.Enabled; action = $_.Action; service = $_.Service; applicationName = $_.ApplicationName; remoteAddresses = $_.RemoteAddresses; remotePort = $_.RemotePort } }); recoveryFileExists = Test-Path -LiteralPath $statePath }
if ($restoreScript) { [void]([scriptblock]::Create($restoreScript)).Invoke() }
$restored = [ordered]@{ profiles = @($script:firewallProfiles | ForEach-Object { [ordered]@{ name = $_.Name; defaultOutboundAction = $_.DefaultOutboundAction } }); rules = @($script:firewallRules | ForEach-Object { [ordered]@{ name = $_.Name; displayName = $_.DisplayName; enabled = $_.Enabled; action = $_.Action; service = $_.Service; applicationName = $_.ApplicationName; remoteAddresses = $_.RemoteAddresses; remotePort = $_.RemotePort } }); recoveryFileExists = Test-Path -LiteralPath $statePath }
[ordered]@{ error = $failure; active = $active; restored = $restored; profiles = $restored.profiles; rules = $restored.rules; recoveryFileExists = $restored.recoveryFileExists } | ConvertTo-Json -Depth 8 -Compress
""";
		harness = harness.Replace("__POLICY_ALLOW__", policyFlag, StringComparison.Ordinal)
			.Replace("__DYNAMIC_ICS_ALLOW__", dynamicIcsFlag, StringComparison.Ordinal)
			.Replace("__ENABLE_SCRIPT__", enableBase64, StringComparison.Ordinal)
			.Replace("__RESTORE_SCRIPT__", restoreBase64, StringComparison.Ordinal)
			.Replace("__STATE_PATH__", recoveryPathBase64, StringComparison.Ordinal);

		var start = new ProcessStartInfo(GetPowerShellPath())
		{
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add("-NoProfile");
		start.ArgumentList.Add("-NonInteractive");
		var harnessPath = statePath + ".ps1";
		await File.WriteAllTextAsync(harnessPath, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		start.ArgumentList.Add("-File");
		start.ArgumentList.Add(harnessPath);
		try
		{
			using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			await process.WaitForExitAsync(timeout.Token);
			var output = await stdout;
			var error = await stderr;
			Assert.AreEqual(0, process.ExitCode, $"Firewall policy simulation failed: {error}");
			return JsonDocument.Parse(output);
		}
		finally
		{
			if (File.Exists(harnessPath)) File.Delete(harnessPath);
		}
	}

	private static async Task ParseWithWindowsPowerShell(string script)
	{
		var start = new ProcessStartInfo(GetPowerShellPath())
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add("-NoProfile");
		start.ArgumentList.Add("-NonInteractive");
		start.ArgumentList.Add("-Command");
		start.ArgumentList.Add("$tokens=$null; $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$tokens, [ref]$errors); if ($errors.Count) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
		await process.StandardInput.WriteAsync(script);
		process.StandardInput.Close();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		await process.WaitForExitAsync(timeout.Token);
		var error = await process.StandardError.ReadToEndAsync(timeout.Token);
		var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
		Assert.AreEqual(0, process.ExitCode, $"PowerShell syntax error: {error} {output}");
	}

	private static string GetPowerShellPath() => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
