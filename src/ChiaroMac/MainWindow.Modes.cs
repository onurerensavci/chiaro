#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

public partial class MainWindow
{
	internal static readonly string[] UserModes = { "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" };

	/// <summary>Mac test 1'de çalışan modlar (yalnız yazılım efektleri). Ekran/Ses/Spotify: "yakında".</summary>
	internal static bool ModeAvailable(string? mode) => mode is "Gökkuşağı" or "Şömine" or "Gün ışığı" or "Uyku" or "Uyandırma" or "Manuel";

	private static readonly (string Id, string Icon)[] Tiles =
	{
		("Manuel", "🎚️"), ("Ekran", "🖥️"), ("Ses", "🎵"), ("Spotify", "🎧"), ("Gökkuşağı", "🌈"), ("Şömine", "🔥"), ("Gün ışığı", "🌤️"),
	};

	private CancellationTokenSource? modeCancellation;
	private string modeName = "";
	private Task modeTask = Task.CompletedTask;
	private string selectedTab = "Manuel";
	private readonly Dictionary<string, Button> tileButtons = new Dictionary<string, Button>();
	private int silentOptions;

	// Uyku / uyandırma durumları (Zamanlayıcı)
	private DateTime sleepFadeStart;
	private DateTime sleepFadeEnd;
	private double sleepFadeFrom = 70;
	private DateTime wakeStart;
	private TimeSpan wakeDuration = TimeSpan.FromMinutes(20);

	private bool ModeSupported(string mode) => ModeAvailable(mode) && mode != "Manuel" && mode switch
	{
		"Ses" => ble.Supports(3),
		"Gün ışığı" => ble.Supports(4) || ble.Supports(5),
		"Uyku" => ble.Supports(2),
		"Uyandırma" => ble.Supports(2) && (ble.Supports(3) || ble.Supports(5)),
		_ => ble.Supports(5),
	};

	private string UnsupportedText(string mode) => !ModeAvailable(mode) ? L.T("mac_soon_desc") : mode switch
	{
		"Ses" => L.T("no_brightness_ctl"),
		"Gün ışığı" => L.T("no_white_ctl"),
		_ => L.T("no_color_ctl"),
	};

	private bool ModeConflicts(int code)
	{
		if (modeCancellation == null) return false;
		if (code == 3)
		{
			return modeName switch
			{
				"Ses" or "Gün ışığı" or "Uyku" or "Uyandırma" => true,
				"Şömine" => ble.Supports(3),
				"Ekran" => settings.Screen.FollowBrightness,
				"Gökkuşağı" => settings.Rainbow.UseBrightness,
				_ => false,
			};
		}
		return modeName != "Ses";
	}

