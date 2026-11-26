using System.Windows;
using KillSwitch.Core;

namespace KillSwitch.Companion;

public partial class AddRuleWindow : Window
{
	private readonly string _currentIp;

	public AccessRule? NewRule { get; private set; }

	public AddRuleWindow(string currentIp)
	{
		InitializeComponent();
		_currentIp = currentIp;
		CurrentIpText.Text = currentIp;
		IpConditionsInput.Text = currentIp;
		TargetInput.Focus();
	}

	private void Add_Click(object sender, RoutedEventArgs e)
	{
		var target = TargetInput.Text.Trim();
		var addresses = IpConditionsInput.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (addresses.Length == 0) addresses = [_currentIp];
		if (addresses.Any(address => address.StartsWith('!')))
		{
			ErrorText.Text = "Enter plain IP addresses here. Choose Allow or Block above to set what happens.";
			return;
		}
		var blockListedAddresses = BlockIpsRadio.IsChecked == true;
		var conditions = string.Join(",", addresses.Select(address => blockListedAddresses ? "!" + address : address));
		var rule = new AccessRule { Target = target, IpConditions = conditions };
		var error = AccessRule.Validate(rule);
		if (error is not null)
		{
			ErrorText.Text = error;
			return;
		}
		NewRule = rule;
		DialogResult = true;
	}
}
