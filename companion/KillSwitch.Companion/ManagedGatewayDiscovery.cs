using System.Net.Http;
using System.Net.Http.Json;

namespace KillSwitch.Companion;

public sealed record ManagedGatewayConfiguration(bool Enabled, string? Endpoint);

public static class ManagedGatewayDiscovery
{
	private static readonly Uri ConfigurationUri = new("https://houtanrocky.github.io/killswitchextension/managed-gateway.json");
	private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

	public static async Task<Uri> GetEndpointAsync(CancellationToken cancellationToken = default)
	{
		using var response = await Client.GetAsync(ConfigurationUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		var configuration = await response.Content.ReadFromJsonAsync<ManagedGatewayConfiguration>(cancellationToken: cancellationToken);
		if (configuration is not { Enabled: true } || !Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
			throw new InvalidOperationException("The managed service is not available yet. Please try again later.");
		return endpoint;
	}
}
