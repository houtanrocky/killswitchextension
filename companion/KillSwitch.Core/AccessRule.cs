using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KillSwitch.Core;

public sealed class AccessRule : INotifyPropertyChanged
{
	private string _target = "";
	private string _ipConditions = "";
	private string _currentStatus = "Not checked";

	public string Target
	{
		get => _target;
		set { if (_target == value) return; _target = value; PropertyChanged?.Invoke(this, new(nameof(Target))); }
	}

	public string IpConditions
	{
		get => _ipConditions;
		set { if (_ipConditions == value) return; _ipConditions = value; PropertyChanged?.Invoke(this, new(nameof(IpConditions))); }
	}

	[JsonIgnore]
	public string CurrentStatus
	{
		get => _currentStatus;
		set { if (_currentStatus == value) return; _currentStatus = value; PropertyChanged?.Invoke(this, new(nameof(CurrentStatus))); }
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public static string? Validate(AccessRule rule)
	{
		if (string.IsNullOrWhiteSpace(rule.Target)) return "Enter a website or page.";
		if (string.IsNullOrWhiteSpace(rule.IpConditions)) return "Enter at least one IPv4 address.";

		foreach (var raw in rule.IpConditions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			var ip = raw.StartsWith('!') ? raw[1..] : raw;
			if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
				return $"'{raw}' is not a valid IPv4 address.";
		}

		return null;
	}

	public bool Blocks(string? currentIp)
	{
		if (!IPAddress.TryParse(currentIp, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return true;
		var values = IpConditions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (values.Length == 0) return true;
		var blocked = values.Any(value => value.StartsWith('!') && value[1..] == currentIp);
		if (blocked) return true;
		var allowList = values.Where(value => !value.StartsWith('!')).ToArray();
		return allowList.Length > 0 && !allowList.Contains(currentIp, StringComparer.Ordinal);
	}

	public bool MatchesUri(Uri uri)
	{
		if (uri.Scheme is not ("http" or "https")) return false;
		var target = Target.Trim().ToLowerInvariant();
		if (target.Length == 0) return false;
		var candidate = (uri.Authority + uri.PathAndQuery + uri.Fragment).ToLowerInvariant();
		var pattern = Regex.Replace(target, "^https?://", "", RegexOptions.IgnoreCase);
		var wildcardHost = Regex.Match(pattern, @"^\*\.([a-z0-9.-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		if (wildcardHost.Success)
		{
			var suffix = wildcardHost.Groups[1].Value;
			return uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
				|| uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
		}
		if (!pattern.Contains('/') && !pattern.Contains('?') && !pattern.Contains('#') && !pattern.Contains('*'))
			return uri.Host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
		if (pattern.Contains('*'))
			return Regex.IsMatch(candidate, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		return candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase)
			|| candidate.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase)
			|| candidate.StartsWith(pattern + "?", StringComparison.OrdinalIgnoreCase)
			|| candidate.StartsWith(pattern + "#", StringComparison.OrdinalIgnoreCase);
	}

	public bool MatchesSearch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query)) return true;
		return Target.Contains(query, StringComparison.OrdinalIgnoreCase)
			|| IpConditions.Contains(query, StringComparison.OrdinalIgnoreCase)
			|| CurrentStatus.Contains(query, StringComparison.OrdinalIgnoreCase);
	}
}
