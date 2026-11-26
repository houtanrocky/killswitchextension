using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace KillSwitch.Core;

public interface IRequestForwarder
{
	Task<GatewayFetchResponse> FetchAsync(Uri target, string method, IReadOnlyDictionary<string, string[]> headers, byte[] body, CancellationToken cancellationToken = default);
}

public sealed class GatewayClient : IRequestForwarder, IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly HttpClient _httpClient;

	public GatewayClient(Uri endpoint, string bearerToken, HttpMessageHandler? handler = null, IReadOnlyCollection<IPAddress>? resolvedGatewayAddresses = null)
	{
		if (endpoint.Scheme != Uri.UriSchemeHttps)
			throw new ArgumentException("The gateway endpoint must use HTTPS.", nameof(endpoint));
		if (string.IsNullOrWhiteSpace(bearerToken))
			throw new ArgumentException("A gateway token is required.", nameof(bearerToken));

		_httpClient = new HttpClient(handler ?? CreateHandler(resolvedGatewayAddresses));
		_httpClient.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
		_httpClient.Timeout = TimeSpan.FromSeconds(60);
		_httpClient.DefaultRequestVersion = HttpVersion.Version11;
		_httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
		_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
	}

	public static async Task<string> RegisterDeviceAsync(Uri endpoint, CancellationToken cancellationToken = default)
	{
		if (endpoint.Scheme != Uri.UriSchemeHttps)
			throw new ArgumentException("The gateway endpoint must use HTTPS.", nameof(endpoint));
		using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
		{
			BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"),
			Timeout = TimeSpan.FromSeconds(20)
		};
		using var request = new HttpRequestMessage(HttpMethod.Post, "v1/register") { Content = JsonContent.Create(new { }) };
		request.Headers.ConnectionClose = true;
		using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		var result = await response.Content.ReadFromJsonAsync<RegistrationResponse>(JsonOptions, cancellationToken);
		if (result?.Token is not { Length: 64 } token || !token.All(Uri.IsHexDigit))
			throw new InvalidDataException("The managed gateway returned an invalid device key.");
		return token.ToLowerInvariant();
	}

	public async Task SyncPolicyAsync(IEnumerable<AccessRule> rules, CancellationToken cancellationToken = default)
	{
		var policy = rules.Select(rule => new GatewayRule(rule.Target, rule.IpConditions)).ToArray();
		using var request = new HttpRequestMessage(HttpMethod.Put, "v1/policy")
		{
			Content = JsonContent.Create(policy, options: JsonOptions)
		};
		request.Headers.ConnectionClose = true;
		using var response = await _httpClient.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
	}

	private sealed record RegistrationResponse(string Token);

	public async Task<string> GetObservedPublicIpAsync(CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "v1/ip");
		request.Headers.ConnectionClose = true;
		using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		var value = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
		if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
			throw new InvalidDataException("The gateway returned an invalid IPv4 address.");
		return address.ToString();
	}

	public async Task<GatewayFetchResponse> FetchAsync(
		Uri target,
		string method,
		IReadOnlyDictionary<string, string[]> headers,
		byte[] body,
		CancellationToken cancellationToken = default)
	{
		var payload = new GatewayFetchRequest(target.AbsoluteUri, method, headers, body);
		using var request = new HttpRequestMessage(HttpMethod.Post, "v1/fetch")
		{
			Content = JsonContent.Create(payload, options: JsonOptions)
		};
		request.Headers.ConnectionClose = true;
		var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		var responseHeaders = response.Headers.Concat(response.Content.Headers)
			.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
		var responseBody = await response.Content.ReadAsStreamAsync(cancellationToken);
		return new GatewayFetchResponse(response, response.StatusCode, responseHeaders, responseBody);
	}

	public void Dispose() => _httpClient.Dispose();

	private static HttpMessageHandler CreateHandler(IReadOnlyCollection<IPAddress>? addresses)
	{
		if (addresses is null || addresses.Count == 0) return new SocketsHttpHandler { UseProxy = false };
		var frozenAddresses = addresses.Distinct().ToArray();
		return new SocketsHttpHandler
		{
			UseProxy = false,
			ConnectCallback = (context, cancellationToken) => ResolvedGatewayConnector.ConnectAsync(context.DnsEndPoint, frozenAddresses, cancellationToken)
		};
	}
}

public static class ResolvedGatewayConnector
{
	public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken = default)
	{
		Exception? lastError = null;
		foreach (var address in addresses)
		{
			var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			try
			{
				await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch (Exception error) when (error is SocketException or OperationCanceledException)
			{
				socket.Dispose();
				lastError = error;
				if (error is OperationCanceledException) throw;
			}
		}
		throw new HttpRequestException("Could not connect to any resolved gateway address.", lastError);
	}
}

public sealed record GatewayRule(string Target, string IpConditions);
public sealed record GatewayFetchRequest(string Url, string Method, IReadOnlyDictionary<string, string[]> Headers, byte[] Body);
public sealed class GatewayFetchResponse(HttpResponseMessage response, HttpStatusCode statusCode, IReadOnlyDictionary<string, string[]> headers, Stream body) : IAsyncDisposable
{
	public HttpStatusCode StatusCode { get; } = statusCode;
	public IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
	public Stream Body { get; } = body;

	public async ValueTask DisposeAsync()
	{
		await Body.DisposeAsync();
		response.Dispose();
	}
}
