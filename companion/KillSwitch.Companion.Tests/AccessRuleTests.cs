using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class AccessRuleTests
{
	[TestMethod]
	public void AllowListBlocksUnknownAndMismatchedAddresses()
	{
		var rule = new AccessRule { Target = "example.com", IpConditions = "203.0.113.10, 198.51.100.20" };
		Assert.IsFalse(rule.Blocks("203.0.113.10"));
		Assert.IsFalse(rule.Blocks("198.51.100.20"));
		Assert.IsTrue(rule.Blocks("192.0.2.44"));
		Assert.IsTrue(rule.Blocks(null));
	}

	[TestMethod]
	public void DenyListBlocksOnlyListedAddressAndOverridesAllowList()
	{
		var rule = new AccessRule { Target = "example.com", IpConditions = "203.0.113.10, !203.0.113.10" };
		Assert.IsTrue(rule.Blocks("203.0.113.10"));
		Assert.IsTrue(rule.Blocks("198.51.100.20"));

		var denyOnly = new AccessRule { Target = "example.com", IpConditions = "!203.0.113.10" };
		Assert.IsTrue(denyOnly.Blocks("203.0.113.10"));
		Assert.IsFalse(denyOnly.Blocks("198.51.100.20"));
	}

	[TestMethod]
	public void ValidationRequiresTargetAndIpv4Entries()
	{
		Assert.IsNotNull(AccessRule.Validate(new AccessRule { Target = "", IpConditions = "203.0.113.1" }));
		Assert.IsNotNull(AccessRule.Validate(new AccessRule { Target = "example.com", IpConditions = "not-an-ip" }));
		Assert.IsNotNull(AccessRule.Validate(new AccessRule { Target = "example.com", IpConditions = "2001:db8::1" }));
		Assert.IsNull(AccessRule.Validate(new AccessRule { Target = "example.com", IpConditions = "203.0.113.1, !198.51.100.2" }));
	}

	[TestMethod]
	public void TargetMatchHandlesHostsAndPagePaths()
	{
		var hostRule = new AccessRule { Target = "example.com", IpConditions = "203.0.113.1" };
		Assert.IsTrue(hostRule.MatchesUri(new Uri("https://example.com/path")));
		Assert.IsFalse(hostRule.MatchesUri(new Uri("https://sub.example.com/path")));

		var wildcardHost = new AccessRule { Target = "*.example.com", IpConditions = "203.0.113.1" };
		Assert.IsTrue(wildcardHost.MatchesUri(new Uri("https://example.com/path")));
		Assert.IsTrue(wildcardHost.MatchesUri(new Uri("https://sub.example.com/path")));
		Assert.IsFalse(wildcardHost.MatchesUri(new Uri("https://notexample.com/path")));

		var pageRule = new AccessRule { Target = "https://example.com/private", IpConditions = "203.0.113.1" };
		Assert.IsTrue(pageRule.MatchesUri(new Uri("https://example.com/private/child")));
		Assert.IsTrue(pageRule.MatchesUri(new Uri("https://example.com/private?view=full")));
		Assert.IsFalse(pageRule.MatchesUri(new Uri("https://example.com/public")));
		Assert.IsFalse(pageRule.MatchesUri(new Uri("https://example.com/privately")));

		var wildcard = new AccessRule { Target = "example.com/*/admin", IpConditions = "203.0.113.1" };
		Assert.IsTrue(wildcard.MatchesUri(new Uri("https://example.com/a/b/admin")));
		Assert.IsFalse(wildcard.MatchesUri(new Uri("https://example.com/a/b/user")));
	}

	[TestMethod]
	public void SearchMatchesTargetIpConditionsAndCurrentStatusCaseInsensitively()
	{
		var rule = new AccessRule { Target = "private.example.com", IpConditions = "203.0.113.10, !198.51.100.2", CurrentStatus = "Limited" };
		Assert.IsTrue(rule.MatchesSearch("EXAMPLE"));
		Assert.IsTrue(rule.MatchesSearch("203.0.113"));
		Assert.IsTrue(rule.MatchesSearch("limited"));
		Assert.IsTrue(rule.MatchesSearch("  "));
		Assert.IsFalse(rule.MatchesSearch("allowed"));
	}
}
