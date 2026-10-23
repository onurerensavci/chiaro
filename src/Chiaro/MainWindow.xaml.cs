#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;

namespace HueStudio;

public partial class MainWindow : Window
{
	internal sealed record Appearance(bool Dark);

	private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

	// ---- Bağlantı durumu ----
	private readonly bool persistent;
	private bool autoCancelled;
	private CancellationTokenSource? modeCancellation;
	private string modeName = "";
	private Task modeTask = Task.CompletedTask;
	private bool darkTheme = true;
	private readonly ObservableCollection<Lamp> lamps = new ObservableCollection<Lamp>();
	private readonly List<string> logs = new List<string>();
	private readonly BleController ble;
	private CancellationTokenSource? operation;
	private bool busy;
	private bool closed;
	private bool lastRunFailed;
	private int scanGeneration;
	private string connectedName = "";
	private string connectedKey = "";
	private TrayController? tray;
	private bool exitRequested;
	private bool hideTipShown;
	private bool autoConnecting;
	/// <summary>Windows açılışında Run kaydıyla (--tray) başlatıldı mı? "Bilgisayar açılınca ışığı aç" yalnız bu durumda çalışır.</summary>
	private bool autostartLaunch;

	// ---- Ayarlar ----
	private readonly StudioSettings settings;
	private readonly bool initialized;
	private bool? powerOn;
	private Color currentColor = (Color)ColorConverter.ConvertFromString("#FFD271");
	private bool colorKnown;
	private double? liveBrightness;
	private string selectedTab = "Manuel";
	private bool glassActive;

	private readonly SolidColorBrush bulbFill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A4256"));
	private readonly SolidColorBrush filamentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8090A0"));
	private readonly SolidColorBrush dockBulbBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7385"));
	private readonly SolidColorBrush liveBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD271"));
	private readonly GradientStop halo0 = new GradientStop(Colors.Transparent, 0);
	private readonly GradientStop halo1 = new GradientStop(Colors.Transparent, 0.45);
	private readonly GradientStop halo2 = new GradientStop(Colors.Transparent, 1);
	private readonly GradientStop blobLive0 = new GradientStop(Colors.Transparent, 0);
	private readonly GradientStop blobLive1 = new GradientStop(Colors.Transparent, 1);
	private readonly GradientStop chipGlow0 = new GradientStop(Colors.Transparent, 0);

	// Renk tekerleği
	private const double WheelDip = 176;
	private double customHue = 28;
	private double customSat = 0.9;
	private Color customColor = (Color)ColorConverter.ConvertFromString("#FF8C1A");
	private bool wheelDragging;
	private bool updatingCustom;

	// Önizleme zamanlayıcısı (gökkuşağı yönü / şömine)
	private readonly DispatcherTimer previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
	private readonly Stopwatch previewClock = Stopwatch.StartNew();
	private double previewPhase;
	private double previewLast;
	private readonly FireEngine previewFire = new FireEngine();

	private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueBluetooth", "last-lamp.json");
	private static string ThemePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueBluetooth", "appearance.json");

