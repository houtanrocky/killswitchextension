using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Data;
using System.Security.Cryptography.X509Certificates;
using KillSwitch.Core;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace KillSwitch.Companion;

public partial class MainWindow : Window
{
	private readonly RuleRepository _repository = new();
	private readonly GatewaySettingsStore _gatewaySettingsStore = new();
	private readonly PublicIpClient _ipClient = new();
	private readonly WindowsUserProxyController _proxyController = new();
	private readonly ObservableCollection<AccessRule> _rules = [];
	private ICollectionView? _rulesView;
	private readonly DispatcherTimer _ipRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
	private string? _currentIp;
	private GatewaySettings? _gatewaySettings;
	private TlsCertificateAuthority? _certificateAuthority;
	private GatewayClient? _gatewayClient;
	private LocalRequestProxy? _requestProxy;
	private bool _allowWindowClose;
	private bool _exitRequestedFromTray;
	private bool _startingProxy;
	private bool _stoppingProxy;
	private bool _loadingStartupSetting;

	public MainWindow()
	{
		InitializeComponent();
		RulesGrid.ItemsSource = _rules;
		_rulesView = CollectionViewSource.GetDefaultView(_rules);
		_rulesView.Filter = FilterRule;
		try
		{
			_gatewaySettings = _gatewaySettingsStore.Load();
			if (_gatewaySettings is not null)
			{
				GatewayStatusText.Text = "A connection is saved for this Windows user.";
			}
		}
		catch (Exception error)
		{
			GatewayStatusText.Text = "The saved connection could not be opened. It will be set up again when needed.";
			System.Diagnostics.Debug.WriteLine(error);
		}
		_ipRefreshTimer.Tick += async (_, _) => await RefreshCurrentIpAsync();
		Loaded += async (_, _) => await InitializeAsync();
		Closing += (_, args) =>
		{
			if (_allowWindowClose) return;
			if (_startingProxy || _stoppingProxy)
			{
				args.Cancel = true;
				GatewayStatusText.Text = "Wait for the current routing operation to finish, then close the window again.";
				return;
			}
			if (!_exitRequestedFromTray)
			{
				args.Cancel = true;
				Hide();
				return;
			}
			if (_requestProxy is null) return;
			args.Cancel = true;
			_ = StopBeforeCloseAsync();
		};
		Closed += (_, _) => _ipRefreshTimer.Stop();
	}

	private async Task InitializeAsync()
	{
		StartProxyButton.IsEnabled = false;
		StopProxyButton.IsEnabled = false;
		ProtectionStatus.Text = "Checking Windows firewall status…";
		var hadFirewallRecovery = WindowsFirewallGate.HasRecoveryState;
		try
		{
			await WindowsFirewallGate.RestoreAsync();
			if (hadFirewallRecovery)
				GatewayStatusText.Text = "Recovered and restored a previous Windows firewall session.";
			StartProxyButton.IsEnabled = true;
			ProtectionStatus.Text = "Not active — Windows egress gate is stopped";
			ProtectionStatus.Foreground = System.Windows.Media.Brushes.Orange;
		}
		catch (Exception error)
		{
			ProtectionStatus.Text = "Firewall recovery required — outbound traffic may remain restricted";
			GatewayStatusText.Text = $"Could not restore the previous firewall settings. Retry as administrator: {error.Message}";
			StopProxyButton.IsEnabled = true;
		}

		_loadingStartupSetting = true;
		LaunchAtStartupCheck.IsEnabled = false;
		try
		{
			LaunchAtStartupCheck.IsChecked = await StartupRegistration.IsEnabledAsync();
		}
		catch (Exception error)
		{
			GatewayStatusText.Text = $"Could not read the sign-in setting: {error.Message}";
		}
		finally
		{
			_loadingStartupSetting = false;
			LaunchAtStartupCheck.IsEnabled = true;
		}

		await LoadRulesAsync();
		await RefreshCurrentIpAsync();
		_ipRefreshTimer.Start();
	}

	private async void LaunchAtStartup_Changed(object sender, RoutedEventArgs e)
	{
		if (_loadingStartupSetting || LaunchAtStartupCheck.IsChecked is not bool enabled) return;
		LaunchAtStartupCheck.IsEnabled = false;
		try
		{
			await StartupRegistration.SetEnabledAsync(enabled);
			GatewayStatusText.Text = enabled
				? "Kill Switch will open when this Windows user signs in."
				: "Automatic sign-in launch is disabled.";
		}
		catch (Exception error)
		{
			_loadingStartupSetting = true;
			LaunchAtStartupCheck.IsChecked = !enabled;
			_loadingStartupSetting = false;
			GatewayStatusText.Text = $"Could not update the sign-in setting: {error.Message}";
		}
		finally
		{
			LaunchAtStartupCheck.IsEnabled = true;
		}
	}

