using System.Configuration;
using System.Data;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace KillSwitch.Companion;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private System.Threading.Mutex? _instanceMutex;
	private Forms.NotifyIcon? _trayIcon;

	protected override void OnStartup(StartupEventArgs e)
	{
		var mutex = new System.Threading.Mutex(initiallyOwned: false, "Local\\KillSwitch.Companion.Instance");
		var ownsMutex = false;
		try { ownsMutex = mutex.WaitOne(0); }
		catch (AbandonedMutexException) { ownsMutex = true; }
		if (!ownsMutex)
		{
			mutex.Dispose();
			MessageBox.Show("Kill Switch is already running for this Windows session.", "Kill Switch", MessageBoxButton.OK, MessageBoxImage.Information);
			Shutdown();
			return;
		}
		_instanceMutex = mutex;
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		var window = new MainWindow();
		MainWindow = window;
		CreateTrayIcon(window);
		window.Closed += (_, _) => Shutdown();
		window.Show();
	}

	private void CreateTrayIcon(Window window)
	{
		var executable = Environment.ProcessPath;
		var icon = executable is not null
			? System.Drawing.Icon.ExtractAssociatedIcon(executable)
			: null;
		_trayIcon = new Forms.NotifyIcon
		{
			Icon = icon ?? System.Drawing.SystemIcons.Application,
			Text = "Kill Switch network protection",
			Visible = true
		};
		var menu = new Forms.ContextMenuStrip();
		menu.Items.Add("Open Kill Switch", null, (_, _) => ShowMainWindow(window));
		menu.Items.Add(new Forms.ToolStripSeparator());
		menu.Items.Add("Exit", null, (_, _) => ((MainWindow)window).ExitFromTray());
		_trayIcon.ContextMenuStrip = menu;
		_trayIcon.DoubleClick += (_, _) => ShowMainWindow(window);
	}

	private static void ShowMainWindow(Window window)
	{
		if (window.WindowState == WindowState.Minimized)
			window.WindowState = WindowState.Normal;
		window.Show();
		window.Activate();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (_trayIcon is not null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Dispose();
		}
		_instanceMutex?.ReleaseMutex();
		_instanceMutex?.Dispose();
		base.OnExit(e);
	}
}

