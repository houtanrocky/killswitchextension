using System.Net;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class PublicIpClientTests
{
	[TestMethod]
	public async Task ReturnsFreshProviderValueWithoutUsingCachedState()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("203.0.113.10")
		});
		var client = new PublicIpClient(new HttpClient(handler));
		Assert.AreEqual("203.0.113.10", await client.GetCurrentIpAsync());
		Assert.AreEqual("https://api.ipify.org/", handler.LastRequestUri?.ToString());
		Assert.IsTrue(handler.NoCache, "IP lookups must bypass intermediary caches.");
	}

	[TestMethod]
	public async Task RejectsInvalidProviderValue()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("not-an-ip")
		});
		var client = new PublicIpClient(new HttpClient(handler));
		await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.GetCurrentIpAsync());
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public Uri? LastRequestUri { get; private set; }
		public bool NoCache { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequestUri = request.RequestUri;
			NoCache = request.Headers.CacheControl?.NoCache == true && request.Headers.CacheControl.NoStore == true;
			return Task.FromResult(respond(request));
		}
	}
}
