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

	/// <summary>Mac test 2: tüm modlar kullanılabilir (Ekran/Ses: ScreenCaptureKit, Spotify: AppleScript).</summary>
	internal static bool ModeAvailable(string? mode) => mode is "Gökkuşağı" or "Şömine" or "Gün ışığı" or "Uyku" or "Uyandırma" or "Manuel" or "Ekran" or "Ses" or "Spotify";

	private static readonly string[] Tiles = { "Manuel", "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" };

	// Ekran / ses / medya kaynakları (macOS gerçek, diğer sistemlerde demo)
	private IScreenSource? screenSource;
	private readonly INowPlaying media = OperatingSystem.IsMacOS() ? new MacNowPlaying() : new DemoNowPlaying();
	private readonly MacPrefs macPrefs = MacPrefs.Load();
	private string? permissionPane;

	private IScreenSource ScreenSource => screenSource ??= OperatingSystem.IsMacOS() ? new MacScreenSource() : new DemoScreenSource();

	private IAudioSource CreateAudioSource() => !OperatingSystem.IsMacOS() ? new DemoAudioSource()
		: macPrefs.AudioSource == 1 ? new MacMicrophone() : new MacSystemAudio();

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
		foreach (string id in Tiles)
		{
			StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 5 };
			sp.Children.Add(Ui.ModeIcons.Icon(id, 20));
			sp.Children.Add(new TextBlock { Tag = "label", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
			Button b = new Button { Classes = { "tile" }, Height = 62, Margin = new Thickness(0, 0, 8, 8), Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Tag = id };
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
			}
			b.Classes.Set("selected", id == selectedTab);
		}
		ModeTitle.Text = L.Mode(selectedTab);
		ModeDesc.Text = selectedTab switch
		{
			"Manuel" => L.T("manual_desc"),
			"Ekran" => L.T("screen_desc") + "\n" + L.T("mac_perm_screen_note"),
			"Ses" => L.T("audio_desc") + (macPrefs.AudioSource == 0 ? "\n" + L.T("mac_perm_screen_note") : ""),
			"Spotify" => (settings.Spotify.OnlySpotify ? L.T("spotify_desc") : L.T("spotify_desc_any")) + "\n" + L.T("mac_perm_spotify_note"),
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
		ScreenOptions.IsVisible = tab == "Ekran";
		AudioOptions.IsVisible = tab == "Ses";
		SpotifyOptions.IsVisible = tab == "Spotify";
		if (permissionPane != null && !PermissionApplies(tab, permissionPane)) HidePermission();
		if (tab == "Ekran") _ = RefreshMonitors();
		if (tab == "Spotify" && modeName != "Spotify" && NowPlayingTitle.Text is null or "") ShowNowPlaying(null);
		RainbowOptions.IsVisible = tab == "Gökkuşağı";
		FireOptions.IsVisible = tab == "Şömine";
		DaylightOptions.IsVisible = tab == "Gün ışığı";
		RainbowBar.IsVisible = tab is "Gökkuşağı" or "Gün ışığı";
		ModeReset.IsVisible = tab != "Manuel";
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
		IAudioSource? audio = null;
		if (mode == "Ses")
		{
			HidePermission();
			try
			{
				audio = CreateAudioSource();
				using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
				await audio.StartAsync(cts.Token);
			}
			catch (Exception ex)
			{
				audio?.Dispose();
				Log("Ses kaynağı: " + ex);
				if (ex is PermissionException pe) ShowPermission(pe);
				DetailText.Text = L.F("mode_stopped_reason", Friendly(ex));
				LiveText.Text = DetailText.Text;
				return;
			}
		}
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
			modeTask = ModeLoop(mode, modeCancellation, audio);
		}
		else
		{
			audio?.Dispose();
		}
	}

	/// <summary>Windows ModeLoop'unun karşılığı: Ekran, Ses, Spotify, Gökkuşağı, Şömine, Gün ışığı, Uyku, Uyandırma.</summary>
	private async Task ModeLoop(string mode, CancellationTokenSource cts, IAudioSource? audio = null)
	{
		FireEngine fire = new FireEngine();
		byte[]? last = null, lastBri = null, lastTemp = null;
		double phase = 0;
		Stopwatch clockSw = Stopwatch.StartNew();
		double lastTime = 0;
		bool finished = false;
		// Ekran
		double r = 0, g = 0, b = 0, luma = 0.5;
		bool first = true;
		// Ses
		double level = settings.Audio.MinBrightness;
		Stopwatch silent = Stopwatch.StartNew();
		// Spotify geçişi
		NowPlayingTrack? track = null;
		double lastPoll = -10;
		MColor spFrom = currentColor, spTo = currentColor, spNow = currentColor;
		double spStart = 0;
		bool spHasTarget = false;
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
				if (mode == "Ekran")
				{
					ScreenOptions o = settings.Screen;
					interval = (int)Math.Clamp(o.IntervalMs, 50, 1000);
					int area = o.Area;
					var s = await ScreenSource.ReadAsync(o.Monitor, area, o.Method, o.IgnoreDark, cts.Token);
					double alpha = first ? 1.0 : Math.Clamp(1.0 - o.Smoothing / 100.0, 0.05, 1.0);
					r += (s.R - r) * alpha;
					g += (s.G - g) * alpha;
					b += (s.B - b) * alpha;
					luma += (s.Luma - luma) * alpha;
					MColor col = MColor.FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
					if (o.SaturationBoost > 0)
					{
						(double h, double sat, double v) = ColorMath.ToHsv(col);
						col = ColorMath.FromHsv(h, Math.Min(1.0, sat * (1.0 + o.SaturationBoost / 100.0 * 1.5)), v);
					}
					value = Protocol.Rgb(col.R, col.G, col.B);
					if (o.FollowBrightness) bri = 5.0 + 95.0 * Math.Sqrt(Math.Clamp(luma, 0, 1));
					SetLiveColor(col, bri);
					string[] areas = L.T("sc_areas").Split(',');
					LiveText.Text = L.F("live_screen", areas[Math.Clamp(area, 0, areas.Length - 1)], 1000.0 / interval);
					if (first) HidePermission();
					first = false;
				}
				else if (mode == "Ses")
				{
					AudioOptions o = settings.Audio;
					interval = (int)Math.Clamp(o.IntervalMs, 100, 600);
					float peak = 0f;
					for (int i = 0; i < 4; i++)
					{
						peak = Math.Max(peak, audio!.Peak());
						await Task.Delay(25, cts.Token);
					}
					if (peak > 0.0005) silent.Restart();
					double min = Math.Min(o.MinBrightness, o.MaxBrightness - 1);
					double target = AudioMath.Level(peak, o.Sensitivity, min, o.MaxBrightness);
					level += (target - level) * (target > level ? o.Attack / 100.0 : o.Release / 100.0);
					bri = level;
					briThreshold = 2;
					SetLiveColor(currentColor, level);
					LiveText.Text = silent.Elapsed.TotalSeconds > 4.0 ? L.T("no_sound") : L.F("live_audio", L.Pct(level));
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
							if (permissionPane == Privacy.AutomationPane) HidePermission();
						}
						catch (OperationCanceledException) when (cts.IsCancellationRequested)
						{
							throw;
						}
						catch (PermissionException pe)
						{
							track = null;
							ShowNowPlaying(null, L.T("media_unreadable"));
							ShowPermission(pe);
							lastPoll = now + 4; // izin verilene kadar daha seyrek sor
						}
						catch (Exception ex)
						{
							track = null;
							ShowNowPlaying(null, L.T("media_unreadable"));
							Log("Spotify: " + ex.Message);
						}
						if (track?.Color is MColor tc)
						{
							MColor target = CoverColor.Boost(tc, o.Saturation);
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
						SetLiveColor(spNow, o.Brightness);
					}
					bri = o.Brightness;
					briThreshold = 1;
					interval = t < 1 ? 110 : 400;
					LiveText.Text = track == null ? L.T("nothing_playing_wait") : track.Playing ? L.F("live_value", track.Title) : L.T("paused");
				}
				else if (mode == "Gökkuşağı")
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
							if (mode == "Ses")
							{
								SetSilently(BrightnessSlider, Math.Clamp(Math.Round(level), 1, 100));
								StateText.Text = L.F("audio_level", L.Pct(level));
							}
							else if (mode is "Uyku" or "Uyandırma" or "Gün ışığı") SetSilently(BrightnessSlider, Math.Clamp(Math.Round(bri.Value), 1, 100));
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
				if (ex is PermissionException pe) ShowPermission(pe);
				DetailText.Text = L.F("mode_stopped_reason", Friendly(ex));
				LiveText.Text = DetailText.Text;
			}
		}
		finally
		{
			audio?.Dispose();
			if (mode == "Ekran")
			{
				screenSource?.Dispose();
				screenSource = null;
			}
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
		Bind(ScInterval, () => settings.Screen.IntervalMs, v => settings.Screen.IntervalMs = Math.Round(v / 10) * 10, ScIntervalLabel, v => L.F("interval_fmt", Math.Round(v / 10) * 10, 1000.0 / Math.Max(10, Math.Round(v / 10) * 10)));
		Bind(ScSmooth, () => settings.Screen.Smoothing, v => settings.Screen.Smoothing = Math.Round(v), ScSmoothLabel, v => L.Pct(v));
		Bind(ScBoost, () => settings.Screen.SaturationBoost, v => settings.Screen.SaturationBoost = Math.Round(v), ScBoostLabel, v => L.Pct(v));
		Bind(AuSens, () => settings.Audio.Sensitivity, v => settings.Audio.Sensitivity = Math.Round(v, 1), AuSensLabel, v => $"{v:F1}×");
		Bind(AuAttack, () => settings.Audio.Attack, v => settings.Audio.Attack = Math.Round(v), AuAttackLabel, v => L.Pct(v));
		Bind(AuRelease, () => settings.Audio.Release, v => settings.Audio.Release = Math.Round(v), AuReleaseLabel, v => L.Pct(v));
		Bind(AuMin, () => settings.Audio.MinBrightness, v => settings.Audio.MinBrightness = Math.Round(v), AuMinLabel, v => L.Pct(v));
		Bind(AuMax, () => settings.Audio.MaxBrightness, v => settings.Audio.MaxBrightness = Math.Round(v), AuMaxLabel, v => L.Pct(v));
		Bind(AuInterval, () => settings.Audio.IntervalMs, v => settings.Audio.IntervalMs = Math.Round(v / 10) * 10, AuIntervalLabel, v => $"{Math.Round(v / 10) * 10:F0} ms");
		Bind(SpBri, () => settings.Spotify.Brightness, v => settings.Spotify.Brightness = Math.Round(v), SpBriLabel, v => L.Pct(v));
		Bind(SpFade, () => settings.Spotify.TransitionMs, v => settings.Spotify.TransitionMs = Math.Round(v / 100) * 100, SpFadeLabel, v => $"{Math.Round(v / 100) * 100:F0} ms");
		Bind(SpSat, () => settings.Spotify.Saturation, v => settings.Spotify.Saturation = Math.Round(v), SpSatLabel, v => L.Pct(v));
		ScIgnoreDark.IsCheckedChanged += (_, _) => { if (silentOptions > 0) return; settings.Screen.IgnoreDark = ScIgnoreDark.IsChecked == true; SaveSettings(); };
		ScFollow.IsCheckedChanged += (_, _) => { if (silentOptions > 0) return; settings.Screen.FollowBrightness = ScFollow.IsChecked == true; SaveSettings(); };
		SpOnly.IsCheckedChanged += (_, _) => { if (silentOptions > 0) return; settings.Spotify.OnlySpotify = SpOnly.IsChecked == true; SaveSettings(); RebuildModeTilesText(); };
		ScArea.SelectionChanged += (_, _) => { if (silentOptions > 0 || ScArea.SelectedIndex < 0) return; settings.Screen.Area = ScArea.SelectedIndex; SaveSettings(); };
		ScMethod.SelectionChanged += (_, _) => { if (silentOptions > 0 || ScMethod.SelectedIndex < 0) return; settings.Screen.Method = ScMethod.SelectedIndex; SaveSettings(); };
		ScMonitor.SelectionChanged += (_, _) => { if (silentOptions > 0 || ScMonitor.SelectedIndex < 0) return; settings.Screen.Monitor = ScMonitor.SelectedIndex; SaveSettings(); };
		ScMonitor.DropDownOpened += (_, _) => _ = RefreshMonitors();
		AuSource.SelectionChanged += async (_, _) =>
		{
			if (silentOptions > 0 || AuSource.SelectedIndex < 0) return;
			macPrefs.AudioSource = AuSource.SelectedIndex;
			macPrefs.Save();
			HidePermission();
			RebuildModeTilesText();
			if (modeName == "Ses")
			{
				await StopModeAsync();
				await StartMode("Ses");
			}
		};
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
			ScArea.ItemsSource = L.T("sc_areas").Split(',');
			ScMethod.ItemsSource = L.T("sc_methods").Split(',');
			AuSource.ItemsSource = L.T("mac_audio_sources").Split(',');
			ScArea.SelectedIndex = Math.Clamp(settings.Screen.Area, 0, 5);
			ScMethod.SelectedIndex = Math.Clamp(settings.Screen.Method, 0, 1);
			AuSource.SelectedIndex = Math.Clamp(macPrefs.AudioSource, 0, 1);
			if (monitorNames.Length > 0) _ = RefreshMonitors();
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
			ScIgnoreDark.IsChecked = settings.Screen.IgnoreDark;
			ScFollow.IsChecked = settings.Screen.FollowBrightness;
			SpOnly.IsChecked = settings.Spotify.OnlySpotify;
			ScArea.SelectedIndex = Math.Clamp(settings.Screen.Area, 0, 5);
			ScMethod.SelectedIndex = Math.Clamp(settings.Screen.Method, 0, 1);
			if (monitorNames.Length > 0) ScMonitor.SelectedIndex = Math.Clamp(settings.Screen.Monitor, 0, monitorNames.Length - 1);
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
			case "Ekran": settings.Screen = new ScreenOptions(); break;
			case "Ses": settings.Audio = new AudioOptions(); break;
			case "Spotify": settings.Spotify = new SpotifyOptions(); break;
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

	// =====================================================================
	//  Ekran listesi, çalan parça, izin uyarısı
	// =====================================================================

	private string[] monitorNames = Array.Empty<string>();
	private bool refreshingMonitors;

	private async Task RefreshMonitors()
	{
		if (refreshingMonitors) return;
		refreshingMonitors = true;
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			string[] names;
			try
			{
				names = await ScreenSource.MonitorNamesAsync(cts.Token);
			}
			catch (Exception ex)
			{
				// İzin yokken ScreenCaptureKit ekran listesini vermez; birincil ekranı göster.
				Log("Ekran listesi alınamadı: " + ex.Message);
				names = new[] { L.F("monitor_n", 1, L.T("primary_suffix"), "—", "—") };
			}
			if (names.Length == 0) names = new[] { L.F("monitor_n", 1, L.T("primary_suffix"), "—", "—") };
			if (names.SequenceEqual(monitorNames) && ScMonitor.ItemsSource != null) return;
			monitorNames = names;
			silentOptions++;
			try
			{
				ScMonitor.ItemsSource = names;
				ScMonitor.SelectedIndex = Math.Clamp(settings.Screen.Monitor, 0, names.Length - 1);
			}
			finally
			{
				silentOptions--;
			}
		}
		finally
		{
			refreshingMonitors = false;
		}
	}

	private void ShowNowPlaying(NowPlayingTrack? t, string? error = null)
	{
		if (t == null)
		{
			NowPlayingTitle.Text = error ?? L.T("nothing_playing");
			NowPlayingArtist.Text = error == null ? (settings.Spotify.OnlySpotify ? L.T("start_spotify") : L.T("start_player")) : L.T("media_session_needed");
			NowPlayingChip.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
			return;
		}
		NowPlayingTitle.Text = t.Title.Length > 0 ? t.Title : L.T("unknown_track");
		NowPlayingArtist.Text = (t.Artist.Length > 0 ? t.Artist + " • " : "") + (t.Playing ? t.App : L.F("paused_suffix", t.App));
		if (t.Color is MColor c) NowPlayingChip.Background = new SolidColorBrush(A(c));
	}

	private static bool PermissionApplies(string tab, string pane) => pane switch
	{
		Privacy.ScreenPane => tab is "Ekran" or "Ses",
		Privacy.MicrophonePane => tab == "Ses",
		Privacy.AutomationPane => tab == "Spotify",
		_ => false,
	};

	private void ShowPermission(PermissionException pe)
	{
		permissionPane = pe.Pane;
		PermissionText.Text = L.Message(pe.Message);
		PermissionButton.Content = pe.Pane switch
		{
			Privacy.MicrophonePane => L.T("mac_perm_mic_btn"),
			Privacy.AutomationPane => L.T("mac_perm_auto_btn"),
			_ => L.T("mac_perm_screen_btn"),
		};
		PermissionPanel.IsVisible = true;
	}

	private void HidePermission()
	{
		permissionPane = null;
		PermissionPanel.IsVisible = false;
	}

	private void PermissionButton_Click(object? sender, RoutedEventArgs e)
	{
		if (permissionPane == null) return;
		OpenExternal(permissionPane);
		DetailText.Text = L.T("mac_restart_needed");
	}

	/// <summary>Ekran görüntüsü otomasyonu için izin panelini göster.</summary>
	internal void ShowPermissionDemo(string pane) => ShowPermission(pane switch
	{
		Privacy.AutomationPane => Privacy.AutomationDenied("Spotify"),
		Privacy.MicrophonePane => Privacy.MicDenied(),
		_ => Privacy.ScreenDenied(),
	});
}
