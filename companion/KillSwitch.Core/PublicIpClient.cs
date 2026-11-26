using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;

namespace KillSwitch.Core;

public sealed class PublicIpClient(HttpClient? httpClient = null)
{
	private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

	public async Task<string> GetCurrentIpAsync(CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org");
		request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
		using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		var value = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
		if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
			throw new InvalidDataException("The IP provider returned an invalid IPv4 address.");
		return value;
	}
}