	public MainWindow(bool enableTray = true)
	{
		InitializeComponent();
		persistent = enableTray;
		settings = persistent ? StudioSettings.Load() : new StudioSettings();
		Anim.Enabled = settings.Animations;

		// Seçenek listeleri DataContext'ten ÖNCE doldurulur (SelectedIndex bağları -1'e düşmesin).
		ScreenArea.ItemsSource = ScreenSampler.AreaNames;
		ScreenMethod.ItemsSource = ScreenSampler.MethodNames;
		RainbowDirectionBox.ItemsSource = RainbowEngine.DirectionNames;
		RainbowPaletteBox.ItemsSource = RainbowEngine.PaletteNames;
		ClampSettings();
		RefreshMonitors();
		ModeOptionsHost.DataContext = settings;
		ScreenMonitor.SelectionChanged += (_, _) =>
		{
			if (ScreenMonitor.SelectedIndex >= 0) settings.Screen.Monitor = ScreenMonitor.SelectedIndex;
		};
		ScreenInterval.ValueChanged += (_, _) => UpdateScreenIntervalText();
		UpdateScreenIntervalText();

		// Görsel fırçalar
		BulbGlass.Fill = bulbFill;
		BulbFilament.Stroke = filamentBrush;
		DockBulb.Fill = dockBulbBrush;
		DockChip.Fill = liveBrush;
		BulbHalo.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { halo0, halo1, halo2 } };
		BlobLive.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { blobLive0, blobLive1 } };
		DockChipGlow.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { chipGlow0, new GradientStop(Colors.Transparent, 1) } };
		Anim.AttachSwitch(PowerToggle);

		ApplyTheme(!persistent || ReadTheme());
		AutoStartBox.IsChecked = settings.AutoStart;
		UpdateLightOnStartupBox();
		SourceInitialized += (_, _) =>
		{
			ApplyTitleTheme();
			ApplyGlass();
		};
		if (enableTray)
		{
			ContentRendered += StartupConnect;
			Width = Math.Min(1240.0, SystemParameters.WorkArea.Width);
			Height = Math.Min(980.0, SystemParameters.WorkArea.Height);
			MinHeight = Math.Min(660.0, Height);
			MinWidth = Math.Min(960.0, Width);
		}
		Devices.ItemsSource = lamps;
		ble = new BleController(Log);
		ble.ConnectionChanged += (bool connected) =>
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (!(closed | connected) && ble.Ready)
				{
					operation?.Cancel();
					modeCancellation?.Cancel();
					ble.Disconnect();
					powerOn = null;
					StatusText.Text = L.T("disconnected");
					DetailText.Text = L.T("out_of_range");
					StateText.Text = L.T("state_unknown");
					UpdateBulbVisual(true);
					UpdateControls();
				}
			}));
		};
		VersionLabel.Text = "Chiaro " + Updater.CurrentText + BuildTag.Suffix;
		// Deneme yapısı: başlık ve sürüm etiketi ayırt edilsin (ör. "Chiaro 3.6 • test1").
		if (BuildTag.Test != null) Title = "Chiaro " + Updater.CurrentText + BuildTag.Suffix;
		Log($"Chiaro {Updater.CurrentText}; Windows {Environment.OSVersion}; .NET {Environment.Version}; x64={Environment.Is64BitProcess}");
		if (enableTray)
		{
			EnableTray(visible: true);
		}
		Closing += (object? _, CancelEventArgs e) =>
		{
			if (!exitRequested && tray != null)
			{
				e.Cancel = true;
				HideToTray();
			}
		};
		StateChanged += (_, _) =>
		{
			if (WindowState == WindowState.Minimized && tray != null)
			{
				HideToTray();
			}
		};
		IsVisibleChanged += (_, _) => UpdateAmbientMotion();
		Closed += (_, _) =>
		{
			closed = true;
			scanGeneration++;
			previewTimer.Stop();
			operation?.Cancel();
			modeCancellation?.Cancel();
			SaveSettings();
			DisposeAutomation();
			tray?.Dispose();
			ble.Dispose();
		};

		// Bölümler, palet, tekerlek
		BuildPalette();
		BuildWheel();
		SetCustomFromColor(customColor, updateHex: true);
		RebuildQuickSwatches();
		RebuildRecent();
		InitSections();
		UpdateRainbowBar();
		previewTimer.Tick += PreviewTick;
		initialized = true;
		InitAutomation();
		InitLanguage();
		ShowTab(settings.SelectedTab, animate: false);
		UpdateBulbVisual(false);
		UpdateControls();
		Loaded += (_, _) => UpdateAmbientMotion();
	}

	// =====================================================================
	//  Ayarlar
	// =====================================================================

	private void ClampSettings()
	{
		settings.Screen.Area = Math.Clamp(settings.Screen.Area, 0, ScreenSampler.AreaNames.Length - 1);
		settings.Screen.Method = Math.Clamp(settings.Screen.Method, 0, ScreenSampler.MethodNames.Length - 1);
		settings.Rainbow.Direction = Math.Clamp(settings.Rainbow.Direction, 0, RainbowEngine.DirectionNames.Length - 1);
		settings.Rainbow.Palette = Math.Clamp(settings.Rainbow.Palette, 0, RainbowEngine.PaletteNames.Length - 1);
		settings.Favorites = settings.Favorites.Where(h => ColorMath.TryParseHex(h, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
		settings.Recent = settings.Recent.Where(h => ColorMath.TryParseHex(h, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
	}

	private void SaveSettings()
	{
		if (!persistent) return;
		try
		{
			settings.Save();
		}
		catch (Exception ex)
		{
			Log("Ayarlar kaydedilemedi: " + ex.Message);
		}
	}

	private void RefreshMonitors()
	{
		string[] names;
		try
		{
			names = ScreenSampler.MonitorNames();
		}
		catch (Exception ex)
		{
			Log("Ekran listesi: " + ex.Message);
			names = new[] { L.T("main_display") };
		}
		int keep = settings.Screen.Monitor;
		ScreenMonitor.ItemsSource = names;
		ScreenMonitor.SelectedIndex = Math.Clamp(keep, 0, Math.Max(0, names.Length - 1));
	}

	private void ScreenMonitor_DropDownOpened(object? sender, EventArgs e) => RefreshMonitors();

	private void UpdateScreenIntervalText()
	{
		double ms = Math.Max(1, ScreenInterval.Value);
		ScreenIntervalText.Text = L.F("interval_fmt", ms, 1000.0 / ms);
	}

	// =====================================================================
	//  Bölüm aç/kapa
	// =====================================================================


	// =====================================================================
	//  Ampul görseli (parlama animasyonu)
	// =====================================================================

	private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

	private static void SetBrushColor(SolidColorBrush brush, Color c, int ms)
	{
		if (ms <= 0)
		{
			brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
			brush.Color = c;
		}
		else
		{
			Anim.Color(brush, c, ms);
		}
	}

	private static void SetStop(GradientStop stop, Color c, int ms)
	{
		if (ms <= 0)
		{
			stop.BeginAnimation(GradientStop.ColorProperty, null);
			stop.Color = c;
		}
		else
		{
			Anim.Color(stop, c, ms);
		}
	}

	private static void SetDouble(IAnimatable target, DependencyProperty p, double value, int ms, IEasingFunction? ease = null)
	{
		if (ms <= 0)
		{
			target.BeginAnimation(p, null);
			((DependencyObject)target).SetValue(p, value);
		}
		else
		{
			target.BeginAnimation(p, Anim.To(value, ms, ease));
		}
	}

	/// <param name="animate">Aç/kapa geçişi için uzun animasyon; canlı modlarda kısa geçiş.</param>
	private void UpdateBulbVisual(bool animate, int quickMs = 0)
	{
		if (!initialized) return;
		bool on = powerOn == true;
		double bri = Math.Clamp((liveBrightness ?? BrightnessSlider.Value) / 100.0, 0.01, 1.0);
		Color c = currentColor;
		int ms = animate ? 520 : quickMs;
		Color offGlass = darkTheme ? C("#3A4256") : C("#C3CAD6");
		Color glass = on ? ColorMath.Lerp(c, Colors.White, 0.30) : offGlass;
		SetBrushColor(bulbFill, glass, ms);
		SetBrushColor(filamentBrush, on ? ColorMath.Lerp(c, Colors.White, 0.75) : C("#8090A0"), ms);
		SetBrushColor(dockBulbBrush, on ? ColorMath.Lerp(c, Colors.White, 0.2) : C("#6B7385"), ms);
		SetStop(halo0, WithAlpha(c, 230), ms);
		SetStop(halo1, WithAlpha(c, 90), ms);
		SetStop(halo2, WithAlpha(c, 0), ms);
		SetStop(chipGlow0, WithAlpha(c, 200), ms);
		SetStop(blobLive0, WithAlpha(c, on ? (byte)200 : (byte)70), animate ? 900 : ms);
		SetStop(blobLive1, WithAlpha(c, 0), animate ? 900 : ms);
		SetBrushColor(liveBrush, c, ms);

		double haloOpacity = on ? 0.30 + 0.70 * bri : 0.0;
		SetDouble(BulbHalo, OpacityProperty, haloOpacity, ms);
		SetDouble(DockChipGlow, OpacityProperty, on ? 0.9 : 0.0, ms);
		double scale = on ? 0.85 + 0.2 * bri : 0.7;
		SetDouble(BulbHaloScale, ScaleTransform.ScaleXProperty, scale, ms, animate ? Anim.Spring : null);
		SetDouble(BulbHaloScale, ScaleTransform.ScaleYProperty, scale, ms, animate ? Anim.Spring : null);
		BulbShadow.Color = c;
		SetDouble(BulbShadow, System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, on ? 18 + 32 * bri : 0, ms);
		SetDouble(BulbShadow, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, on ? 0.95 : 0, ms);

		if (PowerToggle.IsChecked != on)
		{
			PowerToggle.IsChecked = on;
		}
		string name = colorKnown ? Palette.NameOf(c) : L.T("no_color");
		DockColorName.Text = (!colorKnown || name == ColorMath.Hex(c)) ? name : $"{name}  {ColorMath.Hex(c)}";
		DockDetail.Text = DockSummary();
		DockBrightness.Text = L.Pct(Math.Round(liveBrightness ?? BrightnessSlider.Value));
		RaiseLightState();
	}

	private string DockSummary()
	{
		string mode = L.Mode(modeName.Length == 0 ? "Manuel" : modeName);
		if (!ble.Ready || powerOn == null) return mode;
		return mode + " • " + (powerOn == true ? L.T("on") : L.T("off"));
	}

	private static void SetHint(TextBlock hint, string text)
	{
		hint.Text = text;
		hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void SetLiveColor(Color c, double? brightness, int ms = 140)
	{
		currentColor = c;
		colorKnown = true;
		liveBrightness = brightness;
		UpdateBulbVisual(false, ms);
	}

	private void UpdateAmbientMotion()
	{
		bool run = IsVisible && !closed && Anim.Enabled;
		Drift(BlobLive, 60, 40, 23, run);
		Drift(BlobA, -70, -50, 29, run);
		Drift(BlobB, 50, -60, 19, run);
		UpdatePreviewTimer();
	}

	private static void Drift(FrameworkElement e, double dx, double dy, double seconds, bool run)
	{
		if (e.RenderTransform is not TranslateTransform t || t.IsFrozen)
		{
			t = new TranslateTransform();
			e.RenderTransform = t;
		}
		if (!run)
		{
			t.BeginAnimation(TranslateTransform.XProperty, null);
			t.BeginAnimation(TranslateTransform.YProperty, null);
			return;
		}
		DoubleAnimation ax = new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
		DoubleAnimation ay = new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
		Timeline.SetDesiredFrameRate(ax, 24);
		Timeline.SetDesiredFrameRate(ay, 24);
		t.BeginAnimation(TranslateTransform.XProperty, ax);
		t.BeginAnimation(TranslateTransform.YProperty, ay);
	}

	// =====================================================================
	//  Açılışta otomatik bağlanma
	// =====================================================================

	private void SaveLamp(Lamp lamp)
	{
		if (!persistent)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			File.WriteAllText(SettingsPath, JsonSerializer.Serialize(lamp));
		}
		catch (Exception ex)
		{
			Log("Cihaz kaydı: " + ex.Message);
		}
	}

	private async void StartupConnect(object? sender, EventArgs e)
	{
		ContentRendered -= StartupConnect;
		// "Bilgisayar açılınca ışığı aç": yalnız Run kaydıyla (--tray) açılışta; elle açılışta ya da --updated ile yeniden açılışta değil.
		bool lightUp = persistent && StartupLight.ShouldRun(autostartLaunch, settings.LightOnStartup, settings.AutoStart);
		if (persistent && settings.LightOnStartup)
		{
			Log(lightUp ? "Bilgisayar açılınca ışığı aç: otomatik başlatma algılandı (--tray); son ampule bağlanılıp ışık açılacak (en çok ~60 sn yeniden denenir)."
				: autostartLaunch ? "Bilgisayar açılınca ışığı aç: Windows ile başlat kapalı, atlandı."
				: "Bilgisayar açılınca ışığı aç: elle açılış (otomatik başlatma değil), ışık açılmayacak.");
		}
		Lamp? lamp;
		try
		{
			lamp = (File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Lamp>(File.ReadAllText(SettingsPath)) : null);
		}
		catch (Exception ex)
		{
			Log("Kayıt okunamadı: " + ex.Message);
			return;
		}
		if (lamp is null || lamp.Address == 0L || lamp.Address > 281474976710655L)
		{
			if (lightUp) Log("Bilgisayar açılınca ışığı aç: hatırlanan ampul yok, atlandı.");
			return;
		}
		lamps.Add(lamp);
		Devices.SelectedItem = lamp;
		ScanSummary.Text = L.T("reconnecting_last");
		autoConnecting = true;
		Stopwatch elapsed = Stopwatch.StartNew();
		try
		{
		for (int attempt = 0; ; attempt++)
		{
			if (closed || autoCancelled || ble.Ready)
			{
				break;
			}
			if (lightUp && busy)
			{
				// Başka bir otomatik işlem (ör. program) bağlanıyorsa bitmesini bekle; vazgeçme.
				while (busy && !closed && elapsed.Elapsed < StartupLight.Window) await Task.Delay(500);
				if (busy || closed) break;
				continue;
			}
			if (busy || (Devices.SelectedItem as Lamp)?.Key != lamp.Key)
			{
				if (lightUp) Log("Bilgisayar açılınca ışığı aç: kullanıcı başka bir ampul/işlem seçti, vazgeçildi.");
				return;
			}
			if (lightUp) Log($"Bilgisayar açılınca ışığı aç: bağlantı denemesi {attempt + 1} ({elapsed.Elapsed.TotalSeconds:F0} sn).");
			await ConnectSelected(lamp);
			if (ble.Ready || autoCancelled || closed)
			{
				break;
			}
			TimeSpan wait = TimeSpan.FromSeconds(10L);
			if (lightUp)
			{
				// Bluetooth oturum açılışında hemen hazır olmayabilir: artan aralıklarla ~60 sn boyunca yeniden dene.
				if (StartupLight.NextDelay(attempt, elapsed.Elapsed) is not TimeSpan next)
				{
					Log($"Bilgisayar açılınca ışığı aç: {attempt + 1} denemede bağlanılamadı ({elapsed.Elapsed.TotalSeconds:F0} sn), vazgeçildi.");
					break;
				}
				wait = next;
				Log($"Bilgisayar açılınca ışığı aç: bağlanılamadı, {wait.TotalSeconds:F0} sn sonra yeniden denenecek.");
			}
			else if (attempt >= 2)
			{
				break;
			}
			await Task.Delay(wait);
		}
		}
		finally
		{
			autoConnecting = false;
		}
		if (!closed)
		{
			ScanSummary.Text = (ble.Ready ? L.T("connected_dot") : L.T("connect_failed_retry"));
		}
		if (lightUp && !closed)
		{
			if (ble.Ready) await StartupPowerOn();
			else if (autoCancelled) Log("Bilgisayar açılınca ışığı aç: kullanıcı iptal etti / taradı, ışığa dokunulmadı.");
		}
	}

	/// <summary>Açılışta bağlandıktan sonra bir kez: ışığı açar. Ampul son rengi ve parlaklığıyla yanar (Hue ampul bunları kendisi saklar).</summary>
	private async Task StartupPowerOn()
	{
		for (int i = 0; i < 40 && busy; i++) await Task.Delay(250);
		string? skip = StartupLight.SkipReason(autoCancelled, modeName, powerOn);
		if (skip != null)
		{
			Log("Bilgisayar açılınca ışığı aç: " + skip + ", ışığa dokunulmadı.");
			return;
		}
		if (!ble.Supports(2))
		{
			Log("Bilgisayar açılınca ışığı aç: ampul güç komutunu desteklemiyor.");
			return;
		}
		await SetPower(true);
		bool ok = powerOn == true && !lastRunFailed && ble.Ready;
		Log("Bilgisayar açılınca ışığı aç: " + (ok ? "ışık açıldı." : "ışık açılamadı."));
		if (ok) DetailText.Text = L.T("light_on_startup_done");
	}

	private void Forget_Click(object sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		try
		{
			if (File.Exists(SettingsPath))
			{
				File.Delete(SettingsPath);
			}
			ScanSummary.Text = L.T("bulb_forgotten");
		}
		catch (Exception ex)
		{
			Log(ex.ToString());
			ScanSummary.Text = L.T("forget_failed");
		}
	}

	// =====================================================================
	//  Modlar
	// =====================================================================


	private void ShowTab(string tab, bool animate)
	{
		if (!UserModes.Contains(tab)) tab = "Manuel";
		selectedTab = tab;
		settings.SelectedTab = tab;
		TabManual.IsChecked = tab == "Manuel";
		TabScreen.IsChecked = tab == "Ekran";
		TabAudio.IsChecked = tab == "Ses";
		TabRainbow.IsChecked = tab == "Gökkuşağı";
		TabFire.IsChecked = tab == "Şömine";
		TabSpotify.IsChecked = tab == "Spotify";
		TabDaylight.IsChecked = tab == "Gün ışığı";
		(string Tab, FrameworkElement Panel)[] panels =
		{
			("Manuel", OptManual), ("Ekran", OptScreen), ("Ses", OptAudio), ("Spotify", OptSpotify), ("Gökkuşağı", OptRainbow), ("Şömine", OptFire), ("Gün ışığı", OptDaylight),
		};
		foreach ((string t, FrameworkElement p) in panels)
		{
			if (t == tab)
			{
				if (animate && p.Visibility != Visibility.Visible) Anim.FadeIn(p);
				else p.Visibility = Visibility.Visible;
			}
			else
			{
				p.Visibility = Visibility.Collapsed;
			}
		}
		if (tab == "Şömine") ShowFirePreview();
		if (tab == "Gün ışığı") Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)UpdateDaylightBar);
		UpdatePreviewTimer();
		UpdateControls();
	}

	private void UpdatePreviewTimer()
	{
		bool need = initialized && !closed && IsVisible && ModesVisible && (selectedTab == "Gökkuşağı" || selectedTab == "Şömine");
		if (need && !previewTimer.IsEnabled)
		{
			previewLast = previewClock.Elapsed.TotalSeconds;
			previewTimer.Start();
		}
		else if (!need && previewTimer.IsEnabled)
		{
			previewTimer.Stop();
		}
	}

	private void PreviewTick(object? sender, EventArgs e)
	{
		double now = previewClock.Elapsed.TotalSeconds;
		double dt = Math.Clamp(now - previewLast, 0, 0.5);
		previewLast = now;
		if (selectedTab == "Gökkuşağı" && modeName != "Gökkuşağı")
		{
			previewPhase += dt / Math.Max(1.0, settings.Rainbow.CycleSeconds);
			MoveRainbowMarker(RainbowEngine.Position(previewPhase, 1.0, (RainbowDirection)settings.Rainbow.Direction));
		}
		else if (selectedTab == "Şömine" && modeName != "Şömine")
		{
			ShowFirePreview();
		}
	}

	private void ShowFirePreview()
	{
		FireOptions o = settings.Fire;
		LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
		for (int i = 0; i < 5; i++)
		{
			(Color c, double b) = previewFire.Next(o.Intensity / 100.0, o.Speed, o.Warmth / 100.0, o.Brightness, o.Sparks);
			double k = Math.Clamp(b / 100.0, 0.15, 1.0);
			g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k)), i / 4.0));
		}
		g.Freeze();
		FirePreview.Background = g;
	}

	private void FireWarmth_Changed(object sender, RoutedEventArgs e)
	{
		if (initialized) ShowFirePreview();
	}

	private void RainbowPalette_Changed(object sender, RoutedEventArgs e)
	{
		if (initialized) UpdateRainbowBar();
	}

	private void UpdateRainbowBar()
	{
		LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
		int palette = Math.Clamp(settings.Rainbow.Palette, 0, RainbowEngine.PaletteNames.Length - 1);
		if (RainbowPaletteBox.SelectedIndex >= 0) palette = RainbowPaletteBox.SelectedIndex;
		double sat = (RainbowSat.Value > 0 ? RainbowSat.Value : settings.Rainbow.Saturation) / 100.0;
		for (int i = 0; i <= 24; i++)
		{
			double p = i / 24.0;
			g.GradientStops.Add(new GradientStop(RainbowEngine.ColorAt(p, palette, sat), p));
		}
		g.Freeze();
		RainbowBar.Background = g;
	}

	private void MoveRainbowMarker(double position)
	{
		double h = RainbowMarkerCanvas.ActualHeight;
		if (h <= 0) return;
		Canvas.SetTop(RainbowMarker, Math.Clamp(position, 0, 1) * Math.Max(0, h - RainbowMarker.Height));
		RainbowMarker.Fill = new SolidColorBrush(RainbowEngine.ColorAt(position, settings.Rainbow.Palette, settings.Rainbow.Saturation / 100.0));
	}

	private async void ModeTile_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not RadioButton { Tag: string tab }) return;
		ShowTab(tab, animate: true);
		activeSceneId = null;
		if (tab == "Manuel")
		{
			if (modeCancellation != null) await StopModeAsync();
			return;
		}
		if (modeName == tab) return;
		if (!ble.Ready)
		{
			LiveText.Text = L.T("connect_to_start");
			return;
		}
		if (!ModeSupported(tab))
		{
			LiveText.Text = UnsupportedText(tab);
			return;
		}
		await StartMode(tab);
	}

	private async void ModeStart_Click(object sender, RoutedEventArgs e)
	{
		if (selectedTab == "Manuel" || modeName == selectedTab)
		{
			await StopModeAsync();
		}
		else
		{
			await StartMode(selectedTab);
		}
	}

	private async Task StopModeAsync()
	{
		modeCancellation?.Cancel();
		await modeTask;
	}

	private async Task StartMode(string mode, double? brightness = null)
	{
		if (busy || closed || !ble.Ready || !ModeSupported(mode))
		{
			return;
		}
		await Run(L.F("mode_starting", L.Mode(mode)), async (CancellationToken ct) =>
		{
			if (mode == "Ses")
			{
				using AmbientInput.AudioReader audioReader = new AmbientInput.AudioReader();
				audioReader.Peak();
			}
			if (mode == "Uyandırma" && powerOn != true)
			{
				// Gün doğumu karanlıktan başlasın: önce loş kızıl, sonra aç.
				if (ble.Supports(3)) await ble.Write(3, Protocol.Brightness(1), ct);
				Color dawn = SunriseColor(0, settings.Wake.TargetKelvin);
				if (ble.Supports(5)) await ble.Write(5, Protocol.Rgb(dawn.R, dawn.G, dawn.B), ct);
			}
			if (mode != "Uyku")
			{
				await ble.Write(2, Protocol.Power(on: true), ct);
				powerOn = true;
			}
			if (brightness.HasValue && ble.Supports(3))
			{
				await ble.Write(3, Protocol.Brightness(brightness.Value), ct);
				SetSilently(BrightnessSlider, Math.Clamp(Math.Round(brightness.Value), 1, 100));
				if (mode == "Spotify") settings.Spotify.Brightness = brightness.Value;
			}
		}, 10);
		if (!closed && ble.Ready && !lastRunFailed)
		{
			autoCancelled = true;
			modeCancellation = new CancellationTokenSource();
			modeName = mode;
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("applied_instantly");
			if (selectedTab != mode && UserModes.Contains(mode)) ShowTab(mode, animate: true);
			liveLast.Clear();
			UpdateBulbVisual(true);
			UpdateControls();
			modeTask = ModeLoop(mode, modeCancellation);
		}
	}

	private async Task ModeLoop(string mode, CancellationTokenSource cts)
	{
		using AmbientInput.AudioReader? audio = mode == "Ses" ? new AmbientInput.AudioReader() : null;
		using ScreenSampler screen = new ScreenSampler();
		FireEngine fire = new FireEngine();
		double level = settings.Audio.MinBrightness;
		double r = 0.0, g = 0.0, b = 0.0, luma = 0.5;
		bool first = true;
		byte[]? last = null;
		byte[]? lastBri = null;
		double phase = 0;
		Stopwatch silent = Stopwatch.StartNew();
		Stopwatch clock = Stopwatch.StartNew();
		double lastTime = 0;
		byte[]? lastTemp = null;
		// Spotify geçişi
		MediaColorReader.Track? track = null;
		double lastPoll = -10;
		Color spFrom = currentColor, spTo = currentColor, spNow = currentColor;
		double spStart = 0;
		bool spHasTarget = false;
		bool finished = false;
		try
		{
			while (!cts.IsCancellationRequested)
			{
				Stopwatch frame = Stopwatch.StartNew();
				double now = clock.Elapsed.TotalSeconds;
				double dt = Math.Clamp(now - lastTime, 0, 1);
				lastTime = now;
				byte[]? value = null;
				byte[]? temp = null;
				bool powerOff = false;
				double? bri = null;
				int briThreshold = 3;
				int interval = 160;
				if (mode == "Ekran")
				{
					ScreenOptions o = settings.Screen;
					interval = (int)Math.Clamp(o.IntervalMs, 50, 1000);
					int monitor = o.Monitor, area = o.Area, method = o.Method;
					bool ignoreDark = o.IgnoreDark;
					(byte R, byte G, byte B, double Luma) s = await Task.Run(() => screen.Read(monitor, area, method, ignoreDark), cts.Token);
					double alpha = first ? 1.0 : Math.Clamp(1.0 - o.Smoothing / 100.0, 0.05, 1.0);
					r += (s.R - r) * alpha;
					g += (s.G - g) * alpha;
					b += (s.B - b) * alpha;
					luma += (s.Luma - luma) * alpha;
					Color col = Color.FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
					if (o.SaturationBoost > 0)
					{
						(double h, double sat, double v) = ColorMath.ToHsv(col);
						col = ColorMath.FromHsv(h, Math.Min(1.0, sat * (1.0 + o.SaturationBoost / 100.0 * 1.5)), v);
					}
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (o.FollowBrightness)
					{
						bri = 5.0 + 95.0 * Math.Sqrt(Math.Clamp(luma, 0, 1));
					}
					SetLiveColor(col, bri);
					LiveText.Text = L.F("live_screen", ScreenSampler.AreaNames[Math.Clamp(area, 0, ScreenSampler.AreaNames.Length - 1)], 1000.0 / interval);
					first = false;
				}
				else if (mode == "Gökkuşağı")
				{
					RainbowOptions o = settings.Rainbow;
					interval = (int)Math.Clamp(o.IntervalMs, 100, 1000);
					phase += dt / Math.Max(1.0, o.CycleSeconds);
					double pos = RainbowEngine.Position(phase, 1.0, (RainbowDirection)o.Direction);
					Color col = RainbowEngine.ColorAt(pos, o.Palette, o.Saturation / 100.0);
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (o.UseBrightness)
					{
						bri = o.Brightness;
						briThreshold = 1;
					}
					SetLiveColor(col, bri);
					MoveRainbowMarker(pos);
					LiveText.Text = L.F("live_rainbow", RainbowEngine.DirectionNames[Math.Clamp(o.Direction, 0, RainbowEngine.DirectionNames.Length - 1)], o.CycleSeconds);
				}
				else if (mode == "Şömine")
				{
					FireOptions o = settings.Fire;
					interval = FireEngine.FrameMilliseconds(o.Speed);
					(Color col, double fb) = fire.Next(o.Intensity / 100.0, o.Speed, o.Warmth / 100.0, o.Brightness, o.Sparks);
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (ble.Supports(3))
					{
						bri = fb;
						briThreshold = 2;
					}
					SetLiveColor(col, bri ?? o.Brightness, 90);
					if (selectedTab == "Şömine") ShowFirePreview();
					LiveText.Text = ble.Supports(3) ? L.T("live_fire") : L.T("live_fire_color");
				}
				else if (mode == "Spotify")
				{
					SpotifyOptions o = settings.Spotify;
					if (now - lastPoll >= 0.8)
					{
						lastPoll = now;
						try
						{
							track = await media.ReadAsync(o.OnlySpotify, cts.Token);
							ShowNowPlaying(track);
						}
						catch (OperationCanceledException) when (cts.IsCancellationRequested)
						{
							throw;
						}
						catch (Exception ex)
						{
							track = null;
							ShowNowPlaying(null, L.T("media_unreadable"));
							Log("Spotify: " + ex.Message);
						}
						if (track?.Color is Color tc)
						{
							Color target = MediaColorReader.Boost(tc, o.Saturation);
							if (!spHasTarget || target != spTo)
							{
								spFrom = spHasTarget ? spNow : currentColor;
								spTo = target;
								spStart = now;
								spHasTarget = true;
							}
						}
					}
					double fade = Math.Max(0.2, o.TransitionMs / 1000.0);
					double t = spHasTarget ? Math.Clamp((now - spStart) / fade, 0, 1) : 1;
					if (spHasTarget)
					{
						double e = t * t * (3 - 2 * t);
						spNow = ColorMath.Lerp(spFrom, spTo, e);
						value = Protocol.Rgb(spNow.R, spNow.G, spNow.B);
						SetLiveColor(spNow, o.Brightness, 110);
					}
					bri = o.Brightness;
					briThreshold = 1;
					interval = t < 1 ? 110 : 400;
					LiveText.Text = track == null ? L.T("nothing_playing_wait") : track.Playing ? L.F("live_value", track.Title) : L.T("paused");
				}
				else if (mode == "Gün ışığı")
				{
					(double k, double db) = Daylight.At(settings.Daylight, DateTime.Now);
					if (ble.Supports(4))
					{
						temp = Protocol.Temperature(k);
					}
					else
					{
						Color kc = ColorMath.FromKelvin(k);
						value = Protocol.Rgb(kc.R, kc.G, kc.B);
					}
					bri = db;
					briThreshold = 1;
					interval = 1000;
					lastWasWhite = ble.Supports(4);
					SetLiveColor(ColorMath.FromKelvin(k), db, 600);
					LiveText.Text = L.F("live_daylight", k, L.Pct(db));
				}
				else if (mode == "Uyku")
				{
					double total = Math.Max(1, (sleepFadeEnd - sleepFadeStart).TotalSeconds);
					double t = Math.Clamp((DateTime.Now - sleepFadeStart).TotalSeconds / total, 0, 1);
					bri = Math.Max(1, sleepFadeFrom + (1 - sleepFadeFrom) * t);
					briThreshold = 1;
					interval = 1000;
					SetLiveColor(currentColor, bri, 900);
					LiveText.Text = L.F("sleep_progress", Remaining(sleepFadeEnd));
					if (t >= 1)
					{
						powerOff = true;
					}
				}
				else if (mode == "Uyandırma")
				{
					WakeAlarm o = settings.Wake;
					double t = Math.Clamp((DateTime.Now - wakeStart).TotalSeconds / Math.Max(1, wakeDuration.TotalSeconds), 0, 1);
					Color col = SunriseColor(t, o.TargetKelvin);
					if (t >= 0.8 && ble.Supports(4))
					{
						temp = Protocol.Temperature(2700 + (o.TargetKelvin - 2700) * ((t - 0.8) / 0.2));
					}
					else if (ble.Supports(5))
					{
						value = Protocol.Rgb(col.R, col.G, col.B);
					}
					bri = 1 + (o.TargetBrightness - 1) * Math.Pow(t, 1.7);
					briThreshold = 1;
					interval = 1500;
					SetLiveColor(col, bri, 1200);
					LiveText.Text = L.F("wake_progress", L.Pct(bri ?? 0));
					if (t >= 1) finished = true;
				}
				else
				{
					AudioOptions o = settings.Audio;
					interval = (int)Math.Clamp(o.IntervalMs, 100, 600);
					float peak = 0f;
					for (int i = 0; i < 4; i++)
					{
						peak = Math.Max(peak, audio!.Peak());
						await Task.Delay(25, cts.Token);
					}
					if ((double)peak > 0.0005)
					{
						silent.Restart();
					}
					double min = Math.Min(o.MinBrightness, o.MaxBrightness - 1);
					double target = AmbientInput.AudioLevel(peak, o.Sensitivity, min, o.MaxBrightness);
					level += (target - level) * ((target > level) ? o.Attack / 100.0 : o.Release / 100.0);
					bri = level;
					briThreshold = 2;
					LiveText.Text = ((silent.Elapsed.TotalSeconds > 4.0) ? L.T("no_sound") : L.F("live_audio", L.Pct(level)));
				}

				await lightGate.WaitAsync(cts.Token);
				try
				{
					if (value != null && (last == null || !value.SequenceEqual(last)))
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4L));
						await ble.Write(5, value, timeout.Token);
						last = value;
						StateText.Text = L.F("mode_live", L.Mode(mode));
					}
					if (temp != null && (lastTemp == null || !temp.SequenceEqual(lastTemp)))
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4L));
						await ble.Write(4, temp, timeout.Token);
						lastTemp = temp;
						StateText.Text = L.F("mode_live", L.Mode(mode));
					}
					if (bri.HasValue && ble.Supports(3))
					{
						byte[] bv = Protocol.Brightness(bri.Value);
						if (lastBri == null || Math.Abs(bv[0] - lastBri[0]) >= briThreshold)
						{
							using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
							timeout.CancelAfter(TimeSpan.FromSeconds(4L));
							await ble.Write(3, bv, timeout.Token);
							lastBri = bv;
							if (mode == "Ses")
							{
								SetSilently(BrightnessSlider, level);
								StateText.Text = L.F("audio_level", L.Pct(level));
							}
							else if (mode is "Uyku" or "Uyandırma" or "Gün ışığı")
							{
								SetSilently(BrightnessSlider, Math.Clamp(Math.Round(bri.Value), 1, 100));
							}
						}
					}
					if (powerOff)
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4L));
						await ble.Write(2, Protocol.Power(on: false), timeout.Token);
						powerOn = false;
					}
				}
				finally
				{
					lightGate.Release();
				}
				if (powerOff)
				{
					settings.SleepUntil = null;
					sleepFadeStarted = false;
					SaveSettings();
					DetailText.Text = L.T("good_night");
					if (!IsVisible) tray?.Notify(L.T("sleep_timer"), L.T("light_turned_off"));
					break;
				}
				if (finished)
				{
					DetailText.Text = L.T("good_morning");
					break;
				}
				await Task.Delay(Math.Max(15, interval - (int)frame.ElapsedMilliseconds), cts.Token);
			}
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			Log(ex2.ToString());
			if (!closed)
			{
				DetailText.Text = L.F("mode_stopped_reason", Friendly(ex2));
				LiveText.Text = DetailText.Text;
			}
		}
		finally
		{
			if (mode == "Uyku" && cts.IsCancellationRequested && settings.SleepUntil.HasValue && !closed)
			{
				// Uyku solması elle kesildi: sayaç da iptal.
				settings.SleepUntil = null;
				sleepFadeStarted = false;
				SaveSettings();
			}
			modeCancellation = null;
			modeName = "";
			liveBrightness = null;
			liveLast.Clear();
			if (!closed)
			{
				if (!busy)
				{
					StatusText.Text = (ble.Ready ? L.F("connected_to", connectedName) : L.T("disconnected"));
				}
				StateText.Text = L.T("last_color_kept");
				if (cts.IsCancellationRequested)
				{
					LiveText.Text = "";
					DetailText.Text = L.T("mode_stopped");
				}
				UpdateBulbVisual(false, 300);
				UpdateControls();
			}
			cts.Dispose();
		}
	}

	internal static Color RainbowColor(double seconds)
	{
		return RainbowEngine.ColorAt(RainbowEngine.Position(seconds, 24.0, RainbowDirection.TopToBottom), 0, 0.86);
	}

	// =====================================================================
	//  Tema ve cam efekti
	// =====================================================================

	internal static void SaveAppearance(string path, bool dark)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Appearance(dark)));
		File.Move(path + ".tmp", path, overwrite: true);
	}

	private bool ReadTheme()
	{
		try
		{
			return !File.Exists(ThemePath) || (JsonSerializer.Deserialize<Appearance>(File.ReadAllText(ThemePath))?.Dark ?? true);
		}
		catch (Exception ex)
		{
			Log("Tema okunamadı: " + ex.Message);
			return true;
		}
	}

	private void Theme_Click(object sender, RoutedEventArgs e)
	{
		ApplyTheme(!darkTheme);
		if (persistent)
		{
			try
			{
				SaveAppearance(ThemePath, darkTheme);
			}
			catch (Exception ex)
			{
				Log(ex.ToString());
				DetailText.Text = L.T("theme_save_failed");
			}
		}
	}

	private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

	private static void SetResource(string key, object value)
	{
		if (value is Freezable f && f.CanFreeze) f.Freeze();
		Application.Current.Resources[key] = value;
	}

	internal void ApplyTheme(bool dark)
	{
		darkTheme = dark;
		foreach (KeyValuePair<string, string> item in new Dictionary<string, string>
		{
			["Text"] = dark ? "#F5F7FF" : "#1C2333",
			["Muted"] = dark ? "#AEB8D0" : "#5A6478",
			["Glass"] = dark ? "#1FFFFFFF" : "#8CFFFFFF",
			["GlassStrong"] = dark ? "#2EFFFFFF" : "#CCFFFFFF",
			["Stroke"] = dark ? "#33FFFFFF" : "#24000000",
			["Control"] = dark ? "#1FFFFFFF" : "#80FFFFFF",
			["Hover"] = dark ? "#2BFFFFFF" : "#59FFFFFF",
			["Accent"] = dark ? "#0A84FF" : "#007AFF",
			["AccentText"] = "#FFFFFF",
			["AccentSoft"] = dark ? "#7CC2FF" : "#0062CC",
			["Track"] = dark ? "#38FFFFFF" : "#29000000",
			["Thumb"] = "#FFFFFF",
			["ModeText"] = dark ? "#9EF0D8" : "#1E6B5C",
			["SwitchOn"] = "#34C759",
			["PopupBg"] = dark ? "#F21C2333" : "#F7F6F8FC",
		})
		{
			SetResource(item.Key, new SolidColorBrush(C(item.Value)));
		}
		SetResource("GlassEdge", new LinearGradientBrush(new GradientStopCollection
		{
			new GradientStop(C(dark ? "#66FFFFFF" : "#FFFFFFFF"), 0),
			new GradientStop(C(dark ? "#14FFFFFF" : "#66FFFFFF"), 0.5),
			new GradientStop(C(dark ? "#26FFFFFF" : "#99FFFFFF"), 1),
		}, new Point(0, 0), new Point(0, 1)));
		SetResource("GlassShine", new LinearGradientBrush(new GradientStopCollection
		{
			new GradientStop(C(dark ? "#22FFFFFF" : "#66FFFFFF"), 0),
			new GradientStop(C("#00FFFFFF"), 0.45),
		}, new Point(0, 0), new Point(0, 1)));
		SetResource("Backdrop", new LinearGradientBrush(new GradientStopCollection
		{
			new GradientStop(C(dark ? "#0E1322" : "#E6ECF8"), 0),
			new GradientStop(C(dark ? "#171D30" : "#F1F0FA"), 0.55),
			new GradientStop(C(dark ? "#21182B" : "#F8ECF3"), 1),
		}, new Point(0, 0), new Point(1, 1)));
		UpdateBackdropTint();
		BlobA.Fill = new RadialGradientBrush(C(dark ? "#B35E5CE6" : "#998FB3FF"), C(dark ? "#005E5CE6" : "#008FB3FF"));
		BlobB.Fill = new RadialGradientBrush(C(dark ? "#99FF6B9A" : "#80FFC3A0"), C(dark ? "#00FF6B9A" : "#00FFC3A0"));
		ThemeButton.Content = dark ? L.T("theme_light") : L.T("theme_dark");
		ThemeButton.ToolTip = L.T("theme_tip");
		ApplyTitleTheme();
		tray?.SetTheme(dark);
		UpdateBulbVisual(false);
	}

	private void UpdateBackdropTint()
	{
		if (glassActive)
		{
			SetResource("BackdropTint", new LinearGradientBrush(new GradientStopCollection
			{
				new GradientStop(C(darkTheme ? "#B30E1322" : "#99F2F5FB"), 0),
				new GradientStop(C(darkTheme ? "#80171D30" : "#66F4F0FA"), 1),
			}, new Point(0, 0), new Point(1, 1)));
		}
		else
		{
			SetResource("BackdropTint", new SolidColorBrush(Colors.Transparent));
		}
	}

	private void ApplyTitleTheme()
	{
		GlassBackdrop.SetDarkTitle(this, darkTheme);
	}

	private void ApplyGlass()
	{
		bool want = settings.GlassEnabled && GlassBackdrop.Supported;
		if (want)
		{
			glassActive = GlassBackdrop.Apply(this, true);
		}
		else if (glassActive)
		{
			GlassBackdrop.Apply(this, false);
			glassActive = false;
		}
		if (glassActive)
		{
			Background = Brushes.Transparent;
		}
		else
		{
			SetResourceReference(BackgroundProperty, "Backdrop");
		}
		UpdateBackdropTint();
		if (!GlassBackdrop.Supported)
		{
			GlassButton.Content = L.T("glass_win11");
			GlassButton.IsEnabled = false;
			GlassButton.ToolTip = L.T("glass_needs_win11");
		}
		else
		{
			GlassButton.Content = glassActive ? L.T("glass_on") : L.T("glass_off");
			GlassButton.ToolTip = L.T("glass_tip");
		}
	}

	private void Glass_Click(object sender, RoutedEventArgs e)
	{
		settings.GlassEnabled = !settings.GlassEnabled;
		ApplyGlass();
		SaveSettings();
	}

	// =====================================================================
	//  Sistem tepsisi
	// =====================================================================

	private void EnableTray(bool visible)
	{
		tray = new TrayController(RestoreWindow, ExitApplication, () =>
		{
			Connect_Click(this, new RoutedEventArgs());
		}, async (int code, byte[] value) =>
		{
			await Send(code, value);
		}, visible, async (string mode) =>
		{
			if (mode == "Dur")
			{
				await StopModeAsync();
			}
			else
			{
				await StartMode(mode);
			}
		}, async () =>
		{
			await SetPower(!(powerOn ?? false));
		}, async (Color c) =>
		{
			await ApplyColor(c);
		}, () =>
		{
			RestoreWindow();
			if (!IsExpanded("colors"))
			{
				SetSectionState("colors", true, animate: true);
			}
		}, persistent ? AutoStart.IsEnabled : null, persistent ? SetAutoStart : null);
		tray.Extend(ToggleQuickPanel, ShowMini, m => StartSleepTimer(m), id => _ = ApplySceneById(id));
		tray.SetScenes(Scenes);
		tray.SetTheme(darkTheme);
		UpdateControls();
	}

	/// <summary>Windows açılışında (--tray): pencere gizli, son ampule yine bağlanır.</summary>
	internal void InitAutoStart()
	{
		if (persistent)
		{
			// 3.3: eski "HueStudio" / "Sati" başlangıç kaydı "Chiaro" adına taşınır.
			if (AutoStart.MigrateLegacy()) Log("Başlangıç kaydı Chiaro adına taşındı.");
			SyncAutoStart();
		}
	}

	internal void StartInTray()
	{
		hideTipShown = true;
		autostartLaunch = true;
		StartupConnect(this, EventArgs.Empty);
	}

	private void SyncAutoStart()
	{
		if (!AutoStart.Apply(settings.AutoStart))
		{
			Log("Başlangıç kaydı yazılamadı.");
		}
		AutoStartBox.IsChecked = AutoStart.IsEnabled();
		UpdateLightOnStartupBox();
	}

	/// <summary>"Bilgisayar açılınca ışığı aç": Windows ile başlat kapalıyken görünür ama soluk; ipucu neden gerektiğini söyler.</summary>
	private void UpdateLightOnStartupBox()
	{
		bool startup = AutoStartBox.IsChecked == true;
		LightOnStartupBox.IsChecked = settings.LightOnStartup;
		LightOnStartupBox.IsEnabled = startup;
		LightOnStartupBox.ToolTip = L.T(startup ? "light_on_startup_tip" : "light_on_startup_needs");
	}

	private void LightOnStartup_Click(object sender, RoutedEventArgs e)
	{
		if (AutoStartBox.IsChecked != true)
		{
			// Önce "Windows ile başlat" gerekir; diğer ayarlar sessizce değiştirilmez.
			DetailText.Text = L.T("light_on_startup_needs");
			UpdateLightOnStartupBox();
			return;
		}
		settings.LightOnStartup = LightOnStartupBox.IsChecked == true;
		SaveSettings();
		Log("Bilgisayar açılınca ışığı aç: " + (settings.LightOnStartup ? "açık" : "kapalı"));
		UpdateLightOnStartupBox();
	}

	private void SetAutoStart(bool enabled)
	{
		settings.AutoStart = enabled;
		SaveSettings();
		SyncAutoStart();
	}

	private void AutoStart_Click(object sender, RoutedEventArgs e)
	{
		SetAutoStart(AutoStartBox.IsChecked == true);
	}

	internal void RestoreWindow()
	{
		Show();
		WindowState = WindowState.Normal;
		Activate();
	}

	private void HideToTray()
	{
		if (tray != null)
		{
			Hide();
			SaveSettings();
			if (!hideTipShown)
			{
				tray.Notify(L.T("tray_bg_title"), L.T("tray_bg_text"));
				hideTipShown = true;
			}
		}
	}

	private void Hide_Click(object sender, RoutedEventArgs e)
	{
		HideToTray();
	}

	internal void ExitApplication()
	{
		exitRequested = true;
		Close();
		Application.Current.Shutdown();
	}

	internal void PrepareForShutdown()
	{
		exitRequested = true;
	}

	/// <summary>Gizli test kancası: ayarlanırsa günlük satırları bu dosyaya da yazılır (ör. Wine'da açılış benzetimi).</summary>
	private static readonly string? LogFile = Environment.GetEnvironmentVariable("CHIARO_LOG_FILE");

	private void Log(string message)
	{
		lock (logs)
		{
			logs.Add($"{DateTimeOffset.Now:O} {message}");
			if (!string.IsNullOrWhiteSpace(LogFile))
			{
				try { File.AppendAllText(LogFile, $"{DateTimeOffset.Now:O} {message}\n"); } catch (Exception) { }
			}
			if (logs.Count > 1000)
			{
				logs.RemoveAt(0);
			}
		}
	}

	// =====================================================================
	//  Kontrol durumu
	// =====================================================================

	private void UpdateControls()
	{
		if (!initialized || ble == null) return;
		ScanButton.IsEnabled = !busy;
		ForgetButton.IsEnabled = !busy;
		Devices.IsEnabled = !busy && modeCancellation == null;
		ConnectButton.IsEnabled = !busy && Devices.SelectedItem is Lamp;
		DisconnectButton.IsEnabled = !busy && ble.Ready;
		RefreshButton.IsEnabled = !busy && ble.Ready;
		PowerToggle.IsEnabled = !busy && ble.Supports(2);
		DockPower.IsEnabled = PowerToggle.IsEnabled;
		BrightnessPanel.IsEnabled = !busy && ble.Supports(3);
		TemperaturePanel.IsEnabled = !busy && ble.Supports(4);

		ModeBadge.Text = modeName.Length == 0 ? L.T("manual_upper") : L.Upper(L.Mode(modeName)) + " • " + L.T("live_upper");
		TabManualText.Text = (modeName.Length == 0 ? "● " : "") + L.Mode("Manuel");
		TabScreenText.Text = (modeName == "Ekran" ? "● " : "") + L.Mode("Ekran");
		TabAudioText.Text = (modeName == "Ses" ? "● " : "") + L.Mode("Ses");
		TabRainbowText.Text = (modeName == "Gökkuşağı" ? "● " : "") + L.Mode("Gökkuşağı");
		TabFireText.Text = (modeName == "Şömine" ? "● " : "") + L.Mode("Şömine");
		TabSpotifyText.Text = modeName == "Spotify" ? "● Spotify" : "Spotify";
		TabDaylightText.Text = (modeName == "Gün ışığı" ? "● " : "") + L.Mode("Gün ışığı");

		if (selectedTab == "Manuel")
		{
			ModeStartButton.Content = L.T("back_to_manual");
			ModeStartButton.IsEnabled = modeCancellation != null;
		}
		else if (modeName == selectedTab)
		{
			ModeStartButton.Content = L.T("stop");
			ModeStartButton.IsEnabled = true;
		}
		else
		{
			ModeStartButton.Content = L.T("start");
			ModeStartButton.IsEnabled = !busy && ble.Ready && ModeSupported(selectedTab);
		}
		SetHint(FireNote, ble.Ready && !ble.Supports(3) ? L.T("fire_color_only") : "");

		CancelButton.Visibility = ((!busy) ? Visibility.Collapsed : Visibility.Visible);
		if (busy)
		{
			ConnDot.Fill = new SolidColorBrush(C("#FF9F0A"));
			ConnText.Text = L.T("busy");
		}
		else if (ble.Ready)
		{
			ConnDot.Fill = new SolidColorBrush(C("#30D158"));
			ConnText.Text = L.F("connected_to", connectedName);
		}
		else
		{
			ConnDot.Fill = new SolidColorBrush(C("#8E8E93"));
			ConnText.Text = L.T("not_connected");
		}
		DockDetail.Text = DockSummary();
		tray?.Update(ble.Ready, busy, Devices.SelectedItem is Lamp, ble.Supports(3), ble.Supports(4), ble.Supports(5), ble.Ready ? (connectedName + " • " + L.T("connected")) : L.T("not_connected"), modeCancellation != null, modeName, ble.Ready ? powerOn : null, colorKnown ? currentColor : null, BrightnessSlider.Value);
		RaiseLightState();
	}

	/// <returns>İşlem hatasız tamamlandıysa true.</returns>
	private async Task<bool> Run(string title, Func<CancellationToken, Task> action, int seconds = 35)
	{
		if (busy || closed)
		{
			return false;
		}
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
			if (!IsVisible && !closed && !autoConnecting)
			{
				tray?.Notify(L.T("op_stopped"), DetailText.Text);
			}
			return false;
		}
		catch (Exception ex2)
		{
			lastRunFailed = true;
			Log(ex2.ToString());
			if (!closed)
			{
				StatusText.Text = L.T("error");
				DetailText.Text = Friendly(ex2);
			}
			if (!IsVisible && !closed && !autoConnecting)
			{
				tray?.Notify(L.T("op_failed"), Friendly(ex2));
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
				UpdateBulbVisual(false, 250);
				UpdateControls();
			}
		}
	}

	private static string Friendly(Exception ex)
	{
		if (!(ex is UnauthorizedAccessException))
		{
			if (!(ex is InvalidOperationException))
			{
				return L.F("bt_error", ex.HResult);
			}
			return L.Message(ex.Message);
		}
		return L.T("bt_denied");
	}

	// =====================================================================
	//  Tarama / bağlantı
	// =====================================================================

	private async void Scan_Click(object sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		await ScanForTest();
	}

	internal async Task ScanForTest()
	{
		await Run(L.T("searching"), async (CancellationToken ct) =>
		{
			ble.Disconnect();
			powerOn = null;
			StateText.Text = L.T("not_connected_dot");
			lamps.Clear();
			ScanSummary.Text = L.T("scanning");
			DetailText.Text = L.T("scanning_detail");
			int generation = ++scanGeneration;
			try
			{
				await ble.ScanAsync((Lamp lamp) =>
				{
					Dispatcher.BeginInvoke((Action)(() =>
					{
						if (!closed && generation == scanGeneration)
						{
							int num = lamps.ToList().FindIndex((Lamp x) => x.Key == lamp.Key);
							if (num >= 0)
							{
								Lamp lamp2 = lamps[num];
								Lamp lamp3 = lamp with
								{
									DeviceId = (lamp.DeviceId ?? lamp2.DeviceId),
									AddressType = ((lamp.AddressType == BluetoothAddressType.Unspecified) ? lamp2.AddressType : lamp.AddressType),
									Paired = (lamp.Paired || lamp2.Paired),
									Rssi = (lamp.Rssi ?? lamp2.Rssi),
									Name = ((lamp.Name == "Hue adayı") ? lamp2.Name : lamp.Name)
								};
								bool flag = ReferenceEquals(Devices.SelectedItem, lamp2);
								lamps[num] = lamp3;
								if (flag)
								{
									Devices.SelectedItem = lamp3;
								}
							}
							else
							{
								lamps.Add(lamp);
								if (Devices.SelectedIndex < 0)
								{
									Devices.SelectedIndex = 0;
								}
							}
						}
					}));
				}, ct);
				StatusText.Text = (ble.Ready ? L.F("connected_to", connectedName) : ((lamps.Count > 0) ? L.T("pick_bulb") : L.T("no_bulb_found")));
				DetailText.Text = ((lamps.Count > 0) ? L.T("pick_and_connect") : L.T("no_bulb_hint"));
			}
			finally
			{
				scanGeneration++;
				ScanSummary.Text = L.F("bulbs_found", lamps.Count);
			}
		}, 25);
	}

	private async void Connect_Click(object sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		if (Devices.SelectedItem is Lamp lamp)
		{
			await ConnectSelected(lamp);
		}
	}

	private async Task ConnectSelected(Lamp lamp)
	{
		await Run(L.T("connecting"), async (CancellationToken ct) =>
		{
			await ble.ConnectAsync(lamp, (string text) =>
			{
				DetailText.Text = text;
			}, ct);
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

	private async Task RefreshState(CancellationToken ct)
	{
		byte[]? power = await ble.Read(2, ct);
		if (power == null || power.Length != 1 || power[0] > 1)
		{
			throw new InvalidOperationException(L.T("power_unreadable"));
		}
		powerOn = power[0] == 1;
		byte[]? brightness = await ble.ReadOptional(3, ct);
		byte[]? data = await ble.ReadOptional(4, ct);
		string text = ((power[0] == 1) ? L.T("on") : L.T("off"));
		if (brightness != null && brightness.Length == 1)
		{
			byte b = brightness[0];
			if (b >= 1 && b <= 254)
			{
				double num = Math.Round((double)(int)brightness[0] * 100.0 / 254.0);
				SetSilently(BrightnessSlider, Math.Max(1.0, num));
				text += " • " + L.Pct(num);
			}
		}
		int? num2 = Protocol.Kelvin(data);
		if (num2.HasValue)
		{
			int valueOrDefault = num2.GetValueOrDefault();
			SetSilently(TemperatureSlider, Math.Clamp(valueOrDefault, 2200, 6500));
			text += $" • {valueOrDefault} K";
		}
		StateText.Text = text + "";
		UpdateBulbVisual(true);
	}

	private async Task<bool> Send(int code, byte[] value)
	{
		return await Run(L.T("sending"), async (CancellationToken ct) =>
		{
			await ble.Write(code, value, ct);
			if (code == 2)
			{
				powerOn = value[0] == 1;
			}
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("ready");
			if (!IsVisible)
			{
				tray?.Notify("Chiaro", (code != 2) ? L.T("applied") : ((value[0] == 1) ? L.T("light_on") : L.T("light_off")));
			}
			try
			{
				await RefreshState(ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex2)
			{
				Log(ex2.Message);
				DetailText.Text = L.T("applied_unread");
				StateText.Text = L.T("state_unknown");
			}
		});
	}

	// =====================================================================
	//  Güç (tek anahtar)
	// =====================================================================

	private async Task SetPower(bool on)
	{
		if (!ble.Ready || !ble.Supports(2))
		{
			DetailText.Text = L.T("connect_first");
			UpdateBulbVisual(true);
			return;
		}
		if (busy)
		{
			UpdateBulbVisual(true);
			return;
		}
		bool? before = powerOn;
		powerOn = on;
		UpdateBulbVisual(true);
		bool ok = await Send(2, Protocol.Power(on));
		if (!ok && ble.Ready)
		{
			powerOn = before;
		}
		UpdateBulbVisual(true);
		UpdateControls();
	}

	private async void PowerToggle_Click(object sender, RoutedEventArgs e)
	{
		await SetPower(PowerToggle.IsChecked == true);
	}

	private async void PowerToggleQuick_Click(object sender, RoutedEventArgs e)
	{
		await SetPower(!(powerOn ?? false));
	}

	// =====================================================================
	//  Parlaklık / beyaz tonu
	// =====================================================================

	private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (BrightnessLabel != null)
		{
			BrightnessLabel.Text = L.Pct(e.NewValue);
		}
		if (initialized && modeName.Length == 0)
		{
			UpdateBulbVisual(false, 120);
		}
		if (initialized && silentSliders == 0 && BrightnessPanel.IsEnabled)
		{
			LiveBrightness(e.NewValue, final: false);
		}
	}

	private void Temperature_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (TemperatureLabel != null)
		{
			TemperatureLabel.Text = $"{e.NewValue:F0} K";
		}
		if (initialized && silentSliders == 0 && TemperaturePanel.IsEnabled)
		{
			TemperatureLive(final: false);
		}
	}

	// =====================================================================
	//  Renkler: palet, favoriler, son kullanılanlar, tekerlek
	// =====================================================================

	private Button MakeSwatch(Color c, string name, double size, bool favorite)
	{
		Button b = new Button
		{
			Style = (Style)FindResource("Swatch"),
			Background = new SolidColorBrush(c),
			Width = size,
			Height = size,
			ToolTip = favorite
				? L.F("fav_tip_remove", name, ColorMath.Hex(c))
				: L.F("fav_tip_add", name, ColorMath.Hex(c)),
			Tag = c,
		};
		System.Windows.Automation.AutomationProperties.SetName(b, name);
		b.Click += async (_, _) => await ApplyColor(c);
		b.MouseRightButtonUp += (_, ev) =>
		{
			ev.Handled = true;
			if (favorite) RemoveFavorite(c);
			else AddFavorite(c);
		};
		return b;
	}

	private void BuildPalette()
	{
		ColorPanel.Children.Clear();
		foreach (IGrouping<string, NamedColor> group in Palette.All.GroupBy(n => n.Group))
		{
			ColorPanel.Children.Add(new TextBlock
			{
				Text = L.Upper(L.Color(group.Key)),
				Style = (Style)FindResource("SectionLabel"),
				Margin = new Thickness(0, 6, 0, 8),
			});
			WrapPanel wrap = new WrapPanel();
			foreach (NamedColor n in group)
			{
				wrap.Children.Add(MakeSwatch(n.Color, L.Color(n.Name), 34, favorite: false));
			}
			ColorPanel.Children.Add(wrap);
		}
	}

	private void RebuildQuickSwatches()
	{
		QuickSwatches.Children.Clear();
		foreach (string hex in settings.Favorites)
		{
			if (ColorMath.TryParseHex(hex, out Color c))
			{
				Button b = MakeSwatch(c, Palette.NameOf(c), 26, favorite: true);
				b.Margin = new Thickness(0, 3, 8, 3);
				QuickSwatches.Children.Add(b);
			}
		}
		if (QuickSwatches.Children.Count == 0)
		{
			TextBlock t = new TextBlock { Text = L.T("fav_empty_hint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
			t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
			QuickSwatches.Children.Add(t);
		}
	}

	private void RebuildRecent()
	{
		RecentPanel.Children.Clear();
		foreach (string hex in settings.Recent)
		{
			if (ColorMath.TryParseHex(hex, out Color c))
			{
				RecentPanel.Children.Add(MakeSwatch(c, Palette.NameOf(c), 30, favorite: false));
			}
		}
		RecentEmpty.Visibility = RecentPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void AddFavorite(Color c)
	{
		string hex = ColorMath.Hex(c);
		settings.Favorites.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
		settings.Favorites.Insert(0, hex);
		if (settings.Favorites.Count > 24) settings.Favorites.RemoveRange(24, settings.Favorites.Count - 24);
		RebuildQuickSwatches();
		SaveSettings();
		DetailText.Text = L.F("fav_added", Palette.NameOf(c));
	}

	private void RemoveFavorite(Color c)
	{
		string hex = ColorMath.Hex(c);
		settings.Favorites.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
		RebuildQuickSwatches();
		SaveSettings();
		DetailText.Text = L.F("removed", Palette.NameOf(c));
	}

	private void AddRecent(Color c)
	{
		string hex = ColorMath.Hex(c);
		settings.Recent.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
		settings.Recent.Insert(0, hex);
		if (settings.Recent.Count > 12) settings.Recent.RemoveRange(12, settings.Recent.Count - 12);
		RebuildRecent();
	}

	private async Task ApplyColor(Color c)
	{
		SetCustomFromColor(c, updateHex: true);
		if (!ble.Ready || !ble.Supports(5))
		{
			DetailText.Text = ble.Ready ? L.T("no_color_on_bulb") : L.T("connect_to_send");
			return;
		}
		activeSceneId = null;
		if (await Send(5, Protocol.Rgb(c.R, c.G, c.B)))
		{
			currentColor = c;
			colorKnown = true;
			lastWasWhite = false;
			liveLast.Clear();
			AddRecent(c);
			UpdateBulbVisual(true);
			UpdateControls();
		}
	}

	private void BuildWheel()
	{
		const int px = 352;
		WriteableBitmap bmp = new WriteableBitmap(px, px, 192, 192, PixelFormats.Bgra32, null);
		byte[] pixels = new byte[px * px * 4];
		double center = px / 2.0;
		double radius = center - 1.0;
		for (int y = 0; y < px; y++)
		{
			for (int x = 0; x < px; x++)
			{
				double dx = x + 0.5 - center;
				double dy = y + 0.5 - center;
				double r = Math.Sqrt(dx * dx + dy * dy);
				int o = (y * px + x) * 4;
				double a = Math.Clamp(radius + 0.5 - r, 0, 1);
				if (a <= 0) continue;
				double hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
				double sat = Math.Min(1.0, r / radius);
				Color c = ColorMath.FromHsv(hue, sat, 1.0);
				pixels[o] = c.B;
				pixels[o + 1] = c.G;
				pixels[o + 2] = c.R;
				pixels[o + 3] = (byte)(a * 255);
			}
		}
		bmp.WritePixels(new Int32Rect(0, 0, px, px), pixels, px * 4, 0);
		bmp.Freeze();
		Wheel.Source = bmp;
	}

	private void PlaceWheelMarker()
	{
		double rad = customHue * Math.PI / 180.0;
		double half = WheelDip / 2.0;
		double x = half + Math.Cos(rad) * customSat * (half - 2);
		double y = half + Math.Sin(rad) * customSat * (half - 2);
		Canvas.SetLeft(WheelMarker, x - WheelMarker.Width / 2);
		Canvas.SetTop(WheelMarker, y - WheelMarker.Height / 2);
		WheelMarker.Fill = new SolidColorBrush(customColor);
	}

	private void SetCustomFromColor(Color c, bool updateHex)
	{
		updatingCustom = true;
		try
		{
			(double h, double s, double _) = ColorMath.ToHsv(c);
			customHue = h;
			customSat = s;
			customColor = c;
			SatSlider.Value = Math.Round(s * 100);
			SatLabel.Text = L.Pct(s * 100);
			CustomPreview.Background = new SolidColorBrush(c);
			if (updateHex) HexBox.Text = ColorMath.Hex(c);
			PlaceWheelMarker();
		}
		finally
		{
			updatingCustom = false;
		}
	}

	private void SetCustomFromHueSat(double hue, double sat)
	{
		customHue = hue;
		customSat = Math.Clamp(sat, 0, 1);
		customColor = ColorMath.FromHsv(customHue, customSat, 1.0);
		updatingCustom = true;
		try
		{
			SatSlider.Value = Math.Round(customSat * 100);
			SatLabel.Text = L.Pct(customSat * 100);
			CustomPreview.Background = new SolidColorBrush(customColor);
			HexBox.Text = ColorMath.Hex(customColor);
			PlaceWheelMarker();
		}
		finally
		{
			updatingCustom = false;
		}
	}

	private void PickFromWheel(Point p)
	{
		double half = WheelDip / 2.0;
		double dx = p.X - half;
		double dy = p.Y - half;
		double hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
		double sat = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy) / (half - 2));
		SetCustomFromHueSat(hue, sat);
	}

	private void Wheel_MouseDown(object sender, MouseButtonEventArgs e)
	{
		wheelDragging = true;
		Wheel.CaptureMouse();
		PickFromWheel(e.GetPosition(Wheel));
		e.Handled = true;
	}

	private void Wheel_MouseMove(object sender, MouseEventArgs e)
	{
		if (wheelDragging && e.LeftButton == MouseButtonState.Pressed)
		{
			PickFromWheel(e.GetPosition(Wheel));
			if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: false);
		}
	}

	private async void Wheel_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!wheelDragging) return;
		wheelDragging = false;
		Wheel.ReleaseMouseCapture();
		if (ble.Ready && ble.Supports(5))
		{
			LiveColorSet(customColor, final: true);
		}
		await Task.CompletedTask;
	}

	private void SatSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!initialized || updatingCustom) return;
		SetCustomFromHueSat(customHue, e.NewValue / 100.0);
		if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: false);
	}

	private void HexBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			CommitHex();
			e.Handled = true;
			if (ble.Ready && ble.Supports(5)) _ = ApplyColor(customColor);
		}
	}

	private void HexBox_LostFocus(object sender, RoutedEventArgs e) => CommitHex();

	private void CommitHex()
	{
		if (ColorMath.TryParseHex(HexBox.Text, out Color c))
		{
			if (c != customColor) SetCustomFromColor(c, updateHex: true);
		}
		else
		{
			HexBox.Text = ColorMath.Hex(customColor);
		}
	}

	private void AddFavorite_Click(object sender, RoutedEventArgs e)
	{
		CommitHex();
		AddFavorite(customColor);
	}

	// =====================================================================
	//  Diğer
	// =====================================================================

	private async void Refresh_Click(object sender, RoutedEventArgs e)
	{
		await Run(L.T("reading_state"), async (CancellationToken ct) =>
		{
			await RefreshState(ct);
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("ready");
		});
	}

	private void Selection_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (ble != null)
		{
			if (!busy && ble.Ready && (Devices.SelectedItem as Lamp)?.Key != connectedKey)
			{
				ble.Disconnect();
				powerOn = null;
				StatusText.Text = L.T("new_bulb_selected");
				DetailText.Text = L.T("press_connect");
				StateText.Text = L.T("state_unknown");
				UpdateBulbVisual(true);
			}
			UpdateControls();
		}
	}

	private async void Disconnect_Click(object sender, RoutedEventArgs e)
	{
		await Run(L.T("disconnecting"), async (CancellationToken ct) =>
		{
			autoCancelled = true;
			ble.Disconnect();
			powerOn = null;
			StatusText.Text = L.T("disconnected");
			DetailText.Text = L.T("pairing_kept");
			StateText.Text = L.T("state_unknown");
			await Task.CompletedTask;
		});
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		autoCancelled = true;
		operation?.Cancel();
		modeCancellation?.Cancel();
	}

	private void Help_Click(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(this, L.T("help_text"), L.T("help_title"), MessageBoxButton.OK, MessageBoxImage.Asterisk);
	}

	private void Settings_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("ms-settings:bluetooth")
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Log(ex.ToString());
			MessageBox.Show(this, L.T("open_bt_settings_manual"));
		}
	}

	internal string Report()
	{
		lock (logs)
		{
			string lampLine = (Devices.SelectedItem is Lamp sel ? L.F("report_lamp", sel.Display, sel.Tail) + (ble.Ready ? L.T("report_connected") : "") : L.T("report_no_lamp")) + "\n";
			return L.F("report_header", "Chiaro " + Updater.CurrentText) + "\n" + lampLine + "\n" + string.Join(Environment.NewLine, logs);
		}
	}

	private void Report_Click(object sender, RoutedEventArgs e)
	{
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			FileName = L.T("report_file"),
			Filter = L.T("report_filter")
		};
		if (saveFileDialog.ShowDialog(this) == true)
		{
			try
			{
				File.WriteAllText(saveFileDialog.FileName, Report());
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, L.F("report_save_failed", ex.Message));
			}
		}
	}

	// =====================================================================
	//  Duman testi (--smoke-test) yardımcıları — gerçek ampule komut göndermez
	// =====================================================================

	internal void AssertOfflineState()
	{
		if (busy || !ScanButton.IsEnabled || PowerToggle.IsEnabled || BrightnessPanel.IsEnabled || TemperaturePanel.IsEnabled)
		{
			throw new InvalidOperationException("Smoke test: offline controls are not in the expected state.");
		}
	}

	internal async Task TestModeHandoff()
	{
		using CancellationTokenSource cancel = new CancellationTokenSource();
		TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		modeCancellation = cancel;
		modeName = "Ekran";
		modeTask = tcs.Task;
		ShowTab("Ekran", animate: false);
		if (!ModeStartButton.IsEnabled || !ModeBadge.Text.Contains("CANLI"))
		{
			throw new InvalidOperationException("Active mode incorrectly appears as loading");
		}
		bool ran = false;
		Task<bool> task = Run("Test handoff", (CancellationToken ct) =>
		{
			ran = true;
			return Task.CompletedTask;
		});
		if (!cancel.IsCancellationRequested | ran)
		{
			throw new InvalidOperationException("Manual command did not wait for old mode");
		}
		modeCancellation = null;
		modeName = "";
		tcs.SetResult();
		await task;
		modeTask = Task.CompletedTask;
		if (!ran || busy)
		{
			throw new InvalidOperationException("Mode handoff failed");
		}
		ShowTab("Manuel", animate: false);
		StatusText.Text = L.T("status_connect_bulb");
		UpdateControls();
	}

	internal void ShowTabForTest(string tab) => ShowTab(tab, animate: false);

	internal void SimulateGlow(Color c, bool on)
	{
		currentColor = c;
		colorKnown = true;
		powerOn = on;
		UpdateBulbVisual(false);
		UpdateControls();
	}

	internal void SetSectionsForTest(bool bulb, bool modes, bool colors)
	{
		SetSectionState("bulb", bulb, animate: false);
		SetSectionState("modes", modes, animate: false);
		SetSectionState("colors", colors, animate: false);
	}

	internal void AddLampsForTest()
	{
		lamps.Add(new Lamp(0x0A0B0C0D4F2AuL, BluetoothAddressType.Random, "Hue Play", null, Paired: true, (short)(-41)));
		lamps.Add(new Lamp(0x0A0B0C0D9E71uL, BluetoothAddressType.Random, "Hue Play", null, Paired: false, (short)(-63)));
		Devices.SelectedIndex = 0;
	}

	internal void TestTrayLifecycle()
	{
		EnableTray(visible: false);
		hideTipShown = true;
		Close();
		if (closed || IsVisible)
		{
			throw new InvalidOperationException("Tray lifecycle: close must hide and preserve window.");
		}
		PrepareForShutdown();
		Close();
		if (!closed)
		{
			throw new InvalidOperationException("Tray lifecycle: explicit exit must close window.");
		}
	}
}
