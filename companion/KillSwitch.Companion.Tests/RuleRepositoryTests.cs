using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class RuleRepositoryTests
{
	[TestMethod]
	public async Task SavesAndLoadsRulesFromItsConfiguredLocalFile()
	{
		var folder = Path.Combine(Path.GetTempPath(), "killswitch-tests", Guid.NewGuid().ToString("N"));
		var path = Path.Combine(folder, "rules.json");
		try
		{
			var repository = new RuleRepository(path);
			var expected = new[]
			{
				new AccessRule { Target = "example.com", IpConditions = "203.0.113.10, !198.51.100.20" }
			};
			await repository.SaveAsync(expected);
			var actual = await repository.LoadAsync();
			Assert.HasCount(1, actual);
			Assert.AreEqual(expected[0].Target, actual[0].Target);
			Assert.AreEqual(expected[0].IpConditions, actual[0].IpConditions);
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
		}
	}

	[TestMethod]
	public async Task ImportsAndExportsTheBrowserExtensionRuleShape()
	{
		var folder = Path.Combine(Path.GetTempPath(), "killswitch-tests", Guid.NewGuid().ToString("N"));
		var path = Path.Combine(folder, "rules.json");
		Directory.CreateDirectory(folder);
		try
		{
			await File.WriteAllTextAsync(path, """
				{"urls":["example.com","https://example.net/private"],"allowed_ips":["203.0.113.10|","!198.51.100.20|"]}
				""");
			var imported = await RuleRepository.ImportAsync(path);
			Assert.HasCount(2, imported);
			Assert.AreEqual("!198.51.100.20", imported[1].IpConditions);

			await RuleRepository.ExportExtensionFormatAsync(path, imported);
			var roundTrip = await RuleRepository.ImportAsync(path);
			Assert.AreEqual(imported[0].Target, roundTrip[0].Target);
			Assert.AreEqual(imported[1].IpConditions, roundTrip[1].IpConditions);
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
		}
	}
}
