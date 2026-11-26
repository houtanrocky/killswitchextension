using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KillSwitch.Core;

/// <summary>
/// Loopback HTTP/1.1 proxy. HTTPS CONNECT tunnels are terminated locally so each HTTP request URL
/// can be sent separately to the egress gate. The caller must prevent clients from bypassing it.
/// </summary>
public sealed class LocalRequestProxy(IRequestForwarder forwarder, TlsCertificateAuthority certificateAuthority) : IAsyncDisposable
{
	private const int MaxHeaderLineBytes = 16 * 1024;
	private const int MaxRequestBodyBytes = 16 * 1024 * 1024;
	private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
	{
		"Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
		"TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length"
	};
	private readonly IRequestForwarder _forwarder = forwarder;
	private readonly TlsCertificateAuthority _certificateAuthority = certificateAuthority;
	private readonly ConcurrentDictionary<int, Task> _connections = new();
	private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _serverCertificates = new(StringComparer.OrdinalIgnoreCase);
	private readonly CancellationTokenSource _shutdown = new();
	private TcpListener? _listener;
	private Task? _acceptLoop;
	private int _nextConnectionId;
	private int _acceptedConnectionCount;
	private int _policyReady = 1;

	public int Port { get; private set; }
	public int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);
	public void SetPolicyReady(bool ready) => Volatile.Write(ref _policyReady, ready ? 1 : 0);
	public event Action<Exception>? ConnectionError;

	public Task StartAsync(int port = 0)
	{
		if (_listener is not null) throw new InvalidOperationException("The local proxy is already running.");
		_listener = new TcpListener(IPAddress.Loopback, port);
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_acceptLoop = AcceptLoopAsync(_shutdown.Token);
		return Task.CompletedTask;
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await _listener!.AcceptTcpClientAsync(cancellationToken);
			}
			catch (OperationCanceledException) { break; }
			catch (ObjectDisposedException) { break; }
			catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }

			Interlocked.Increment(ref _acceptedConnectionCount);
			var id = Interlocked.Increment(ref _nextConnectionId);
			var connectionTask = HandleClientSafelyAsync(client, cancellationToken);
			_connections[id] = connectionTask;
			_ = connectionTask.ContinueWith(
				completed => _connections.TryRemove(id, out var removed),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
	}

	private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		{
			try
			{
				client.NoDelay = true;
				var networkStream = client.GetStream();
				var reader = new Http11Reader(networkStream);
				var head = await reader.ReadHeadAsync(cancellationToken);
				if (head is null) return;

				if (head.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
				{
					var connectUri = ParseConnectAuthority(head.Target);
					if (connectUri is null)
					{
						await WriteErrorAsync(networkStream, HttpStatusCode.BadRequest, "Invalid CONNECT target", cancellationToken);
						return;
					}
					await networkStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellationToken);
					var leaf = _serverCertificates.GetOrAdd(
						connectUri.Host,
						host => new Lazy<X509Certificate2>(() => _certificateAuthority.CreateServerCertificate(host), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
					using var tlsStream = new SslStream(reader.CreateRemainderStream(), leaveInnerStreamOpen: true);
					await tlsStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
					{
						ServerCertificate = leaf,
						EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
						ApplicationProtocols = [SslApplicationProtocol.Http11]
					}, cancellationToken);
					await ProcessRequestLoopAsync(new Http11Reader(tlsStream), tlsStream, "https", connectUri.Authority, cancellationToken);
					await tlsStream.ShutdownAsync();
					return;
				}

				await ProcessRequestLoopAsync(reader, networkStream, "http", null, cancellationToken, head);
			}
			catch (OperationCanceledException) { }
			catch (Exception error)
			{
				try { ConnectionError?.Invoke(error); } catch { }
			}
		}
	}

	private async Task ProcessRequestLoopAsync(Http11Reader reader, Stream clientStream, string scheme, string? authority, CancellationToken cancellationToken, RequestHead? firstHead = null)
	{
		var head = firstHead;
		while (!cancellationToken.IsCancellationRequested)
		{
			head ??= await reader.ReadHeadAsync(cancellationToken);
			if (head is null) return;
			if (head.Headers.ContainsKey("Upgrade"))
			{
				await WriteErrorAsync(clientStream, HttpStatusCode.NotImplemented, "Protocol upgrades are not supported by the gateway", cancellationToken);
				return;
			}

			if (head.Headers.TryGetValue("Expect", out var expectations) && expectations.Any(value => value.Equals("100-continue", StringComparison.OrdinalIgnoreCase)))
				await clientStream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);

			var body = await reader.ReadBodyAsync(head.Headers, MaxRequestBodyBytes, cancellationToken);
			var target = ResolveTarget(head, scheme, authority);
			if (target is null)
			{
				await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Invalid request URL", cancellationToken);
				return;
			}
			if (!target.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
			{
				await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Unsupported request scheme", cancellationToken);
				return;
			}

			var isCloseRequested = head.Version.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase)
				? !HasHeaderToken(head.Headers, "Connection", "keep-alive")
				: HasHeaderToken(head.Headers, "Connection", "close");
			try
			{
				if (Volatile.Read(ref _policyReady) == 0)
				{
					await WriteErrorAsync(clientStream, HttpStatusCode.ServiceUnavailable, "Kill Switch policy sync failed; request blocked", cancellationToken);
					return;
				}
				await using var response = await _forwarder.FetchAsync(target, head.Method, head.Headers, body, cancellationToken);
				await WriteResponseAsync(clientStream, response, isCloseRequested, cancellationToken);
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Kill Switch gateway unavailable", cancellationToken);
				return;
			}

			if (isCloseRequested) return;
			head = null;
		}
	}

	private static Uri? ResolveTarget(RequestHead head, string scheme, string? authority)
	{
		if (Uri.TryCreate(head.Target, UriKind.Absolute, out var absolute)) return absolute;
		var host = authority;
		if (string.IsNullOrWhiteSpace(host) && head.Headers.TryGetValue("Host", out var hosts)) host = hosts.LastOrDefault();
		if (string.IsNullOrWhiteSpace(host)) return null;
		return Uri.TryCreate($"{scheme}://{host}{head.Target}", UriKind.Absolute, out var target) ? target : null;
	}

	private static Uri? ParseConnectAuthority(string authority) => Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var uri) ? uri : null;

	private static bool HasHeaderToken(IReadOnlyDictionary<string, string[]> headers, string header, string token) =>
		headers.TryGetValue(header, out var values) && values.SelectMany(value => value.Split(',')).Any(value => value.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

	private static async Task WriteResponseAsync(Stream stream, GatewayFetchResponse response, bool close, CancellationToken cancellationToken)
	{
		var statusCode = (int)response.StatusCode;
		var reason = new HttpResponseMessage(response.StatusCode).ReasonPhrase ?? "Response";
		var noBody = statusCode is >= 100 and < 200 or 204 or 304;
		var hasLength = response.Headers.TryGetValue("Content-Length", out var lengths) && long.TryParse(lengths.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out _);
		var header = new StringBuilder($"HTTP/1.1 {statusCode} {reason}\r\n");
		foreach (var (name, values) in response.Headers)
		{
			if (HopByHopHeaders.Contains(name) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Contains('\r') || name.Contains('\n')) continue;
			foreach (var value in values)
				if (!value.Contains('\r') && !value.Contains('\n')) header.Append(name).Append(": ").Append(value).Append("\r\n");
		}
		if (noBody) header.Append("Content-Length: 0\r\n");
		else if (hasLength) header.Append("Content-Length: ").Append(lengths![0]).Append("\r\n");
		else header.Append("Transfer-Encoding: chunked\r\n");
		header.Append(close ? "Connection: close\r\n\r\n" : "Connection: keep-alive\r\n\r\n");
		await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken);
		if (noBody) return;
		var buffer = new byte[32 * 1024];
		while (true)
		{
			var count = await response.Body.ReadAsync(buffer, cancellationToken);
			if (count == 0) break;
			if (!hasLength)
			{
				var chunkHeader = Encoding.ASCII.GetBytes(count.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
				await stream.WriteAsync(chunkHeader, cancellationToken);
			}
			await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
			if (!hasLength) await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
		}
		if (!hasLength) await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	private static async Task WriteErrorAsync(Stream stream, HttpStatusCode statusCode, string message, CancellationToken cancellationToken)
	{
		var body = Encoding.UTF8.GetBytes(message);
		var reason = new HttpResponseMessage(statusCode).ReasonPhrase ?? "Error";
		var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)statusCode} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
		await stream.WriteAsync(header, cancellationToken);
		await stream.WriteAsync(body, cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_shutdown.Cancel();
		_listener?.Stop();
		if (_acceptLoop is not null)
		{
			try { await _acceptLoop; } catch (OperationCanceledException) { }
		}
		await Task.WhenAll(_connections.Values);
		foreach (var certificate in _serverCertificates.Values)
			if (certificate.IsValueCreated) certificate.Value.Dispose();
		_serverCertificates.Clear();
		_shutdown.Dispose();
	}

	private sealed record RequestHead(string Method, string Target, string Version, Dictionary<string, string[]> Headers);

	private sealed class Http11Reader(Stream stream)
	{
		private readonly byte[] _buffer = new byte[8192];
		private int _offset;
		private int _available;

		public async Task<RequestHead?> ReadHeadAsync(CancellationToken cancellationToken)
		{
			var line = await ReadLineAsync(cancellationToken);
			if (line is null) return null;
			var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new InvalidDataException("Invalid HTTP request line.");
			var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			var totalBytes = 0;
			while (true)
			{
				var headerLine = await ReadLineAsync(cancellationToken);
				if (headerLine is null) throw new EndOfStreamException();
				totalBytes += headerLine.Length;
				if (totalBytes > 64 * 1024) throw new InvalidDataException("HTTP headers are too large.");
				if (headerLine.Length == 0) break;
				var colon = headerLine.IndexOf(':');
				if (colon <= 0) throw new InvalidDataException("Invalid HTTP header.");
				var name = headerLine[..colon].Trim();
				var value = headerLine[(colon + 1)..].Trim();
				if (!headers.TryGetValue(name, out var values)) headers[name] = values = [];
				values.Add(value);
			}
			return new RequestHead(parts[0], parts[1], parts[2], headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
		}

		public async Task<byte[]> ReadBodyAsync(IReadOnlyDictionary<string, string[]> headers, int maxBytes, CancellationToken cancellationToken)
		{
			if (headers.TryGetValue("Transfer-Encoding", out var encodings) && encodings.Any(value => value.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
				return await ReadChunkedAsync(maxBytes, cancellationToken);
			if (!headers.TryGetValue("Content-Length", out var lengths)) return [];
			if (!int.TryParse(lengths.LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || length > maxBytes)
				throw new InvalidDataException("Request body is too large or has an invalid length.");
			return await ReadExactlyAsync(length, cancellationToken);
		}

		public Stream CreateRemainderStream()
		{
			var prefix = _buffer.AsSpan(_offset, _available - _offset).ToArray();
			_offset = _available;
			return new PrefixStream(prefix, stream);
		}

		private async Task<byte[]> ReadChunkedAsync(int maxBytes, CancellationToken cancellationToken)
		{
			using var body = new MemoryStream();
			while (true)
			{
				var sizeLine = await ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
				var delimiter = sizeLine.IndexOf(';');
				var number = delimiter >= 0 ? sizeLine[..delimiter] : sizeLine;
				if (!int.TryParse(number, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0 || body.Length + size > maxBytes)
					throw new InvalidDataException("Invalid or oversized chunked request body.");
				if (size == 0)
				{
					while ((await ReadLineAsync(cancellationToken)) is { Length: > 0 }) { }
					return body.ToArray();
				}
				var chunk = await ReadExactlyAsync(size, cancellationToken);
				await body.WriteAsync(chunk, cancellationToken);
				if (await ReadLineAsync(cancellationToken) != "") throw new InvalidDataException("Invalid chunk boundary.");
			}
		}

		private async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
		{
			var result = new byte[length];
			var written = 0;
			while (written < length)
			{
				if (_offset == _available) await RefillAsync(cancellationToken);
				var take = Math.Min(length - written, _available - _offset);
				_buffer.AsSpan(_offset, take).CopyTo(result.AsSpan(written));
				_offset += take;
				written += take;
			}
			return result;
		}

		private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
		{
			using var line = new MemoryStream();
			while (true)
			{
				var value = await ReadByteAsync(cancellationToken);
				if (value < 0)
				{
					if (line.Length == 0) return null;
					throw new EndOfStreamException();
				}
				if (value == '\n')
				{
					var bytes = line.ToArray();
					var length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
					return Encoding.ASCII.GetString(bytes, 0, length);
				}
				line.WriteByte((byte)value);
				if (line.Length > MaxHeaderLineBytes) throw new InvalidDataException("HTTP line is too long.");
			}
		}

		private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
		{
			if (_offset == _available) await RefillAsync(cancellationToken);
			return _offset < _available ? _buffer[_offset++] : -1;
		}

		private async Task RefillAsync(CancellationToken cancellationToken)
		{
			_offset = 0;
			_available = await stream.ReadAsync(_buffer, cancellationToken);
		}
	}

	private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
	{
		private int _offset;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => inner.CanWrite;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => inner.Flush();
		public override int Read(byte[] buffer, int offset, int count)
		{
			var copied = Math.Min(count, prefix.Length - _offset);
			if (copied > 0)
			{
				prefix.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset, copied));
				_offset += copied;
				return copied;
			}
			return inner.Read(buffer, offset, count);
		}
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var copied = Math.Min(buffer.Length, prefix.Length - _offset);
			if (copied > 0)
			{
				prefix.AsMemory(_offset, copied).CopyTo(buffer);
				_offset += copied;
				return copied;
			}
			return await inner.ReadAsync(buffer, cancellationToken);
		}
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
		protected override void Dispose(bool disposing) { }
	}
}
