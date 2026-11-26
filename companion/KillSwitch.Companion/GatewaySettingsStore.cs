using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KillSwitch.Companion;

public sealed record GatewaySettings(Uri Endpoint, string BearerToken);

public sealed class GatewaySettingsStore
{
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KillSwitch.Companion.GatewayToken.v1");
	private static readonly string SettingsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"KillSwitch",
		"gateway.json");

	public GatewaySettings? Load()
	{
		if (!File.Exists(SettingsPath)) return null;
		var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
		if (stored is null || !Uri.TryCreate(stored.Endpoint, UriKind.Absolute, out var endpoint)) return null;
		var tokenBytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
		return new GatewaySettings(endpoint, Encoding.UTF8.GetString(tokenBytes));
	}

	public void Save(GatewaySettings settings)
	{
		if (settings.Endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The gateway must use HTTPS.", nameof(settings));
		if (string.IsNullOrWhiteSpace(settings.BearerToken)) throw new ArgumentException("Enter the gateway token.", nameof(settings));
		Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
		var protectedToken = ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.BearerToken), Entropy, DataProtectionScope.CurrentUser);
		var tempPath = SettingsPath + ".tmp";
		File.WriteAllText(tempPath, JsonSerializer.Serialize(new StoredSettings(settings.Endpoint.AbsoluteUri, Convert.ToBase64String(protectedToken)), new JsonSerializerOptions { WriteIndented = true }));
		File.Move(tempPath, SettingsPath, overwrite: true);
	}

	private sealed record StoredSettings(string Endpoint, string ProtectedToken);
}
