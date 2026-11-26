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
		var conditions = IpConditionsInput.Text.Trim();
		if (string.IsNullOrWhiteSpace(conditions)) conditions = _currentIp;
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
