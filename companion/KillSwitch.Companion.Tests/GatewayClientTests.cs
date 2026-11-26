using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class GatewayClientTests
{
	[TestMethod]
	public async Task SyncPolicyUsesAuthenticatedTlsApiShape()
	{
		var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
		using var client = new GatewayClient(new Uri("https://gateway.example/base"), "secret", handler);
		await client.SyncPolicyAsync([new AccessRule { Target = "example.com", IpConditions = "203.0.113.10|" }]);

		Assert.AreEqual(HttpMethod.Put, handler.Method);
		Assert.AreEqual("https://gateway.example/base/v1/policy", handler.RequestUri?.AbsoluteUri);
		Assert.AreEqual("Bearer secret", handler.Authorization);
		Assert.IsTrue(handler.ConnectionClose, "Policy sync must use a fresh gateway peer connection.");
		using var body = JsonDocument.Parse(handler.Body!);
		Assert.AreEqual("example.com", body.RootElement[0].GetProperty("target").GetString());
		Assert.AreEqual("203.0.113.10|", body.RootElement[0].GetProperty("ipConditions").GetString());
	}

	[TestMethod]
	public async Task FetchSendsOneTargetRequestAndReturnsBlockedStatus()
	{
		var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.UnavailableForLegalReasons)
		{
			Content = new StringContent("Access limited by Kill Switch")
		});
		using var client = new GatewayClient(new Uri("https://gateway.example/"), "secret", handler);
		var response = await client.FetchAsync(
			new Uri("https://example.com/private?mode=1"),
			"POST",
			new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain"] },
			"payload"u8.ToArray());
		await using (response)
		{

			Assert.AreEqual(HttpMethod.Post, handler.Method);
			Assert.IsTrue(handler.ConnectionClose);
			Assert.AreEqual(System.Net.HttpVersion.Version11, handler.Version);
			Assert.AreEqual(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
			using var reader = new StreamReader(response.Body);
			Assert.AreEqual("Access limited by Kill Switch", await reader.ReadToEndAsync());
			using var body = JsonDocument.Parse(handler.Body!);
			Assert.AreEqual("https://example.com/private?mode=1", body.RootElement.GetProperty("url").GetString());
			Assert.AreEqual("payload", System.Text.Encoding.UTF8.GetString(body.RootElement.GetProperty("body").GetBytesFromBase64()));
		}
	}

	[TestMethod]
	public void RejectsNonTlsGatewayEndpoint()
	{
		Assert.ThrowsExactly<ArgumentException>(() => new GatewayClient(new Uri("http://gateway.example"), "secret"));
	}

	[TestMethod]
	public async Task ReadsThePublicIpObservedByTheGateway()
	{
		var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("198.51.100.20")
		});
		using var client = new GatewayClient(new Uri("https://gateway.example/"), "secret", handler);
		Assert.AreEqual("198.51.100.20", await client.GetObservedPublicIpAsync());
		Assert.AreEqual(HttpMethod.Get, handler.Method);
		Assert.AreEqual("https://gateway.example/v1/ip", handler.RequestUri?.AbsoluteUri);
		Assert.IsTrue(handler.ConnectionClose);
	}

	[TestMethod]
	public async Task ResolvedGatewayConnectorUsesPinnedAddressWithoutResolvingHostnameAgain()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			var endpoint = (IPEndPoint)listener.LocalEndpoint;
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			var clientConnection = ResolvedGatewayConnector.ConnectAsync(
				new DnsEndPoint("this-host-must-not-resolve.invalid", endpoint.Port),
				[IPAddress.Loopback],
				timeout.Token).AsTask();
			using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
			await using var clientStream = await clientConnection;
			Assert.IsTrue(accepted.Connected);
		}
		finally
		{
			listener.Stop();
		}
	}

	private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public HttpMethod? Method { get; private set; }
		public Uri? RequestUri { get; private set; }
		public string? Authorization { get; private set; }
		public string? Body { get; private set; }
		public bool ConnectionClose { get; private set; }
		public Version? Version { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Method = request.Method;
			RequestUri = request.RequestUri;
			Authorization = request.Headers.Authorization?.ToString();
			ConnectionClose = request.Headers.ConnectionClose == true;
			Version = request.Version;
			Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			return respond(request);
		}
	}
}
