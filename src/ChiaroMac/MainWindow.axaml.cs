#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HueStudio.Ui;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

/// <summary>
/// Ana pencere — Windows MainWindow'un Avalonia karşılığı. Işık mantığı (Run/Send/canlı yazım/mod döngüsü)
/// Windows sürümündeki akışla aynıdır; Bluetooth katmanı IBulbTransport arkasındadır.
/// </summary>
public partial class MainWindow : Window
{
	private readonly HueGattTransport ble;
	private readonly StudioSettings settings;
	private readonly ObservableCollection<Lamp> lamps = new ObservableCollection<Lamp>();
	private readonly SemaphoreSlim lightGate = new SemaphoreSlim(1, 1);
	private readonly bool demoMode;
	private readonly bool initialized;

	private CancellationTokenSource? operation;
	private bool busy;
	private bool closed;
	private bool lastRunFailed;
	private bool autoCancelled;
	private bool exitRequested;
	private int scanGeneration;
	private string connectedName = "";
	private string connectedKey = "";

	private bool? powerOn;
	private MColor currentColor = MColor.FromRgb(0xFF, 0xD2, 0x71);
	private bool colorKnown;
	private double? liveBrightness;
	private bool lastWasWhite;
	private string? activeSceneId;
	private bool darkTheme = true;
	private int silentSliders;

