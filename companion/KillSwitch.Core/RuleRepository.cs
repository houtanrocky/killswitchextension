using System.IO;
using System.Text.Json;

namespace KillSwitch.Core;

public sealed class RuleRepository
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly string _filePath;

	public RuleRepository(string? filePath = null)
	{
		_filePath = filePath ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"KillSwitch",
			"rules.json");
	}

	public async Task<IReadOnlyList<AccessRule>> LoadAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_filePath)) return [];
		await using var stream = File.OpenRead(_filePath);
		return await JsonSerializer.DeserializeAsync<List<AccessRule>>(stream, JsonOptions, cancellationToken) ?? [];
	}

	public async Task SaveAsync(IEnumerable<AccessRule> rules, CancellationToken cancellationToken = default)
	{
		var directory = Path.GetDirectoryName(_filePath)!;
		Directory.CreateDirectory(directory);
		var tempPath = _filePath + ".tmp";
		await using (var stream = File.Create(tempPath))
		{
			await JsonSerializer.SerializeAsync(stream, rules, JsonOptions, cancellationToken);
		}
		File.Move(tempPath, _filePath, overwrite: true);
	}

	public static async Task<IReadOnlyList<AccessRule>> ImportAsync(string filePath, CancellationToken cancellationToken = default)
	{
		await using var stream = File.OpenRead(filePath);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		if (document.RootElement.ValueKind == JsonValueKind.Array)
			return JsonSerializer.Deserialize<List<AccessRule>>(document.RootElement.GetRawText(), JsonOptions) ?? [];

		if (document.RootElement.ValueKind != JsonValueKind.Object
			|| !document.RootElement.TryGetProperty("urls", out var urlsElement)
			|| !document.RootElement.TryGetProperty("allowed_ips", out var ipsElement)
			|| urlsElement.ValueKind != JsonValueKind.Array
			|| ipsElement.ValueKind != JsonValueKind.Array)
			throw new InvalidDataException("Expected a Kill Switch rules file.");

		var urls = urlsElement.EnumerateArray()
			.Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
			.ToArray();
		var ips = ipsElement.EnumerateArray()
			.Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
			.ToArray();
		return urls.Select((url, index) => new AccessRule
		{
			Target = url ?? "",
			IpConditions = (index < ips.Length ? ips[index] : "")?.Replace("|", "", StringComparison.Ordinal) ?? ""
		}).ToArray();
	}

	public static async Task ExportExtensionFormatAsync(string filePath, IEnumerable<AccessRule> rules, CancellationToken cancellationToken = default)
	{
		var values = rules.ToArray();
		var export = new
		{
			urls = values.Select(rule => rule.Target).ToArray(),
			allowed_ips = values.Select(rule => $"{rule.IpConditions.Trim().TrimEnd('|')}|").ToArray()
		};
		await using var stream = File.Create(filePath);
		await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken);
	}
}
