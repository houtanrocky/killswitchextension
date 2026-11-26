using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class WindowsFirewallLiveIntegrationTests
{
	private const string RuleName = "Kill Switch Companion - Gateway only";

	[TestMethod]
	[TestCategory("WindowsFirewallIntegration")]
	public async Task VerifiesGatewayOnlyPolicyOrSafelyRejectsUnmodifiableExceptions()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("The live firewall test runs on Windows only.");
			return;
		}
		if (Environment.GetEnvironmentVariable("KILLSWITCH_RUN_WINDOWS_FIREWALL_INTEGRATION") != "1")
		{
			Assert.Inconclusive("Set KILLSWITCH_RUN_WINDOWS_FIREWALL_INTEGRATION=1 and run the test process elevated to change Windows Firewall temporarily.");
			return;
		}
		if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
		{
			Assert.Inconclusive("Run this integration test from an elevated process.");
			return;
		}
		if (WindowsFirewallGate.HasRecoveryState)
			Assert.Fail("A prior Kill Switch firewall recovery record exists. Restore it before running the integration test.");

		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path unavailable.");
		using var before = await QueryPersistentFirewallStateAsync();
		using var beforeActive = await QueryActiveFirewallStateAsync();
		InvalidOperationException? policyRefusal = null;
		try
		{
			try { await WindowsFirewallGate.EnableAsync(executable, 9443, [IPAddress.Loopback]); }
			catch (InvalidOperationException error) when (error.Message.Contains("Other outbound allow rules remain active", StringComparison.OrdinalIgnoreCase))
			{
				policyRefusal = error;
			}
			if (policyRefusal is null)
			{
				using var active = await QueryActiveFirewallStateAsync();
				var profiles = active.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
				Assert.HasCount(3, profiles);
				Assert.IsTrue(profiles.All(profile => profile.GetProperty("enabled").GetBoolean()
					&& profile.GetProperty("defaultOutboundAction").GetString() == "Block"));
				var rules = active.RootElement.GetProperty("allows").EnumerateArray().ToArray();
				Assert.HasCount(2, rules, "Only the gateway exception and the overridden SharedAccess dynamic exception may remain as allows.");
				var gatewayRule = rules.Single(rule => rule.GetProperty("displayName").GetString() == RuleName);
				Assert.AreEqual(RuleName, gatewayRule.GetProperty("displayName").GetString());
				Assert.IsTrue(rules.Any(rule => rule.GetProperty("displayName").GetString() == "Internet Connection Sharing (SharedAccess-Out)"));
				var icsBlockRule = active.RootElement.GetProperty("blocks").EnumerateArray()
					.Single(rule => rule.GetProperty("displayName").GetString() == "Kill Switch Companion - Block Internet Connection Sharing egress");
				Assert.AreEqual("SharedAccess", icsBlockRule.GetProperty("service").GetString());
			}
		}
		finally
		{
			if (WindowsFirewallGate.HasRecoveryState)
				await WindowsFirewallGate.RestoreAsync();
		}

		Assert.IsFalse(WindowsFirewallGate.HasRecoveryState, "The recovery record must be removed after restoration.");
		if (policyRefusal is not null)
		{
			using var afterRefusal = await QueryActiveFirewallStateAsync();
			using var persistentAfterRefusal = await QueryPersistentFirewallStateAsync();
			Assert.AreEqual(beforeActive.RootElement.GetRawText(), afterRefusal.RootElement.GetRawText(), "An unmodifiable allow exception must cause a no-change refusal.");
			Assert.AreEqual(before.RootElement.GetRawText(), persistentAfterRefusal.RootElement.GetRawText(), "A policy refusal must preserve persistent firewall settings.");
			return;
		}
		using var after = await QueryPersistentFirewallStateAsync();
		Assert.AreEqual(before.RootElement.GetRawText(), after.RootElement.GetRawText(), "Persistent firewall profile actions and enabled outbound allow rules must be restored.");
		using var activeAfter = await QueryActiveFirewallStateAsync();
		Assert.AreEqual(beforeActive.RootElement.GetRawText(), activeAfter.RootElement.GetRawText(), "Active profiles and allow rules must return to their original state, with no Kill Switch firewall rules left behind.");
	}

	private static Task<JsonDocument> QueryActiveFirewallStateAsync() => RunPowerShellJsonAsync("""
$profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Sort-Object Name | ForEach-Object { [pscustomobject]@{ name=$_.Name; enabled=[bool]$_.Enabled; defaultOutboundAction=[string]$_.DefaultOutboundAction } })
$allows = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Outbound -Action Allow | Sort-Object DisplayName | ForEach-Object { [pscustomobject]@{ displayName=$_.DisplayName } })
$icsBlock = Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Outbound -Action Block | Where-Object { $_.DisplayName -eq 'Kill Switch Companion - Block Internet Connection Sharing egress' }
$blocks = @($icsBlock | ForEach-Object { $rule=$_; $service=$rule | Get-NetFirewallServiceFilter; [pscustomobject]@{ displayName=$rule.DisplayName; service=$service.Service } })
[ordered]@{ profiles=$profiles; allows=$allows; blocks=$blocks } | ConvertTo-Json -Depth 5 -Compress
""");

	private static Task<JsonDocument> QueryPersistentFirewallStateAsync() => RunPowerShellJsonAsync("""
$profiles = @(Get-NetFirewallProfile -PolicyStore PersistentStore | Sort-Object Name | ForEach-Object { [pscustomobject]@{ name=$_.Name; action=[string]$_.DefaultOutboundAction } })
$allows = @(Get-NetFirewallRule -PolicyStore PersistentStore -Enabled True -Direction Outbound -Action Allow | Sort-Object Name | ForEach-Object { [pscustomobject]@{ name=$_.Name; enabled=[bool]$_.Enabled } })
[ordered]@{ profiles=$profiles; allows=$allows } | ConvertTo-Json -Depth 5 -Compress
""");

	private static async Task<JsonDocument> RunPowerShellJsonAsync(string script)
	{
		var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
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
		start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await process.WaitForExitAsync(timeout.Token);
		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		Assert.AreEqual(0, process.ExitCode, stderr);
		return JsonDocument.Parse(stdout);
	}
}
