#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HueStudio;

/// <summary>Windows 11 hızlı ayarlar tarzı açılır panel (simgeye sol tık).</summary>
public partial class QuickPanel : Window
{
	private readonly MainWindow host;
	private readonly SolidColorBrush chip = new SolidColorBrush(Colors.Gray);
	private readonly GradientStop glow0 = new GradientStop(Colors.Transparent, 0);
	private bool syncing;
	private DateTime hiddenAt = DateTime.MinValue;

	internal QuickPanel(MainWindow host)
	{
		InitializeComponent();
		this.host = host;
		Chip.Fill = chip;
		Glow.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { glow0, new GradientStop(Colors.Transparent, 1) } };
		Anim.AttachSwitch(Power);
		Deactivated += (_, _) => HidePanel();
		KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePanel(); };
		host.LightStateChanged += Sync;
		host.ScenesChanged += Rebuild;
		host.UpdateStateChanged += SyncUpdate;
		Rebuild();
		Sync();
	}

	/// <summary>Panel kapanır kapanmaz aynı tıkla yeniden açılmasın.</summary>
	public bool JustHidden => (DateTime.Now - hiddenAt).TotalMilliseconds < 350;

	public void ShowNear(Rect? iconRectDip)
	{
		Rebuild();
		Sync();
		Rect work = SystemParameters.WorkArea;
		Show();
		UpdateLayout();
		double w = ActualWidth > 0 ? ActualWidth : Width;
		double h = ActualHeight > 0 ? ActualHeight : 520;
		double cx = iconRectDip.HasValue ? iconRectDip.Value.Left + iconRectDip.Value.Width / 2 : work.Right - w / 2;
		Left = Math.Clamp(cx - w / 2, work.Left, work.Right - w);
		Top = Math.Max(work.Top, work.Bottom - h);
		Activate();
		if (Anim.Enabled)
		{
			TranslateTransform t = new TranslateTransform(0, 18);
			Surface.RenderTransform = t;
			Surface.Opacity = 0;
			Surface.BeginAnimation(OpacityProperty, Anim.To(1, 200));
			t.BeginAnimation(TranslateTransform.YProperty, Anim.To(0, 260, new CubicEase { EasingMode = EasingMode.EaseOut }));
		}
	}

	public void HidePanel()
	{
		if (!IsVisible) return;
		hiddenAt = DateTime.Now;
		Hide();
	}

	internal void Rebuild()
	{
		Favorites.Children.Clear();
		foreach (Color c in host.FavoriteColors.Take(8))
		{
			Button b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 6), ToolTip = Palette.NameOf(c) };
			b.Click += async (_, _) => await host.ApplyColorAsync(c);
			Favorites.Children.Add(b);
		}
		tabKey = null;
		RefreshTab();
	}

	// ---- Sahneler | Modlar sekmeleri (MiniBar ile ortak ScenesModes) ----

	private string? tabKey;

	private string Tab => host.QuickPanelTab;

	private void Tab_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string tab } && tab != Tab)
		{
			host.QuickPanelTab = tab;
		}
		RefreshTab();
	}

	/// <summary>Seçili sekmeyi gösterir; etkin sahne/mod değiştiyse çipleri yeniler.</summary>
	private void RefreshTab()
	{
		bool modes = Tab == ScenesModes.ModesTab;
		TabScenes.IsChecked = !modes;
		TabModes.IsChecked = modes;
		// Gizli sekme yer kaplamaya devam eder: sekme değişince panel boyu zıplamaz.
		Scenes.Visibility = modes ? Visibility.Hidden : Visibility.Visible;
		Modes.Visibility = modes ? Visibility.Visible : Visibility.Hidden;
		string key = $"{host.ActiveMode}|{host.ActiveSceneId}|{host.SceneList.Count}";
		if (key == tabKey) return;
		tabKey = key;
		ScenesModes.FillScenes(Scenes, host, 8);
		ScenesModes.FillModes(Modes, host, compact: true);
	}

	/// <summary>Dil değişti: tüm kodla yazılan metinleri yeniden kur.</summary>
	internal void Relocalize()
	{
		tabKey = null;
		Rebuild();
		Sync();
		SyncUpdate();
	}

	internal void Sync()
	{
		if (!IsVisible && IsLoaded) return;
		syncing = true;
		try
		{
			LampName.Text = host.LampLabel;
			Status.Text = host.StatusLine();
			Power.IsEnabled = host.IsReady;
			Power.IsChecked = host.PowerState == true;
			Bri.IsEnabled = host.IsReady;
			if (!Bri.IsMouseCaptureWithin)
			{
				Bri.Value = Math.Round(host.CurrentBrightness);
			}
			BriText.Text = L.Pct(Bri.Value);
			Color c = host.HasColor ? host.LiveColor : Colors.Gray;
			bool on = host.PowerState == true;
			Anim.Color(chip, on ? c : Color.FromRgb(0x55, 0x5C, 0x6E), 220);
			Anim.Color(glow0, Color.FromArgb(on ? (byte)190 : (byte)0, c.R, c.G, c.B), 220);
			string? live = host.LiveLine();
			LiveBox.Visibility = live == null ? Visibility.Collapsed : Visibility.Visible;
			LiveText.Text = live ?? "";
			SleepButton.Content = host.SleepActive ? L.T("sleep_cancel") : L.T("sleep_30");
			RefreshTab();
			SyncUpdate();
		}
		finally
		{
			syncing = false;
		}
	}

	private async void Power_Click(object sender, RoutedEventArgs e) => await host.TogglePowerAsync();

	private void Bri_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (BriText != null) BriText.Text = L.Pct(e.NewValue);
		if (!syncing && IsLoaded) host.LiveBrightness(e.NewValue, final: false);
	}

	private void Bri_Release(object sender, MouseButtonEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private void Bri_KeyUp(object sender, KeyEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private async void LiveStop_Click(object sender, RoutedEventArgs e) => await host.StopAutomationAsync();

	private void Open_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
		host.RestoreWindow();
	}

	private void Mini_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
		host.ShowMini();
	}

	internal void SyncUpdate()
	{
		UpdateInfo? u = host.AvailableUpdate;
		UpdateHint.Visibility = u == null ? Visibility.Collapsed : Visibility.Visible;
		if (u != null) UpdateHintText.Text = L.F("update_ready_ver", u.VersionText);
	}

	private void Update_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
		host.RestoreWindow();
		host.ShowUpdateDialog();
	}

	private void Sleep_Click(object sender, RoutedEventArgs e)
	{
		if (host.SleepActive) host.StartSleepTimer(0);
		else host.StartSleepTimer(30);
		Sync();
	}
}