	private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "HueBluetooth");
	private static string LampPath => Path.Combine(DataFolder, "last-lamp-mac.json");
	private static string ThemePath => Path.Combine(DataFolder, "appearance.json");

	/// <summary>Oturum açılışında (--background) pencere gösterilmeden menü çubuğunda başla.</summary>
	internal static bool StartHidden;
	private bool started;

	/// <summary>Pencereyi açmadan başlat: menü çubuğu simgesi + son lambaya bağlanma.</summary>
	internal async void StartInBackground()
	{
		if (started) return;
		started = true;
		Log("Arka planda başlatıldı (oturum açılışı).");
		InitTray();
		if (tray == null)
		{
			// Menü çubuğu simgesi kurulamadıysa görünmez bir uygulama kalmasın: pencereyi göster.
			RestoreWindow();
		}
		else
		{
			// Yalnızca menü çubuğu simgesi: Dock simgesi gösterilmez (Program, --background ile ShowInDock=false başlatır).
			SetDockIcon(false);
		}
		_ = AutoCheckUpdates();
		await StartupConnect();
	}

	public MainWindow()
	{
		InitializeComponent();
		settings = StudioSettings.Load();
		ClampSettings();
		string? langOverride = Environment.GetEnvironmentVariable("CHIARO_LANG");
		L.Apply(langOverride is "tr" or "en" ? langOverride : settings.Language);
		demoMode = !OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("CHIARO_DEMO") == "1";
		IGattBackend backend = demoMode ? new DemoBackend() : new CoreBluetoothBackend(m => Log(m));
		ble = new HueGattTransport(backend, m => Log(m));
		ble.ConnectionChanged += connected => Dispatcher.UIThread.Post(() => OnConnectionChanged(connected));
		Log($"Arka uç: {ble.BackendName}; dil: {L.Lang}; ayarlar: {AppLog.Scrub(StudioSettings.FilePath)}");

		if (OperatingSystem.IsMacOS())
		{
			// Trafik ışıkları içerikle aynı satırda (macOS'a özgü görünüm).
			ExtendClientAreaToDecorationsHint = true;
			ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
			ExtendClientAreaTitleBarHeightHint = 52;
			TrafficLightSpace.Width = 70;
			// Başlık satırının ortası (6 + 40/2 = 26) trafik ışıklarının ortasıyla (52/2) aynı hizada olsun.
			Header.Margin = new Thickness(0, -4, 0, 10);
		}
		else
		{
			FontFamily = new FontFamily("Inter, Noto Sans, DejaVu Sans, Noto Color Emoji");
		}

		Devices.ItemsSource = lamps;
		LanguageBox.SelectedIndex = settings.Language == L.Turkish ? 1 : settings.Language == L.English ? 2 : 0;
		string? themeOverride = Environment.GetEnvironmentVariable("CHIARO_THEME");
		darkTheme = themeOverride is "light" ? false : themeOverride is "dark" ? true : ReadTheme();
		ApplyTheme(darkTheme);
		VersionText.Text = $"Chiaro {MacUpdater.Current.Major}.{MacUpdater.Current.Minor} • Mac";
		HideButton.IsVisible = true;

		WireSliders();
		BuildPalette();
		BuildModeTiles();
		InitModeOptions();
		InitTimers();
		RebuildScenes();
		RebuildRecent();
		RebuildQuickSwatches();
		SetSilently(BrightnessSlider, 72);
		SetSilently(TemperatureSlider, 3000);
		BrightnessLabel.Text = L.Pct(BrightnessSlider.Value);
		TemperatureLabel.Text = $"{TemperatureSlider.Value:F0} K";
		StatusText.Text = L.T("status_connect_bulb");
		DetailText.Text = L.T("status_remembered");
		StateText.Text = L.T("not_connected_dot");
		InitMacExtras();
		ShowTab(UserModes.Contains(settings.SelectedTab) ? settings.SelectedTab : "Manuel");
		L.Changed += ApplyLanguage;
		initialized = true;
		ApplyLanguage();
		UpdateControls();
		UpdateBulbVisual();

		Opened += async (_, _) =>
		{
			if (started) return;
			started = true;
			InitTray();
			_ = AutoCheckUpdates();
			await StartupConnect();
			await ScreenshotIfRequested();
		};
		Closing += Window_Closing;
	}

	// =====================================================================
	//  Kayıt
	// =====================================================================

	private void Log(string message) => AppLog.Write(message);

	private void SaveSettings()
	{
		try
		{
			settings.Save();
		}
		catch (Exception ex)
		{
			Log("Ayarlar kaydedilemedi: " + ex.Message);
		}
	}

	private void ClampSettings()
	{
		settings.Rainbow.CycleSeconds = Math.Clamp(settings.Rainbow.CycleSeconds, 4, 120);
		settings.Rainbow.Saturation = Math.Clamp(settings.Rainbow.Saturation, 10, 100);
		settings.Rainbow.Brightness = Math.Clamp(settings.Rainbow.Brightness, 1, 100);
		settings.Rainbow.Direction = Math.Clamp(settings.Rainbow.Direction, 0, 2);
		settings.Rainbow.Palette = Math.Clamp(settings.Rainbow.Palette, 0, 5);
		settings.Fire.Intensity = Math.Clamp(settings.Fire.Intensity, 0, 100);
		settings.Fire.Speed = Math.Clamp(settings.Fire.Speed, 1, 10);
		settings.Fire.Warmth = Math.Clamp(settings.Fire.Warmth, 0, 100);
		settings.Fire.Brightness = Math.Clamp(settings.Fire.Brightness, 10, 100);
		settings.Daylight.MinKelvin = Math.Clamp(settings.Daylight.MinKelvin, 2200, 4000);
		settings.Daylight.MaxKelvin = Math.Clamp(settings.Daylight.MaxKelvin, 4000, 6500);
		settings.Daylight.MinBrightness = Math.Clamp(settings.Daylight.MinBrightness, 1, 60);
		settings.Daylight.MaxBrightness = Math.Clamp(settings.Daylight.MaxBrightness, 20, 100);
		settings.Screen.IntervalMs = Math.Clamp(settings.Screen.IntervalMs, 50, 1000);
		settings.Screen.Area = Math.Clamp(settings.Screen.Area, 0, 5);
		settings.Screen.Method = Math.Clamp(settings.Screen.Method, 0, 1);
		settings.Screen.Monitor = Math.Max(0, settings.Screen.Monitor);
		settings.Screen.Smoothing = Math.Clamp(settings.Screen.Smoothing, 0, 95);
		settings.Screen.SaturationBoost = Math.Clamp(settings.Screen.SaturationBoost, 0, 100);
		settings.Audio.Sensitivity = Math.Clamp(settings.Audio.Sensitivity, 1, 12);
		settings.Audio.Attack = Math.Clamp(settings.Audio.Attack, 10, 100);
		settings.Audio.Release = Math.Clamp(settings.Audio.Release, 5, 90);
		settings.Audio.MinBrightness = Math.Clamp(settings.Audio.MinBrightness, 1, 60);
		settings.Audio.MaxBrightness = Math.Clamp(settings.Audio.MaxBrightness, 20, 100);
		settings.Audio.IntervalMs = Math.Clamp(settings.Audio.IntervalMs, 100, 600);
		settings.Spotify.Brightness = Math.Clamp(settings.Spotify.Brightness, 5, 100);
		settings.Spotify.TransitionMs = Math.Clamp(settings.Spotify.TransitionMs, 200, 5000);
		settings.Spotify.Saturation = Math.Clamp(settings.Spotify.Saturation, 0, 100);
		settings.Wake.FadeMinutes = Math.Clamp(settings.Wake.FadeMinutes, 5, 60);
		settings.Wake.TargetBrightness = Math.Clamp(settings.Wake.TargetBrightness, 10, 100);
	}

	// =====================================================================
	//  Tema ve dil
	// =====================================================================

	private bool ReadTheme()
	{
		try
		{
			if (File.Exists(ThemePath))
			{
				using JsonDocument d = JsonDocument.Parse(File.ReadAllText(ThemePath));
				if (d.RootElement.TryGetProperty("Dark", out JsonElement v)) return v.GetBoolean();
			}
		}
		catch (Exception ex)
		{
			Log("Tema okunamadı: " + ex.Message);
		}
		return true;
	}

	private void Theme_Click(object? sender, RoutedEventArgs e)
	{
		ApplyTheme(!darkTheme);
		try
		{
			Directory.CreateDirectory(DataFolder);
			File.WriteAllText(ThemePath, JsonSerializer.Serialize(new { Dark = darkTheme }));
		}
		catch (Exception ex)
		{
			Log("Tema kaydı: " + ex.Message);
			DetailText.Text = L.T("theme_save_failed");
		}
	}

	private void ApplyTheme(bool dark)
	{
		darkTheme = dark;
		if (Application.Current != null) Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
		if (BgGradient.Background is LinearGradientBrush g && g.GradientStops.Count >= 2)
		{
			g.GradientStops[0].Color = dark ? Color.Parse("#1B1F2E") : Color.Parse("#F6F1EE");
			g.GradientStops[1].Color = dark ? Color.Parse("#272338") : Color.Parse("#E9ECF6");
		}
		ThemeButton.Content = dark ? L.T("theme_light") : L.T("theme_dark");
		ThemeButton2.Content = dark ? L.T("theme_light") : L.T("theme_dark");
		UpdateBulbVisual();
		RebuildPaletteBorders();
	}

	private void Language_Changed(object? sender, SelectionChangedEventArgs e)
	{
		if (!initialized) return;
		string value = LanguageBox.SelectedIndex switch { 1 => L.Turkish, 2 => L.English, _ => L.Auto };
		if (settings.Language == value) return;
		settings.Language = value;
		SaveSettings();
		L.Apply(value);
	}

	private void ApplyLanguage()
	{
		Tr.RefreshAll();
		ThemeButton.Content = darkTheme ? L.T("theme_light") : L.T("theme_dark");
		ThemeButton2.Content = darkTheme ? L.T("theme_light") : L.T("theme_dark");
		ScenesHint.Text = L.T("scenes_hint").Replace("sağ tıkla", "sağ tıkla / Ctrl-tıkla").Replace("Right-click", "Right-click / Ctrl-click");
		BrightnessLabel.Text = L.Pct(BrightnessSlider.Value);
		if (!busy)
		{
			StatusText.Text = ble.Ready ? L.F("connected_to", connectedName) : L.T("status_connect_bulb");
			if (!ble.Ready) DetailText.Text = L.T("status_remembered");
		}
		if (StateText.Text != null && !ble.Ready) StateText.Text = L.T("not_connected_dot");
		MetaText.Text = (ble.Ready ? L.Message(ble.Metadata) : "") + (demoMode ? (ble.Ready ? " • " : "") + "Demo" : "");
		BuildPalette();
		RebuildScenes();
		RebuildModeTilesText();
		InitModeOptionText();
		RebuildSchedules();
		UpdateTimerUi();
		UpdateDock();
		RebuildTrayMenu();
		RefreshLampNames();
		if (ScanSummary.Text == null || ScanSummary.Text.Length == 0) ScanSummary.Text = "";
	}

	private void RefreshLampNames()
	{
		// Görünen ad dile bağlı ("Hue adayı" / "Hue candidate"); ComboBox'ı yenile.
		object? sel = Devices.SelectedItem;
		Devices.ItemsSource = null;
		Devices.ItemsSource = lamps;
		Devices.SelectedItem = sel;
	}

	// =====================================================================
	//  Durum ve denetimler
	// =====================================================================

	private void UpdateControls()
	{
		bool ready = ble.Ready;
		ScanButton.IsEnabled = !busy;
		ConnectButton.IsEnabled = !busy && Devices.SelectedItem is Lamp;
		RefreshButton.IsEnabled = !busy && ready;
		DisconnectButton.IsEnabled = !busy && ready;
		CancelButton.IsVisible = busy;
		PowerToggle.IsEnabled = ready && !busy;
		PowerToggle.IsChecked = powerOn == true;
		BrightnessPanel.IsEnabled = ready && ble.Supports(3);
		TemperaturePanel.IsEnabled = ready && ble.Supports(4);
		HeaderDot.Fill = ready ? new SolidColorBrush(Color.Parse("#30D158")) : new SolidColorBrush(Color.Parse("#8A93A6"));
		HeaderStatus.Text = ready ? L.F("connected_to", connectedName) : L.T("not_connected");
		ModeStart.IsEnabled = ready && !busy && (modeName.Length > 0 || ModeSupported(selectedTab));
		ModeStart.Content = modeName.Length > 0 ? L.T("stop") : L.T("start");
		ModeBadge.IsVisible = modeName.Length > 0;
		ModeBadgeText.Text = modeName.Length > 0 ? L.Upper(L.Mode(modeName) + " • " + L.T("live")) : "";
		MetaText.Text = (ready ? L.Message(ble.Metadata) : "") + (demoMode ? (ready ? " • " : "") + "Demo" : "");
		UpdateDock();
		RebuildTrayMenu();
	}

	private void OnConnectionChanged(bool connected)
	{
		if (closed) return;
		if (!connected)
		{
			_ = StopModeAsync();
			powerOn = null;
			StatusText.Text = L.T("disconnected");
			DetailText.Text = L.T("out_of_range");
			StateText.Text = L.T("not_connected_dot");
			UpdateBulbVisual();
			UpdateControls();
			AutoReconnect(connectedKey);
		}
	}

	// =====================================================================
	//  Beklenmeyen kopmadan sonra (Mac uykudan uyandı, ampul elektrikten çekilip takıldı, menzil dışı)
	//  son ampule kendiliğinden yeniden bağlanma. Kullanıcı tara/bağlan/kes/unut/iptal derse durur.
	// =====================================================================

	private int reconnectGeneration;
	internal static readonly int[] ReconnectDelaysSeconds = { 3, 10, 30, 60, 120, 300 };

	private void StopAutoReconnect() => reconnectGeneration++;

	private async void AutoReconnect(string key)
	{
		if (string.IsNullOrEmpty(key) || closed) return;
		int gen = ++reconnectGeneration;
		Log("Kendiliğinden yeniden bağlanma planlandı.");
		foreach (int delay in ReconnectDelaysSeconds)
		{
			await Task.Delay(TimeSpan.FromSeconds(delay));
			if (gen != reconnectGeneration || closed || ble.Ready) return;
			if (busy || Devices.SelectedItem is not Lamp lamp || lamp.Key != key) continue;
			Log($"Kendiliğinden yeniden bağlanma denemesi ({delay} sn sonra).");
			ScanSummary.Text = L.T("reconnecting_last");
			await ConnectSelected(lamp);
			if (gen != reconnectGeneration || closed) return;
			if (ble.Ready)
			{
				ScanSummary.Text = L.T("connected_dot");
				return;
			}
		}
		if (gen == reconnectGeneration && !closed && !ble.Ready) ScanSummary.Text = L.T("connect_failed_retry");
	}

	private async Task<bool> Run(string title, Func<CancellationToken, Task> action, int seconds = 35)
	{
		if (busy || closed) return false;
		lastRunFailed = false;
		busy = true;
		operation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
		StatusText.Text = title;
		UpdateControls();
		bool gated = false;
		try
		{
			await StopModeAsync();
			operation.Token.ThrowIfCancellationRequested();
			await lightGate.WaitAsync(operation.Token);
			gated = true;
			await action(operation.Token);
			return true;
		}
		catch (OperationCanceledException)
		{
			lastRunFailed = true;
			if (!closed)
			{
				StatusText.Text = L.T("op_stopped");
				DetailText.Text = L.T("op_stopped_detail");
				StateText.Text = L.T("state_unknown");
			}
			ble.Disconnect();
			powerOn = null;
			Log("İptal / zaman aşımı");
			return false;
		}
		catch (Exception ex)
		{
			lastRunFailed = true;
			Log(ex.ToString());
			if (!closed)
			{
				StatusText.Text = L.T("error");
				DetailText.Text = Friendly(ex);
			}
			return false;
		}
		finally
		{
			if (gated) lightGate.Release();
			operation.Dispose();
			operation = null;
			busy = false;
			if (!closed)
			{
				UpdateBulbVisual();
				UpdateControls();
			}
		}
	}

	private static string Friendly(Exception ex) => ex switch
	{
		UnauthorizedAccessException => L.T("bt_denied"),
		InvalidOperationException => L.Message(ex.Message),
		_ => L.Message(ex.Message),
	};

	private void Cancel_Click(object? sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		StopAutoReconnect();
		operation?.Cancel();
	}

	// =====================================================================
	//  Tarama / bağlantı
	// =====================================================================

	private async void Scan_Click(object? sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		StopAutoReconnect();
		await ScanAsync();
	}

	private async Task ScanAsync()
	{
		await Run(L.T("searching"), async ct =>
		{
			ble.Disconnect();
			powerOn = null;
			StateText.Text = L.T("not_connected_dot");
			Lamp? remembered = lamps.FirstOrDefault();
			lamps.Clear();
			ScanSummary.Text = L.T("scanning");
			DetailText.Text = L.T("scanning_detail");
			int generation = ++scanGeneration;
			try
			{
				await ble.ScanAsync(lamp => Dispatcher.UIThread.Post(() =>
				{
					if (closed || generation != scanGeneration) return;
					int i = lamps.ToList().FindIndex(x => x.Key == lamp.Key);
					if (i >= 0)
					{
						Lamp old = lamps[i];
						Lamp merged = lamp with
						{
							Known = lamp.Known || old.Known,
							Rssi = lamp.Rssi ?? old.Rssi,
							Name = lamp.Name == "Hue adayı" ? old.Name : lamp.Name,
						};
						if (merged == old) return;
						bool selected = ReferenceEquals(Devices.SelectedItem, old);
						lamps[i] = merged;
						if (selected) Devices.SelectedItem = merged;
					}
					else
					{
						lamps.Add(lamp);
						if (Devices.SelectedIndex < 0) Devices.SelectedIndex = 0;
					}
				}), ct);
				await Task.Delay(50, ct);
				StatusText.Text = ble.Ready ? L.F("connected_to", connectedName) : lamps.Count > 0 ? L.T("pick_bulb") : L.T("no_bulb_found");
				DetailText.Text = lamps.Count > 0 ? L.T("pick_and_connect") : L.T("no_bulb_hint");
			}
			finally
			{
				scanGeneration++;
				ScanSummary.Text = L.F("bulbs_found", lamps.Count);
				if (lamps.Count == 0 && remembered != null)
				{
					lamps.Add(remembered);
					Devices.SelectedItem = remembered;
				}
			}
		}, 25);
	}

	private async void Connect_Click(object? sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		StopAutoReconnect();
		if (Devices.SelectedItem is Lamp lamp) await ConnectSelected(lamp);
	}

	private async Task ConnectSelected(Lamp lamp)
	{
		await Run(L.T("connecting"), async ct =>
		{
			await ble.ConnectAsync(lamp, text => Dispatcher.UIThread.Post(() => DetailText.Text = L.Message(text)), ct);
			connectedName = lamp.ShortName;
			connectedKey = lamp.Key;
			LightTitle.Text = lamp.ShortName;
			await RefreshState(ct);
			SaveLamp(lamp);
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("ready");
			SetHint(BrightnessHint, ble.Supports(3) ? "" : L.T("not_on_bulb"));
			SetHint(TemperatureHint, ble.Supports(4) ? "" : L.T("not_on_bulb"));
			SetHint(ColorHint, ble.Supports(5) ? "" : L.T("no_color_on_bulb"));
		}, 90);
	}

	private static void SetHint(TextBlock hint, string text)
	{
		hint.Text = text;
		hint.IsVisible = text.Length > 0;
	}

	private async Task RefreshState(CancellationToken ct)
	{
		byte[]? power = await ble.Read(2, ct);
		if (power == null || power.Length != 1 || power[0] > 1) throw new InvalidOperationException(L.T("power_unreadable"));
		powerOn = power[0] == 1;
		byte[]? brightness = await ble.ReadOptional(3, ct);
		byte[]? temp = await ble.ReadOptional(4, ct);
		string text = power[0] == 1 ? L.T("on") : L.T("off");
		if (brightness is { Length: 1 } && brightness[0] >= 1 && brightness[0] <= 254)
		{
			double pct = Math.Round(brightness[0] * 100.0 / 254.0);
			SetSilently(BrightnessSlider, Math.Max(1, pct));
			text += " • " + L.Pct(pct);
		}
		if (Protocol.Kelvin(temp) is int k)
		{
			SetSilently(TemperatureSlider, Math.Clamp(k, 2200, 6500));
			text += $" • {k} K";
		}
		StateText.Text = text;
		UpdateBulbVisual();
	}

	private async void Refresh_Click(object? sender, RoutedEventArgs e)
	{
		await Run(L.T("reading_state"), async ct =>
		{
			await RefreshState(ct);
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("ready");
		});
	}

	private async void Disconnect_Click(object? sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		StopAutoReconnect();
		await Run(L.T("disconnecting"), _ =>
		{
			ble.Disconnect();
			powerOn = null;
			StatusText.Text = L.T("disconnected");
			DetailText.Text = L.T("pairing_kept");
			StateText.Text = L.T("not_connected_dot");
			return Task.CompletedTask;
		});
	}

	private void Devices_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!initialized) return;
		if (Devices.SelectedItem is Lamp lamp && ble.Ready && lamp.Key != connectedKey && !busy)
		{
			StatusText.Text = L.T("new_bulb_selected");
			DetailText.Text = L.T("press_connect");
		}
		UpdateControls();
	}

	private void SaveLamp(Lamp lamp)
	{
		if (demoMode && OperatingSystem.IsMacOS()) return;
		try
		{
			Directory.CreateDirectory(DataFolder);
			File.WriteAllText(LampPath, JsonSerializer.Serialize(lamp));
		}
		catch (Exception ex)
		{
			Log("Cihaz kaydı: " + ex.Message);
		}
	}

	private Lamp? LoadLamp()
	{
		try
		{
			return File.Exists(LampPath) ? JsonSerializer.Deserialize<Lamp>(File.ReadAllText(LampPath)) : null;
		}
		catch (Exception ex)
		{
			Log("Kayıt okunamadı: " + ex.Message);
			return null;
		}
	}

	private async Task StartupConnect()
	{
		Lamp? lamp = LoadLamp();
		if (lamp == null && demoMode && Environment.GetEnvironmentVariable("CHIARO_DEMO_AUTOCONNECT") == "1")
		{
			lamp = new Lamp(DemoBackend.DemoId, "Hue Play", false, -52);
		}
		if (lamp == null || string.IsNullOrEmpty(lamp.Id)) return;
		lamps.Add(lamp);
		Devices.SelectedItem = lamp;
		ScanSummary.Text = L.T("reconnecting_last");
		try
		{
			for (int attempt = 0; attempt < 3; attempt++)
			{
				if (closed || autoCancelled || ble.Ready) break;
				if (busy || (Devices.SelectedItem as Lamp)?.Key != lamp.Key) return;
				await ConnectSelected(lamp);
				if (ble.Ready || autoCancelled || closed) break;
				await Task.Delay(TimeSpan.FromSeconds(10));
			}
		}
		finally
		{
		}
		if (!closed) ScanSummary.Text = ble.Ready ? L.T("connected_dot") : L.T("connect_failed_retry");
	}

	private async void Forget_Click(object? sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		StopAutoReconnect();
		try
		{
			if (File.Exists(LampPath)) File.Delete(LampPath);
			if (ble.Ready)
			{
				await Run(L.T("disconnecting"), _ => { ble.Disconnect(); return Task.CompletedTask; });
			}
			powerOn = null;
			lamps.Clear();
			connectedKey = "";
			StatusText.Text = L.T("bulb_forgotten");
			DetailText.Text = L.T("open_bt_settings_manual");
			StateText.Text = L.T("not_connected_dot");
			ScanSummary.Text = "";
		}
		catch (Exception ex)
		{
			Log("Unut: " + ex.Message);
			DetailText.Text = L.T("forget_failed");
		}
		UpdateControls();
	}

	// =====================================================================
	//  Güç / komut gönderme
	// =====================================================================

	private async Task<bool> Send(int code, byte[] value)
	{
		return await Run(L.T("sending"), async ct =>
		{
			await ble.Write(code, value, ct);
			if (code == 2) powerOn = value[0] == 1;
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("ready");
			try
			{
				await RefreshState(ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Log(ex.Message);
				DetailText.Text = L.T("applied_unread");
				StateText.Text = L.T("state_unknown");
			}
		});
	}

	private async Task SetPower(bool on)
	{
		if (!ble.Ready || !ble.Supports(2))
		{
			DetailText.Text = L.T("connect_first");
			UpdateBulbVisual();
			UpdateControls();
			return;
		}
		if (busy)
		{
			UpdateControls();
			return;
		}
		bool? before = powerOn;
		powerOn = on;
		UpdateBulbVisual();
		bool ok = await Send(2, Protocol.Power(on));
		if (!ok && ble.Ready) powerOn = before;
		UpdateBulbVisual();
		UpdateControls();
	}

	private async void PowerToggle_Click(object? sender, RoutedEventArgs e) => await SetPower(PowerToggle.IsChecked == true);

	private async void PowerQuick_Click(object? sender, RoutedEventArgs e) => await SetPower(!(powerOn ?? false));

	// =====================================================================
	//  Parlaklık / beyaz tonu (canlı yazım: en çok 10/sn, bırakınca kesin değer)
	// =====================================================================

	private readonly SortedDictionary<int, byte[]> livePending = new SortedDictionary<int, byte[]>();
	private readonly Dictionary<int, byte[]> liveLast = new Dictionary<int, byte[]>();
	private DispatcherTimer? liveTimer;
	private DateTime lastLiveWrite = DateTime.MinValue;
	private bool liveFlushing;
	private bool liveCommitPending;

	private void WireSliders()
	{
		BrightnessSlider.PropertyChanged += (_, e) =>
		{
			if (e.Property != RangeBase.ValueProperty) return;
			BrightnessLabel.Text = L.Pct(BrightnessSlider.Value);
			if (!initialized) return;
			if (modeName.Length == 0) UpdateBulbVisual();
			UpdateDock();
			if (silentSliders == 0 && BrightnessPanel.IsEnabled) LiveBrightness(BrightnessSlider.Value, final: false);
		};
		TemperatureSlider.PropertyChanged += (_, e) =>
		{
			if (e.Property != RangeBase.ValueProperty) return;
			TemperatureLabel.Text = $"{TemperatureSlider.Value:F0} K";
			if (initialized && silentSliders == 0 && TemperaturePanel.IsEnabled) TemperatureLive(final: false);
		};
		OnRelease(BrightnessSlider, () => { if (BrightnessPanel.IsEnabled) LiveBrightness(BrightnessSlider.Value, final: true); });
		OnRelease(TemperatureSlider, () => TemperatureLive(final: true));
	}

	/// <summary>Kaydırıcı bırakıldığında / klavyeyle değiştirildiğinde (işlenmiş olaylar dahil).</summary>
	private static void OnRelease(Slider s, Action act)
	{
		s.AddHandler(PointerReleasedEvent, (_, _) => act(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
		s.AddHandler(PointerCaptureLostEvent, (_, _) => act(), RoutingStrategies.Bubble, handledEventsToo: true);
		s.AddHandler(KeyUpEvent, (_, _) => act(), RoutingStrategies.Bubble, handledEventsToo: true);
	}

	private void SetSilently(RangeBase slider, double value)
	{
		silentSliders++;
		try
		{
			slider.Value = value;
		}
		finally
		{
			silentSliders--;
		}
	}

	private async void LiveSet(int code, byte[] value, bool final)
	{
		if (closed || !ble.Ready || !ble.Supports(code)) return;
		activeSceneId = null;
		if (ModeConflicts(code)) await StopModeAsync();
		livePending[code] = value;
		if (final) liveCommitPending = true;
		liveTimer ??= CreateLiveTimer();
		if (!liveTimer.IsEnabled) liveTimer.Start();
		if (final || (DateTime.Now - lastLiveWrite).TotalMilliseconds >= 100) await FlushLive();
	}

	private DispatcherTimer CreateLiveTimer()
	{
		DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		t.Tick += async (_, _) =>
		{
			if (livePending.Count > 0)
			{
				await FlushLive();
				return;
			}
			t.Stop();
			if (liveCommitPending)
			{
				liveCommitPending = false;
				OnLiveCommitted();
			}
		};
		return t;
	}

	private async Task FlushLive()
	{
		if (liveFlushing) return;
		liveFlushing = true;
		try
		{
			while (livePending.Count > 0 && !busy && ble.Ready && !closed)
			{
				KeyValuePair<int, byte[]> next = livePending.First();
				livePending.Remove(next.Key);
				if (liveLast.TryGetValue(next.Key, out byte[]? prev) && prev.SequenceEqual(next.Value)) continue;
				if (!await lightGate.WaitAsync(3000)) break;
				try
				{
					using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
					await ble.Write(next.Key, next.Value, cts.Token);
					liveLast[next.Key] = next.Value;
					lastLiveWrite = DateTime.Now;
					if (next.Key == 3) liveBrightness = null;
					else if (next.Key == 4) lastWasWhite = true;
					else if (next.Key == 5) lastWasWhite = false;
					UpdateBulbVisual();
				}
				catch (Exception ex)
				{
					Log("Canlı yazım: " + ex.Message);
					DetailText.Text = Friendly(ex);
				}
				finally
				{
					lightGate.Release();
				}
			}
		}
		finally
		{
			liveFlushing = false;
		}
	}

	private void OnLiveCommitted()
	{
		if (!lastWasWhite && colorKnown) AddRecent(currentColor);
		string state = (powerOn == true ? L.T("on") : powerOn == false ? L.T("off") : "") + $" • {L.Pct(BrightnessSlider.Value)}";
		if (lastWasWhite) state += $" • {TemperatureSlider.Value:F0} K";
		StateText.Text = state.TrimStart(' ', '•');
		UpdateControls();
	}

	internal void LiveBrightness(double percent, bool final)
	{
		percent = Math.Clamp(Math.Round(percent), 1, 100);
		if (Math.Abs(BrightnessSlider.Value - percent) > 0.4) SetSilently(BrightnessSlider, percent);
		liveBrightness = null;
		UpdateBulbVisual();
		LiveSet(3, Protocol.Brightness(percent), final);
	}

	private void LiveColorSet(MColor c, bool final)
	{
		currentColor = c;
		colorKnown = true;
		LiveSet(5, Protocol.Rgb(c.R, c.G, c.B), final);
	}

	private void TemperatureLive(bool final)
	{
		if (!TemperaturePanel.IsEnabled) return;
		double k = TemperatureSlider.Value;
		currentColor = ColorMath.FromKelvin(k);
		colorKnown = true;
		lastWasWhite = true;
		LiveSet(4, Protocol.Temperature(k), final);
		UpdateBulbVisual();
	}

	// =====================================================================
	//  Görsel: ampul, arka plan ışığı, alt çubuk
	// =====================================================================

	private static Color A(MColor c, byte? alpha = null) => Color.FromArgb(alpha ?? c.A, c.R, c.G, c.B);

	private void UpdateBulbVisual()
	{
		bool on = ble.Ready && powerOn == true;
		MColor c = colorKnown ? currentColor : MColor.FromRgb(0xFF, 0xD2, 0x71);
		double bri = (liveBrightness ?? BrightnessSlider.Value) / 100.0;
		Color glass = on ? A(c) : (darkTheme ? Color.Parse("#3A4256") : Color.Parse("#C9CED8"));
		BulbGlass.Fill = new RadialGradientBrush
		{
			GradientOrigin = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
			GradientStops =
			{
				new GradientStop(on ? Color.FromArgb(255, (byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60)) : glass, 0),
				new GradientStop(glass, 1),
			},
		};
		Filament.Stroke = new SolidColorBrush(on ? Color.FromArgb(220, 255, 255, 255) : Color.Parse("#8090A0"));
		byte haloA = on ? (byte)(70 + 150 * bri) : (byte)0;
		Halo.Fill = new RadialGradientBrush
		{
			GradientStops =
			{
				new GradientStop(A(c, haloA), 0),
				new GradientStop(A(c, (byte)(haloA / 3)), 0.45),
				new GradientStop(A(c, 0), 1),
			},
		};
		BlobLive.Fill = new RadialGradientBrush
		{
			GradientStops =
			{
				new GradientStop(on ? A(c, (byte)(darkTheme ? 120 : 90)) : Color.FromArgb(darkTheme ? (byte)70 : (byte)50, 0xFF, 0x9A, 0x3C), 0),
				new GradientStop(A(c, 0), 1),
			},
		};
		UpdateDock();
	}

	private void UpdateDock()
	{
		RefreshMini();
		if (DockTitle == null) return;
		bool ready = ble.Ready;
		MColor c = colorKnown ? currentColor : MColor.FromRgb(0x6B, 0x73, 0x85);
		DockColor.Fill = new SolidColorBrush(ready && colorKnown ? A(c) : Color.Parse("#6B7385"));
		DockBulb.Opacity = ready && powerOn == true ? 1 : 0.45;
		string name = !colorKnown ? L.T("color_unknown") : lastWasWhite ? $"{TemperatureSlider.Value:F0} K" : Palette.NameOf(c);
		string hex = ColorMath.Hex(c);
		DockTitle.Text = ready ? (colorKnown && !lastWasWhite && !string.Equals(name, hex, StringComparison.OrdinalIgnoreCase) ? $"{name}   {hex}" : name) : L.T("not_connected");
		DockSub.Text = modeName.Length > 0 ? L.F("modes_active", L.Mode(modeName)) : ready ? (powerOn == true ? L.T("on") : powerOn == false ? L.T("off") : L.T("unknown")) : L.T("no_color");
		DockBrightness.Text = L.Pct(liveBrightness ?? BrightnessSlider.Value);
		RebuildTrayStatus();
	}

	// =====================================================================
	//  Yardım, Bluetooth izni, kayıt dosyası, hakkında
	// =====================================================================

	private async void Help_Click(object? sender, RoutedEventArgs e) => await Dialogs.Info(this, L.T("help_title"), L.T("help_text"));

	internal async void About_Click(object? sender, RoutedEventArgs e) =>
		await Dialogs.Info(this, "Chiaro " + Program.Version, L.T("about_body"));

	private void BtSettings_Click(object? sender, RoutedEventArgs e)
	{
		OpenExternal("x-apple.systempreferences:com.apple.preference.security?Privacy_Bluetooth");
	}

	private void OpenLog_Click(object? sender, RoutedEventArgs e)
	{
		Log("Kayıt dosyası istendi.");
		DetailText.Text = L.F("mac_log_saved", AppLog.Scrub(AppLog.FilePath));
		if (OperatingSystem.IsMacOS()) StartProcess("/usr/bin/open", "-R", AppLog.FilePath);
		else OpenExternal(AppLog.Folder);
	}

	private void OpenExternal(string target)
	{
		if (OperatingSystem.IsMacOS()) StartProcess("/usr/bin/open", target);
		else StartProcess("xdg-open", target);
	}

	private void StartProcess(string file, params string[] args)
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo(file) { UseShellExecute = false };
			foreach (string a in args) psi.ArgumentList.Add(a);
			Process.Start(psi);
		}
		catch (Exception ex)
		{
			Log($"{file}: {ex.Message}");
		}
	}

	// =====================================================================
	//  Pencere: gizle / geri getir / çıkış
	// =====================================================================

	private void Hide_Click(object? sender, RoutedEventArgs e) => HideToTray();

	private void HideToTray()
	{
		Hide();
		Log("Pencere menü çubuğuna gizlendi.");
		UpdateDockIcon();
	}

	internal void RestoreWindow()
	{
		SetDockIcon(true);
		Show();
		if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
		Activate();
	}

	internal void PrepareForExit()
	{
		exitRequested = true;
	}

	internal void ExitApplication()
	{
		exitRequested = true;
		Close();
		if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
	}

	private void Window_Closing(object? sender, WindowClosingEventArgs e)
	{
		if (!exitRequested && tray != null && OperatingSystem.IsMacOS())
		{
			// macOS: kırmızı düğme pencereyi kapatır, uygulama menü çubuğunda sürer (Cmd+Q ile çıkılır).
			e.Cancel = true;
			HideToTray();
			return;
		}
		closed = true;
		modeCancellation?.Cancel();
		operation?.Cancel();
		clock?.Stop();
		liveTimer?.Stop();
		try { ble.Dispose(); } catch { }
		if (!exitRequested && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
		{
			d.Shutdown();
		}
	}
}