	public void ExitFromTray()
	{
		_exitRequestedFromTray = true;
		Show();
		Activate();
		Close();
	}

	private async Task<bool> LoadRulesAsync()
	{
		try
		{
			_rules.Clear();
			foreach (var rule in await _repository.LoadAsync()) _rules.Add(rule);
			UpdateCount();
			UpdateRuleStatuses();
			MessageText.Text = "Rules loaded from this PC.";
			return true;
		}
		catch (Exception error)
		{
			MessageText.Text = "Could not load the local rules file.";
			MessageBox.Show(this, error.Message, "Kill Switch", MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}
	}

	private void AddRule_Click(object sender, RoutedEventArgs e)
	{
		var rule = new AccessRule();
		_rules.Add(rule);
		RulesGrid.SelectedItem = rule;
		RulesGrid.ScrollIntoView(rule);
		RulesGrid.BeginEdit();
		UpdateCount();
	}

	private void RemoveRule_Click(object sender, RoutedEventArgs e)
	{
		if (RulesGrid.SelectedItem is not AccessRule selected)
		{
			MessageText.Text = "Select a rule to remove.";
			return;
		}
		_rules.Remove(selected);
		UpdateCount();
		MessageText.Text = "Rule removed. Save to keep the change.";
	}

	private async void DiscardChanges_Click(object sender, RoutedEventArgs e)
	{
		if (await LoadRulesAsync()) MessageText.Text = "Unsaved changes discarded.";
	}

	private async void Save_Click(object sender, RoutedEventArgs e)
	{
		RulesGrid.CommitEdit();
		RulesGrid.CommitEdit();
		for (var index = 0; index < _rules.Count; index++)
		{
			var error = AccessRule.Validate(_rules[index]);
			if (error is not null)
			{
				RulesGrid.SelectedIndex = index;
				MessageText.Text = error;
				return;
			}
			_rules[index].Target = _rules[index].Target.Trim();
			if (_rules.Take(index).Any(rule => rule.Target.Equals(_rules[index].Target, StringComparison.OrdinalIgnoreCase)))
			{
				RulesGrid.SelectedIndex = index;
				MessageText.Text = $"Duplicate target: {_rules[index].Target}";
				return;
			}
		}

		try
		{
			await _repository.SaveAsync(_rules);
			MessageText.Text = "Rules saved locally.";
			await SyncPolicyIfConfiguredAsync();
		}
		catch (Exception error)
		{
			MessageText.Text = "Could not save the local rules file.";
			MessageBox.Show(this, error.Message, "Kill Switch", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async void Import_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog
		{
			Title = "Import Kill Switch rules",
			Filter = "Kill Switch rules (*.json)|*.json|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true) return;

		try
		{
			var imported = await RuleRepository.ImportAsync(dialog.FileName);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var rule in imported)
			{
				var error = AccessRule.Validate(rule);
				if (error is not null) throw new InvalidDataException($"{rule.Target}: {error}");
				if (!seen.Add(rule.Target.Trim())) throw new InvalidDataException($"Duplicate target: {rule.Target}");
			}
			_rules.Clear();
			foreach (var rule in imported) _rules.Add(rule);
			await _repository.SaveAsync(_rules);
			UpdateCount();
			UpdateRuleStatuses();
			MessageText.Text = $"Imported and saved {imported.Count} rule(s).";
			await SyncPolicyIfConfiguredAsync();
		}
		catch (Exception error)
		{
			MessageBox.Show(this, error.Message, "Could not import rules", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async void Export_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new SaveFileDialog
		{
			Title = "Export Kill Switch rules",
			Filter = "Kill Switch rules (*.json)|*.json",
			FileName = "kill-switch-rules.json",
			DefaultExt = ".json",
			AddExtension = true
		};
		if (dialog.ShowDialog(this) != true) return;
		try
		{
			var savedRules = await _repository.LoadAsync();
			await RuleRepository.ExportExtensionFormatAsync(dialog.FileName, savedRules);
			MessageText.Text = "Saved rules exported in extension-compatible format.";
		}
		catch (Exception error)
		{
			MessageBox.Show(this, error.Message, "Could not export rules", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async Task SyncPolicyIfConfiguredAsync()
	{
		if (_gatewaySettings is null) return;
		try
		{
			if (_gatewayClient is not null)
			{
				await _gatewayClient.SyncPolicyAsync(_rules);
				_requestProxy?.SetPolicyReady(true);
				GatewayStatusText.Text = "Rules synced to the active gateway; the next request uses the updated policy.";
				return;
			}
			using var gateway = new GatewayClient(_gatewaySettings.Endpoint, _gatewaySettings.BearerToken);
			await gateway.SyncPolicyAsync(_rules);
			GatewayStatusText.Text = "Rules synced. Enable Windows routing to apply the request gate to compatible apps.";
		}
		catch (Exception error)
		{
			_requestProxy?.SetPolicyReady(false);
			GatewayStatusText.Text = _requestProxy is null
				? $"Gateway sync failed; local rules remain saved. {error.Message}"
				: $"Gateway sync failed; local proxy now blocks requests until policy sync succeeds. {error.Message}";
		}
	}

	private async void RefreshIp_Click(object sender, RoutedEventArgs e) => await RefreshCurrentIpAsync();

	private async void StartProxy_Click(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(this,
			"To check each request against your rules, Kill Switch sends the destination URL, request headers and data, and the public IP address seen by its connection service. The service forwards allowed requests. Its operator and hosting provider may process this information. Windows Firewall will also block other outbound traffic while protection is on; apps must honor the Windows proxy. Administrator approval is required. Continue?",
			"Enable Windows egress gate", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
		_startingProxy = true;
		StartProxyButton.IsEnabled = false;
		try
		{
			if (_gatewaySettings is null)
			{
				GatewayStatusText.Text = "Connecting to the managed service…";
				var endpoint = await ManagedGatewayDiscovery.GetEndpointAsync();
				var token = await GatewayClient.RegisterDeviceAsync(endpoint);
				_gatewaySettings = new GatewaySettings(endpoint, token);
				_gatewaySettingsStore.Save(_gatewaySettings);
			}
			RulesGrid.CommitEdit();
			RulesGrid.CommitEdit();
			for (var index = 0; index < _rules.Count; index++)
			{
				var validationError = AccessRule.Validate(_rules[index]);
				if (validationError is not null) throw new InvalidDataException(validationError);
				if (_rules.Take(index).Any(rule => rule.Target.Trim().Equals(_rules[index].Target.Trim(), StringComparison.OrdinalIgnoreCase)))
					throw new InvalidDataException($"Duplicate target: {_rules[index].Target}");
			}
			await _repository.SaveAsync(_rules);
			var gatewayAddresses = await Dns.GetHostAddressesAsync(_gatewaySettings.Endpoint.DnsSafeHost);
			_gatewayClient = new GatewayClient(_gatewaySettings.Endpoint, _gatewaySettings.BearerToken, resolvedGatewayAddresses: gatewayAddresses);
			await _gatewayClient.SyncPolicyAsync(_rules);
			_certificateAuthority = TlsCertificateAuthority.Create();
			_proxyController.RecordInspectionCertificate(_certificateAuthority.Certificate);
			using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
			{
				store.Open(OpenFlags.ReadWrite);
				using var trustedRoot = X509CertificateLoader.LoadCertificate(_certificateAuthority.Certificate.RawData);
				store.Add(trustedRoot);
			}
			_requestProxy = new LocalRequestProxy(_gatewayClient, _certificateAuthority);
			await _requestProxy.StartAsync();
			await WindowsFirewallGate.EnableAsync(Environment.ProcessPath ?? "", _gatewaySettings.Endpoint.Port, gatewayAddresses);
			await _gatewayClient.SyncPolicyAsync(_rules);
			_currentIp = await _gatewayClient.GetObservedPublicIpAsync();
			CurrentIpText.Text = _currentIp;
			UpdateRuleStatuses();
			_proxyController.Enable(_requestProxy.Port);
			StartProxyButton.IsEnabled = false;
			StopProxyButton.IsEnabled = true;
			ProtectionStatus.Text = "Windows firewall gate active — gateway is the only remote route";
			ProtectionStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
			GatewayStatusText.Text = "Local inspection CA trusted for this Windows user. Apps with certificate pinning may reject inspected connections.";
		}
		catch (Exception error)
		{
			if (await StopProxyAsync())
				GatewayStatusText.Text = $"Could not start local routing: {error.Message}";
			else
				GatewayStatusText.Text += $" Startup error: {error.Message}";
		}
		finally
		{
			_startingProxy = false;
			StartProxyButton.IsEnabled = _requestProxy is null;
		}
	}

	private async void StopProxy_Click(object sender, RoutedEventArgs e) => await StopProxyAsync();

	private async Task StopBeforeCloseAsync()
	{
		if (!await StopProxyAsync()) return;
		_allowWindowClose = true;
		Close();
	}

	private async Task<bool> StopProxyAsync()
	{
		if (_stoppingProxy) return false;
		_stoppingProxy = true;
		try
		{
			try { _proxyController.Restore(); }
			catch (Exception error)
			{
				GatewayStatusText.Text = $"Could not restore Windows proxy settings; routing remains active. {error.Message}";
				StopProxyButton.IsEnabled = true;
				return false;
			}
			try
			{
				await WindowsFirewallGate.RestoreAsync();
			}
			catch (Exception error)
			{
				if (_requestProxy is not null)
				{
					try { _proxyController.Enable(_requestProxy.Port); } catch { }
				}
				GatewayStatusText.Text = $"Firewall remains active; proxy routing was restored. Retry stopping as administrator: {error.Message}";
				StopProxyButton.IsEnabled = true;
				ProtectionStatus.Text = "Windows firewall gate remains active — outbound traffic is restricted";
				ProtectionStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
				return false;
			}
			if (_requestProxy is not null)
			{
				await _requestProxy.DisposeAsync();
				_requestProxy = null;
			}
			_gatewayClient?.Dispose();
			_gatewayClient = null;
			if (_certificateAuthority is not null)
			{
				try { _proxyController.RemoveInspectionCertificate(_certificateAuthority.Certificate); }
				catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Could not remove local inspection CA: {error}"); }
				_certificateAuthority.Dispose();
				_certificateAuthority = null;
			}
			if (IsLoaded)
			{
				StartProxyButton.IsEnabled = true;
				StopProxyButton.IsEnabled = false;
				ProtectionStatus.Text = "Not active — Windows egress gate is stopped";
				ProtectionStatus.Foreground = System.Windows.Media.Brushes.Orange;
			}
			return true;
		}
		finally { _stoppingProxy = false; }
	}

	private async Task RefreshCurrentIpAsync()
	{
		RefreshIpButton.IsEnabled = false;
		CurrentIpText.Text = "Checking…";
		try
		{
			if (_gatewayClient is not null)
				_currentIp = await _gatewayClient.GetObservedPublicIpAsync();
			else if (_gatewaySettings is not null)
			{
				using var gateway = new GatewayClient(_gatewaySettings.Endpoint, _gatewaySettings.BearerToken);
				_currentIp = await gateway.GetObservedPublicIpAsync();
			}
			else
				_currentIp = await _ipClient.GetCurrentIpAsync();
			CurrentIpText.Text = _currentIp;
			UpdateRuleStatuses();
			var limited = _rules.Where(rule => rule.Blocks(_currentIp)).Select(rule => rule.Target).ToArray();
			MessageText.Text = limited.Length == 0
				? "Current IP matches all configured website rules."
				: $"Current IP is limited for {limited.Length} configured target(s).";
		}
		catch (Exception error)
		{
			_currentIp = null;
			CurrentIpText.Text = "Unavailable";
			UpdateRuleStatuses();
			MessageText.Text = _requestProxy is null
				? "IP lookup failed. Network protection is not active."
				: "Gateway IP check failed. The egress gate remains active; proxied requests fail closed.";
			System.Diagnostics.Debug.WriteLine(error);
		}
		finally
		{
			RefreshIpButton.IsEnabled = true;
		}
	}

	private bool FilterRule(object item)
	{
		if (item is not AccessRule rule) return false;
		return rule.MatchesSearch(RuleSearchInput.Text);
	}

	private void RuleSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
	{
		_rulesView?.Refresh();
		UpdateCount();
	}

	private void UpdateCount()
	{
		var filteredCount = _rulesView?.Cast<object>().Count() ?? _rules.Count;
		RulesCountText.Text = RuleSearchInput.Text.Trim().Length == 0
			? $"{_rules.Count} {(_rules.Count == 1 ? "rule" : "rules")}" 
			: $"{filteredCount} of {_rules.Count} rules";
	}

	private void UpdateRuleStatuses()
	{
		foreach (var rule in _rules)
			rule.CurrentStatus = _currentIp is null ? "Unknown" : rule.Blocks(_currentIp) ? "Limited" : "Allowed";
		_rulesView?.Refresh();
		UpdateCount();
	}
}
