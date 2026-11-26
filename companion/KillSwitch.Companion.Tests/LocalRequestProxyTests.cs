using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class LocalRequestProxyTests
{
	[TestMethod]
	public async Task ReturnsGatewayLimitResponseToTheClient()
	{
		var forwarder = new DenyingForwarder();
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		await using var proxy = new LocalRequestProxy(forwarder, authority);
		await proxy.StartAsync();
		using var tcp = new TcpClient();
		await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
		var network = tcp.GetStream();
		await network.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/private HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"));
		var response = await ReadResponseAsync(network);

		StringAssert.StartsWith(response.Status, "HTTP/1.1 451");
		Assert.AreEqual("Access limited by Kill Switch", response.Body);
		Assert.AreEqual("http://example.test/private", forwarder.Url);
	}

	[TestMethod]
	public async Task FailsClosedWhenTheGatewayCannotBeReached()
	{
		var forwarder = new FailingForwarder();
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		await using var proxy = new LocalRequestProxy(forwarder, authority);
		await proxy.StartAsync();
		using var tcp = new TcpClient();
		await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
		var network = tcp.GetStream();
		await network.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/private HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"));
		var response = await ReadResponseAsync(network);

		StringAssert.StartsWith(response.Status, "HTTP/1.1 502");
		Assert.AreEqual("Kill Switch gateway unavailable", response.Body);
	}

	[TestMethod]
	public async Task PausedPolicyBlocksRequestsBeforeTheyReachTheGateway()
	{
		var forwarder = new RecordingForwarder();
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		await using var proxy = new LocalRequestProxy(forwarder, authority);
		proxy.SetPolicyReady(false);
		await proxy.StartAsync();
		using var tcp = new TcpClient();
		await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
		var network = tcp.GetStream();
		await network.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/private HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"));
		var response = await ReadResponseAsync(network);

		StringAssert.StartsWith(response.Status, "HTTP/1.1 503");
		Assert.AreEqual("Kill Switch policy sync failed; request blocked", response.Body);
		Assert.HasCount(0, forwarder.Urls);
	}

	[TestMethod]
	public async Task HttpsConnectPreservesCoalescedTlsHandshakeAndForwardsIndividualUrl()
	{
		var openssl = Environment.GetEnvironmentVariable("OPENSSL_PATH") ?? "openssl";
		var forwarder = new RecordingForwarder();
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		await using var proxy = new LocalRequestProxy(forwarder, authority);
		Exception? proxyError = null;
		proxy.ConnectionError += error => proxyError = error;
		await proxy.StartAsync();
		var caPath = Path.Combine(Path.GetTempPath(), $"killswitch-test-ca-{Guid.NewGuid():N}.pem");
		await File.WriteAllTextAsync(caPath, authority.Certificate.ExportCertificatePem());
		try
		{
			var startInfo = new ProcessStartInfo(openssl)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			foreach (var argument in new[] { "s_client", "-proxy", $"127.0.0.1:{proxy.Port}", "-connect", "example.test:443", "-servername", "example.test", "-CAfile", caPath, "-quiet" })
				startInfo.ArgumentList.Add(argument);

			using var process = new Process { StartInfo = startInfo };
			try { process.Start(); }
			catch (System.ComponentModel.Win32Exception)
			{
				Assert.Inconclusive("OpenSSL is required for the local TLS proxy integration test.");
				return;
			}
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			await process.StandardInput.WriteAsync("GET /private?view=full HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n");
			process.StandardInput.Close();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			await process.WaitForExitAsync(timeout.Token);
			var response = await stdout;
			var errors = await stderr;
			if (proxyError is System.Security.Cryptography.CryptographicException
				&& proxyError.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
				Assert.Inconclusive("This environment cannot persist the user-scoped Schannel key required by the HTTPS interception test.");

			Assert.AreEqual(0, process.ExitCode, $"{errors} Proxy error: {proxyError}");
			StringAssert.Contains(response, "HTTP/1.1 200 OK");
			StringAssert.Contains(response, "first");
			Assert.HasCount(1, forwarder.Urls);
			Assert.AreEqual("https://example.test/private?view=full", forwarder.Urls[0]);
		}
		finally
		{
			if (File.Exists(caPath)) File.Delete(caPath);
		}
	}

	[TestMethod]
	public async Task EachHttpRequestIsSentToTheGateway()
	{
		var forwarder = new RecordingForwarder();
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		await using var proxy = new LocalRequestProxy(forwarder, authority);
		await proxy.StartAsync();
		using var tcp = new TcpClient();
		await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
		var network = tcp.GetStream();
		await network.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/private?view=full HTTP/1.1\r\nHost: example.test\r\nConnection: keep-alive\r\n\r\n"));
		var first = await ReadResponseAsync(network);
		Assert.AreEqual("first", first.Body);
		await network.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/private/child HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"));
		var second = await ReadResponseAsync(network);
		Assert.AreEqual("second", second.Body);

		Assert.HasCount(2, forwarder.Urls);
		Assert.AreEqual("http://example.test/private?view=full", forwarder.Urls[0]);
		Assert.AreEqual("http://example.test/private/child", forwarder.Urls[1]);
	}

	private static async Task<string> ReadHeadersAsync(Stream stream)
	{
		using var data = new MemoryStream();
		var one = new byte[1];
		while (true)
		{
			await stream.ReadExactlyAsync(one);
			data.WriteByte(one[0]);
			var bytes = data.GetBuffer();
			var length = (int)data.Length;
			if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
				return Encoding.ASCII.GetString(bytes, 0, length);
		}
	}

	private static async Task<(string Status, string Body)> ReadResponseAsync(Stream stream)
	{
		var headerText = await ReadHeadersAsync(stream);
		var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		byte[] body;
		if (lines.Any(line => line.StartsWith("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)))
		{
			using var output = new MemoryStream();
			while (true)
			{
				var sizeLine = await ReadLineAsync(stream);
				var size = int.Parse(sizeLine, System.Globalization.NumberStyles.HexNumber);
				if (size == 0)
				{
					while ((await ReadLineAsync(stream)).Length > 0) { }
					break;
				}
				var chunk = new byte[size];
				await stream.ReadExactlyAsync(chunk);
				await output.WriteAsync(chunk);
				Assert.AreEqual("", await ReadLineAsync(stream));
			}
			body = output.ToArray();
		}
		else
		{
			var lengthHeader = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidDataException($"Response omitted content framing: {headerText}");
			var length = int.Parse(lengthHeader[(lengthHeader.IndexOf(':') + 1)..].Trim());
			body = new byte[length];
			await stream.ReadExactlyAsync(body);
		}
		return (lines[0], Encoding.UTF8.GetString(body));
	}

	private static async Task<string> ReadLineAsync(Stream stream)
	{
		using var line = new MemoryStream();
		var one = new byte[1];
		while (true)
		{
			await stream.ReadExactlyAsync(one);
			if (one[0] == '\n')
			{
				var value = line.ToArray();
				return Encoding.ASCII.GetString(value, 0, value.Length > 0 && value[^1] == '\r' ? value.Length - 1 : value.Length);
			}
			line.WriteByte(one[0]);
		}
	}

	private sealed class RecordingForwarder : IRequestForwarder
	{
		private int _requestCount;
		public List<string> Urls { get; } = [];

		public Task<GatewayFetchResponse> FetchAsync(Uri target, string method, IReadOnlyDictionary<string, string[]> headers, byte[] body, CancellationToken cancellationToken = default)
		{
			Urls.Add(target.AbsoluteUri);
			var content = new StringContent(_requestCount++ == 0 ? "first" : "second");
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
			var responseHeaders = response.Headers.Concat(content.Headers)
				.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
			return Task.FromResult(new GatewayFetchResponse(response, response.StatusCode, responseHeaders, content.ReadAsStream()));
		}
	}

	private sealed class DenyingForwarder : IRequestForwarder
	{
		public string? Url { get; private set; }
		public Task<GatewayFetchResponse> FetchAsync(Uri target, string method, IReadOnlyDictionary<string, string[]> headers, byte[] body, CancellationToken cancellationToken = default)
		{
			Url = target.AbsoluteUri;
			var response = new HttpResponseMessage(HttpStatusCode.UnavailableForLegalReasons) { Content = new StringContent("Access limited by Kill Switch") };
			var responseHeaders = response.Headers.Concat(response.Content.Headers).ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
			return Task.FromResult(new GatewayFetchResponse(response, response.StatusCode, responseHeaders, response.Content.ReadAsStream()));
		}
	}

	private sealed class FailingForwarder : IRequestForwarder
	{
		public Task<GatewayFetchResponse> FetchAsync(Uri target, string method, IReadOnlyDictionary<string, string[]> headers, byte[] body, CancellationToken cancellationToken = default) =>
			Task.FromException<GatewayFetchResponse>(new HttpRequestException("Gateway offline"));
	}
}