	private void BuildModeTiles()
	{
		ModeTiles.Children.Clear();
		tileButtons.Clear();
		foreach ((string id, string icon) in Tiles)
		{
			StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
			sp.Children.Add(new TextBlock { Text = icon, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
			sp.Children.Add(new TextBlock { Tag = "label", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center });
			sp.Children.Add(new TextBlock { Tag = "soon", Classes = { "sub" }, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = !ModeAvailable(id) });
			Button b = new Button { Classes = { "tile" }, Width = 116, Height = 62, Content = sp, HorizontalContentAlignment = HorizontalAlignment.Center, Tag = id };
			if (!ModeAvailable(id)) b.Classes.Add("soon");
			b.Click += ModeTile_Click;
			tileButtons[id] = b;
			ModeTiles.Children.Add(b);
		}
		RebuildModeTilesText();
	}

	private void RebuildModeTilesText()
	{
		foreach ((string id, Button b) in tileButtons)
		{
			if (b.Content is not StackPanel sp) continue;
			foreach (TextBlock t in sp.Children.OfType<TextBlock>())
			{
				if (Equals(t.Tag, "label")) t.Text = (modeName == id ? "● " : "") + L.Mode(id);
				if (Equals(t.Tag, "soon")) t.Text = L.T("mac_soon");
			}
			b.Classes.Set("selected", id == selectedTab);
		}
		ModeTitle.Text = L.Mode(selectedTab);
		ModeDesc.Text = selectedTab switch
		{
			"Manuel" => L.T("manual_desc"),
			"Ekran" => L.T("screen_desc") + "\n" + L.T("mac_soon_desc"),
			"Ses" => L.T("audio_desc") + "\n" + L.T("mac_soon_desc"),
			"Spotify" => L.T("spotify_desc") + "\n" + L.T("mac_soon_desc"),
			"Gökkuşağı" => L.T("rainbow_desc"),
			"Şömine" => L.T("fire_desc"),
			"Gün ışığı" => L.T("daylight_desc"),
			_ => "",
		};
	}

	private void ShowTab(string tab)
	{
		if (!UserModes.Contains(tab)) tab = "Manuel";
		selectedTab = tab;
		settings.SelectedTab = tab;
		RainbowOptions.IsVisible = tab == "Gökkuşağı";
		FireOptions.IsVisible = tab == "Şömine";
		DaylightOptions.IsVisible = tab == "Gün ışığı";
		RainbowBar.IsVisible = tab is "Gökkuşağı" or "Gün ışığı";
		ModeReset.IsVisible = tab is "Gökkuşağı" or "Şömine" or "Gün ışığı";
		ModeStart.IsVisible = tab != "Manuel" && ModeAvailable(tab) || modeName.Length > 0;
		UpdateRainbowBar();
		RebuildModeTilesText();
		UpdateControls();
	}

	private async void ModeTile_Click(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string tab }) return;
		ShowTab(tab);
		activeSceneId = null;
		if (tab == "Manuel")
		{
			if (modeCancellation != null) await StopModeAsync();
			return;
		}
		if (!ModeAvailable(tab))
		{
			LiveText.Text = L.T("mac_soon");
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

	private async void ModeStart_Click(object? sender, RoutedEventArgs e)
	{
		if (selectedTab == "Manuel" || modeName == selectedTab || modeName.Length > 0 && !ModeAvailable(selectedTab)) await StopModeAsync();
		else await StartMode(selectedTab);
	}

	private async Task StopModeAsync()
	{
		modeCancellation?.Cancel();
		await modeTask;
	}

	private async Task StartMode(string mode, double? brightness = null)
	{
		if (busy || closed || !ble.Ready || !ModeSupported(mode)) return;
		await Run(L.F("mode_starting", L.Mode(mode)), async ct =>
		{
			if (mode == "Uyandırma" && powerOn != true)
			{
				if (ble.Supports(3)) await ble.Write(3, Protocol.Brightness(1), ct);
				MColor dawn = SunriseColor(0, settings.Wake.TargetKelvin);
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
			}
		}, 10);
		if (!closed && ble.Ready && !lastRunFailed)
		{
			autoCancelled = true;
			modeCancellation = new CancellationTokenSource();
			modeName = mode;
			StatusText.Text = L.F("connected_to", connectedName);
			DetailText.Text = L.T("applied_instantly");
			if (selectedTab != mode && UserModes.Contains(mode)) ShowTab(mode);
			liveLast.Clear();
			UpdateBulbVisual();
			RebuildModeTilesText();
			ShowTab(selectedTab);
			modeTask = ModeLoop(mode, modeCancellation);
		}
	}

	/// <summary>Windows ModeLoop'unun yazılım efektleri (Gökkuşağı, Şömine, Gün ışığı, Uyku, Uyandırma).</summary>
	private async Task ModeLoop(string mode, CancellationTokenSource cts)
	{
		FireEngine fire = new FireEngine();
		byte[]? last = null, lastBri = null, lastTemp = null;
		double phase = 0;
		Stopwatch clockSw = Stopwatch.StartNew();
		double lastTime = 0;
		bool finished = false;
		try
		{
			while (!cts.IsCancellationRequested)
			{
				Stopwatch frame = Stopwatch.StartNew();
				double now = clockSw.Elapsed.TotalSeconds;
				double dt = Math.Clamp(now - lastTime, 0, 1);
				lastTime = now;
				byte[]? value = null, temp = null;
				bool powerOff = false;
				double? bri = null;
				int briThreshold = 3;
				int interval = 160;
				if (mode == "Gökkuşağı")
				{
					RainbowOptions o = settings.Rainbow;
					interval = (int)Math.Clamp(o.IntervalMs, 100, 1000);
					phase += dt / Math.Max(1.0, o.CycleSeconds);
					double pos = RainbowEngine.Position(phase, 1.0, (RainbowDirection)o.Direction);
					MColor col = RainbowEngine.ColorAt(pos, o.Palette, o.Saturation / 100.0);
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (o.UseBrightness)
					{
						bri = o.Brightness;
						briThreshold = 1;
					}
					SetLiveColor(col, bri);
					LiveText.Text = L.F("live_rainbow", RainbowEngine.DirectionNames[Math.Clamp(o.Direction, 0, RainbowEngine.DirectionNames.Length - 1)], o.CycleSeconds);
				}
				else if (mode == "Şömine")
				{
					FireOptions o = settings.Fire;
					interval = FireEngine.FrameMilliseconds(o.Speed);
					(MColor col, double fb) = fire.Next(o.Intensity / 100.0, o.Speed, o.Warmth / 100.0, o.Brightness, o.Sparks);
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (ble.Supports(3))
					{
						bri = fb;
						briThreshold = 2;
					}
					SetLiveColor(col, bri ?? o.Brightness);
					LiveText.Text = ble.Supports(3) ? L.T("live_fire") : L.T("live_fire_color");
				}
				else if (mode == "Gün ışığı")
				{
					(double k, double db) = Daylight.At(settings.Daylight, DateTime.Now);
					if (ble.Supports(4)) temp = Protocol.Temperature(k);
					else
					{
						MColor kc = ColorMath.FromKelvin(k);
						value = Protocol.Rgb(kc.R, kc.G, kc.B);
					}
					bri = db;
					briThreshold = 1;
					interval = 1000;
					lastWasWhite = ble.Supports(4);
					SetLiveColor(ColorMath.FromKelvin(k), db);
					LiveText.Text = L.F("live_daylight", k, L.Pct(db));
				}
				else if (mode == "Uyku")
				{
					double total = Math.Max(1, (sleepFadeEnd - sleepFadeStart).TotalSeconds);
					double t = Math.Clamp((DateTime.Now - sleepFadeStart).TotalSeconds / total, 0, 1);
					bri = Math.Max(1, sleepFadeFrom + (1 - sleepFadeFrom) * t);
					briThreshold = 1;
					interval = 1000;
					SetLiveColor(currentColor, bri);
					LiveText.Text = L.F("sleep_progress", Remaining(sleepFadeEnd));
					if (t >= 1) powerOff = true;
				}
				else if (mode == "Uyandırma")
				{
					WakeAlarm o = settings.Wake;
					double t = Math.Clamp((DateTime.Now - wakeStart).TotalSeconds / Math.Max(1, wakeDuration.TotalSeconds), 0, 1);
					MColor col = SunriseColor(t, o.TargetKelvin);
					if (t >= 0.8 && ble.Supports(4)) temp = Protocol.Temperature(2700 + (o.TargetKelvin - 2700) * ((t - 0.8) / 0.2));
					else if (ble.Supports(5)) value = Protocol.Rgb(col.R, col.G, col.B);
					bri = 1 + (o.TargetBrightness - 1) * Math.Pow(t, 1.7);
					briThreshold = 1;
					interval = 1500;
					SetLiveColor(col, bri);
					LiveText.Text = L.F("wake_progress", L.Pct(bri ?? 0));
					if (t >= 1) finished = true;
				}
				else
				{
					break;
				}
				await lightGate.WaitAsync(cts.Token);
				try
				{
					if (value != null && (last == null || !value.SequenceEqual(last)))
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4));
						await ble.Write(5, value, timeout.Token);
						last = value;
						StateText.Text = L.F("mode_live", L.Mode(mode));
					}
					if (temp != null && (lastTemp == null || !temp.SequenceEqual(lastTemp)))
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4));
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
							timeout.CancelAfter(TimeSpan.FromSeconds(4));
							await ble.Write(3, bv, timeout.Token);
							lastBri = bv;
							if (mode is "Uyku" or "Uyandırma" or "Gün ışığı") SetSilently(BrightnessSlider, Math.Clamp(Math.Round(bri.Value), 1, 100));
						}
					}
					if (powerOff)
					{
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
						timeout.CancelAfter(TimeSpan.FromSeconds(4));
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
		catch (Exception ex)
		{
			Log(ex.ToString());
			if (!closed)
			{
				DetailText.Text = L.F("mode_stopped_reason", Friendly(ex));
				LiveText.Text = DetailText.Text;
			}
		}
		finally
		{
			if (mode == "Uyku" && cts.IsCancellationRequested && settings.SleepUntil.HasValue && !closed)
			{
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
				if (!busy) StatusText.Text = ble.Ready ? L.F("connected_to", connectedName) : L.T("disconnected");
				StateText.Text = L.T("last_color_kept");
				if (cts.IsCancellationRequested)
				{
					LiveText.Text = "";
					DetailText.Text = L.T("mode_stopped");
				}
				UpdateBulbVisual();
				RebuildModeTilesText();
				ShowTab(selectedTab);
				UpdateTimerUi();
			}
			cts.Dispose();
		}
	}

	private void SetLiveColor(MColor c, double? brightness)
	{
		currentColor = c;
		colorKnown = true;
		if (brightness.HasValue) liveBrightness = brightness;
		UpdateBulbVisual();
	}

	internal static MColor SunriseColor(double t, double targetKelvin)
	{
		(double At, MColor C)[] stops =
		{
			(0.00, MColor.FromRgb(0x8B, 0x1A, 0x00)),
			(0.30, MColor.FromRgb(0xFF, 0x4D, 0x00)),
			(0.55, MColor.FromRgb(0xFF, 0x9A, 0x3C)),
			(0.80, ColorMath.FromKelvin(2700)),
			(1.00, ColorMath.FromKelvin(targetKelvin)),
		};
		t = Math.Clamp(t, 0, 1);
		for (int i = 1; i < stops.Length; i++)
		{
			if (t <= stops[i].At)
			{
				double k = (t - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
				return ColorMath.Lerp(stops[i - 1].C, stops[i].C, k);
			}
		}
		return stops[^1].C;
	}

	private static string Remaining(DateTime until)
	{
		TimeSpan r = until - DateTime.Now;
		if (r < TimeSpan.Zero) r = TimeSpan.Zero;
		return r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes:00}:{r.Seconds:00}";
	}

	// =====================================================================
	//  Mod seçenekleri (kaydırıcılar ↔ ayarlar)
	// =====================================================================

	private void InitModeOptions()
	{
		InitModeOptionText();
		Bind(RbCycle, () => settings.Rainbow.CycleSeconds, v => settings.Rainbow.CycleSeconds = Math.Round(v), RbCycleLabel, v => L.F("fmt_sec", v));
		Bind(RbSat, () => settings.Rainbow.Saturation, v => settings.Rainbow.Saturation = Math.Round(v), RbSatLabel, v => L.Pct(v));
		Bind(RbBri, () => settings.Rainbow.Brightness, v => settings.Rainbow.Brightness = Math.Round(v), RbBriLabel, v => L.Pct(v));
		Bind(FireIntensity, () => settings.Fire.Intensity, v => settings.Fire.Intensity = Math.Round(v), FireIntensityLabel, v => L.Pct(v));
		Bind(FireSpeed, () => settings.Fire.Speed, v => settings.Fire.Speed = Math.Round(v), FireSpeedLabel, v => $"{v:F0}");
		Bind(FireWarmth, () => settings.Fire.Warmth, v => settings.Fire.Warmth = Math.Round(v), FireWarmthLabel, v => L.Pct(v));
		Bind(FireBri, () => settings.Fire.Brightness, v => settings.Fire.Brightness = Math.Round(v), FireBriLabel, v => L.Pct(v));
		Bind(DlMinK, () => settings.Daylight.MinKelvin, v => settings.Daylight.MinKelvin = Math.Round(v / 50) * 50, DlMinKLabel, v => $"{Math.Round(v / 50) * 50:F0} K");
		Bind(DlMaxK, () => settings.Daylight.MaxKelvin, v => settings.Daylight.MaxKelvin = Math.Round(v / 50) * 50, DlMaxKLabel, v => $"{Math.Round(v / 50) * 50:F0} K");
		Bind(DlMinB, () => settings.Daylight.MinBrightness, v => settings.Daylight.MinBrightness = Math.Round(v), DlMinBLabel, v => L.Pct(v));
		Bind(DlMaxB, () => settings.Daylight.MaxBrightness, v => settings.Daylight.MaxBrightness = Math.Round(v), DlMaxBLabel, v => L.Pct(v));
		RbUseBri.IsCheckedChanged += (_, _) => { if (silentOptions > 0) return; settings.Rainbow.UseBrightness = RbUseBri.IsChecked == true; RbBri.IsEnabled = settings.Rainbow.UseBrightness; SaveSettings(); };
		FireSparks.IsCheckedChanged += (_, _) => { if (silentOptions > 0) return; settings.Fire.Sparks = FireSparks.IsChecked == true; SaveSettings(); };
		RbDirection.SelectionChanged += (_, _) => { if (silentOptions > 0 || RbDirection.SelectedIndex < 0) return; settings.Rainbow.Direction = RbDirection.SelectedIndex; SaveSettings(); };
		RbPalette.SelectionChanged += (_, _) => { if (silentOptions > 0 || RbPalette.SelectedIndex < 0) return; settings.Rainbow.Palette = RbPalette.SelectedIndex; SaveSettings(); UpdateRainbowBar(); };
		LoadModeOptions();
	}

	private readonly List<Action> optionLoaders = new List<Action>();

	private void Bind(Slider s, Func<double> get, Action<double> set, TextBlock label, Func<double, string> fmt)
	{
		void Load()
		{
			silentOptions++;
			try { s.Value = get(); } finally { silentOptions--; }
			label.Text = fmt(s.Value);
		}
		optionLoaders.Add(Load);
		s.PropertyChanged += (_, e) =>
		{
			if (e.Property != RangeBase.ValueProperty) return;
			label.Text = fmt(s.Value);
			if (silentOptions > 0) return;
			set(s.Value);
			if (s == DlMinK || s == DlMaxK) UpdateRainbowBar();
		};
		OnRelease(s, () => { if (silentOptions == 0) SaveSettings(); });
	}

	private void InitModeOptionText()
	{
		silentOptions++;
		try
		{
			RbDirection.ItemsSource = RainbowEngine.DirectionNames;
			RbPalette.ItemsSource = RainbowEngine.PaletteNames;
			RbDirection.SelectedIndex = Math.Clamp(settings.Rainbow.Direction, 0, 2);
			RbPalette.SelectedIndex = Math.Clamp(settings.Rainbow.Palette, 0, 5);
		}
		finally
		{
			silentOptions--;
		}
		foreach (Action a in optionLoaders) a();
	}

	private void LoadModeOptions()
	{
		ClampSettings();
		silentOptions++;
		try
		{
			RbUseBri.IsChecked = settings.Rainbow.UseBrightness;
			RbBri.IsEnabled = settings.Rainbow.UseBrightness;
			FireSparks.IsChecked = settings.Fire.Sparks;
			RbDirection.SelectedIndex = Math.Clamp(settings.Rainbow.Direction, 0, 2);
			RbPalette.SelectedIndex = Math.Clamp(settings.Rainbow.Palette, 0, 5);
			DlWake.Text = settings.Daylight.WakeTime;
			DlSleep.Text = settings.Daylight.SleepTime;
		}
		finally
		{
			silentOptions--;
		}
		foreach (Action a in optionLoaders) a();
		UpdateRainbowBar();
	}

	private void DaylightTime_LostFocus(object? sender, RoutedEventArgs e)
	{
		if (Daylight.TryParseTime(DlWake.Text, out TimeSpan w)) settings.Daylight.WakeTime = w.ToString(@"hh\:mm");
		if (Daylight.TryParseTime(DlSleep.Text, out TimeSpan s)) settings.Daylight.SleepTime = s.ToString(@"hh\:mm");
		DlWake.Text = settings.Daylight.WakeTime;
		DlSleep.Text = settings.Daylight.SleepTime;
		SaveSettings();
		UpdateRainbowBar();
	}

	private void ModeReset_Click(object? sender, RoutedEventArgs e)
	{
		switch (selectedTab)
		{
			case "Gökkuşağı": settings.Rainbow = new RainbowOptions(); break;
			case "Şömine": settings.Fire = new FireOptions(); break;
			case "Gün ışığı": settings.Daylight = new DaylightOptions(); break;
			default: return;
		}
		SaveSettings();
		LoadModeOptions();
		DetailText.Text = L.F("mode_reset_x", L.Mode(selectedTab));
	}

	private void ApplyModeOptions(string mode, string? json)
	{
		if (string.IsNullOrEmpty(json)) return;
		try
		{
			switch (mode)
			{
				case "Ekran": settings.Screen = JsonSerializer.Deserialize<ScreenOptions>(json) ?? settings.Screen; break;
				case "Ses": settings.Audio = JsonSerializer.Deserialize<AudioOptions>(json) ?? settings.Audio; break;
				case "Spotify": settings.Spotify = JsonSerializer.Deserialize<SpotifyOptions>(json) ?? settings.Spotify; break;
				case "Gökkuşağı": settings.Rainbow = JsonSerializer.Deserialize<RainbowOptions>(json) ?? settings.Rainbow; break;
				case "Şömine": settings.Fire = JsonSerializer.Deserialize<FireOptions>(json) ?? settings.Fire; break;
				case "Gün ışığı": settings.Daylight = JsonSerializer.Deserialize<DaylightOptions>(json) ?? settings.Daylight; break;
			}
			LoadModeOptions();
		}
		catch (Exception ex)
		{
			Log("Sahne seçenekleri: " + ex.Message);
		}
	}

	/// <summary>Gökkuşağı paletinin ya da gün ışığı eğrisinin önizleme çubuğu.</summary>
	private void UpdateRainbowBar()
	{
		LinearGradientBrush g = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
		for (int i = 0; i <= 24; i++)
		{
			double p = i / 24.0;
			MColor c;
			if (selectedTab == "Gün ışığı")
			{
				(double k, double b) = Daylight.At(settings.Daylight, DateTime.Today.AddHours(p * 24));
				MColor kc = ColorMath.FromKelvin(k);
				double f = 0.25 + 0.75 * b / 100.0;
				c = MColor.FromRgb((byte)(kc.R * f), (byte)(kc.G * f), (byte)(kc.B * f));
			}
			else
			{
				c = RainbowEngine.ColorAt(p, settings.Rainbow.Palette, settings.Rainbow.Saturation / 100.0);
			}
			g.GradientStops.Add(new GradientStop(A(c), p));
		}
		RainbowBar.Background = g;
	}
}
