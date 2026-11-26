using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace KillSwitch.Companion;

/// <summary>Temporarily routes applications that honor the current user's Windows proxy settings.</summary>
internal sealed class WindowsUserProxyController
{
	private const string SettingsSubKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
	private const int InternetOptionSettingsChanged = 39;
	private const int InternetOptionRefresh = 37;
	private static readonly string BackupPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitch", "proxy-backup.json");
	private static readonly string CertificatePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitch", "inspection-ca.txt");

	public WindowsUserProxyController()
	{
		RestoreStaleSettings();
		RemoveStaleInspectionCertificate();
	}

	public void RecordInspectionCertificate(X509Certificate2 certificate)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
		File.WriteAllText(CertificatePath, certificate.Thumbprint);
	}

	public void RemoveInspectionCertificate(X509Certificate2 certificate)
	{
		RemoveCertificateByThumbprint(certificate.Thumbprint);
		if (File.Exists(CertificatePath)) File.Delete(CertificatePath);
	}

	public void Enable(int port)
	{
		if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
		Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
		if (!File.Exists(BackupPath)) File.WriteAllText(BackupPath, JsonSerializer.Serialize(Capture($"127.0.0.1:{port}")));
		using var settings = Registry.CurrentUser.OpenSubKey(SettingsSubKey, writable: true)
			?? Registry.CurrentUser.CreateSubKey(SettingsSubKey, writable: true);
		settings.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
		settings.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
		settings.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
		NotifyWindows();
	}

	public void Restore()
	{
		if (!File.Exists(BackupPath)) return;
		var backup = JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(BackupPath));
		if (backup is not null)
		{
			var current = Capture(activeProxyServer: null);
			if (current.ProxyEnable == 1 && current.ProxyServer == backup.ActiveProxyServer)
				Apply(backup);
		}
		File.Delete(BackupPath);
		NotifyWindows();
	}

	private void RestoreStaleSettings()
	{
		try { Restore(); }
		catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Could not restore saved Windows proxy settings: {error}"); }
	}

	private void RemoveStaleInspectionCertificate()
	{
		try
		{
			if (!File.Exists(CertificatePath)) return;
			var thumbprint = File.ReadAllText(CertificatePath).Trim();
			if (thumbprint.Length > 0) RemoveCertificateByThumbprint(thumbprint);
			File.Delete(CertificatePath);
		}
		catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Could not remove stale local inspection CA: {error}"); }
	}

	private static void RemoveCertificateByThumbprint(string thumbprint)
	{
		using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
		store.Open(OpenFlags.ReadWrite);
		var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
		foreach (var match in matches) store.Remove(match);
	}

	private static ProxySettings Capture(string? activeProxyServer)
	{
		using var settings = Registry.CurrentUser.OpenSubKey(SettingsSubKey, writable: false);
		return new ProxySettings(
			settings?.GetValue("ProxyEnable") as int?,
			settings?.GetValue("ProxyServer") as string,
			settings?.GetValue("ProxyOverride") as string,
			settings?.GetValue("AutoConfigURL") as string,
			settings?.GetValue("AutoDetect") as int?,
			activeProxyServer);
	}

	private static void Apply(ProxySettings values)
	{
		using var settings = Registry.CurrentUser.OpenSubKey(SettingsSubKey, writable: true)
			?? Registry.CurrentUser.CreateSubKey(SettingsSubKey, writable: true);
		SetOrDelete(settings, "ProxyEnable", values.ProxyEnable, RegistryValueKind.DWord);
		SetOrDelete(settings, "ProxyServer", values.ProxyServer, RegistryValueKind.String);
		SetOrDelete(settings, "ProxyOverride", values.ProxyOverride, RegistryValueKind.String);
		SetOrDelete(settings, "AutoConfigURL", values.AutoConfigUrl, RegistryValueKind.String);
		SetOrDelete(settings, "AutoDetect", values.AutoDetect, RegistryValueKind.DWord);
	}

	private static void SetOrDelete(RegistryKey settings, string name, object? value, RegistryValueKind kind)
	{
		if (value is null) settings.DeleteValue(name, throwOnMissingValue: false);
		else settings.SetValue(name, value, kind);
	}

	private static void NotifyWindows()
	{
		_ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
		_ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
	}

	[DllImport("wininet.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

	private sealed record ProxySettings(int? ProxyEnable, string? ProxyServer, string? ProxyOverride, string? AutoConfigUrl, int? AutoDetect, string? ActiveProxyServer);
}
