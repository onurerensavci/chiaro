#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HueStudio;

/// <summary>Uyku sayacı, uyandırma, programlar, gün ışığı, Spotify ve bildirim ışığı.</summary>
public partial class MainWindow
{
	private static string[] DayShort => L.DaysShort;
	private static string[] DayLong => L.DaysLong;

	private DispatcherTimer? clock;
	private string lastMinuteKey = "";
	private bool sleepFadeStarted;
	private DateTime sleepFadeStart;
	private DateTime sleepFadeEnd;
	private double sleepFadeFrom = 70;
	private DateTime wakeStart;
	private TimeSpan wakeDuration;
	private NotificationWatcher? notifier;
	private bool flashing;
	private DateTime lastFlash = DateTime.MinValue;
	private readonly MediaColorReader media = new MediaColorReader();

	private static int DayIndex(DateTime d) => ((int)d.DayOfWeek + 6) % 7;

	private void InitAutomation()
	{
		NotifyCard.DataContext = settings;
		AnimBox.IsChecked = settings.Animations;
		WheelBox.IsChecked = settings.TrayWheel;
		WakeToggle.IsChecked = settings.Wake.Enabled;
		WakeTime.Text = settings.Wake.Time;
		SetSilently(WakeFade, Math.Clamp(settings.Wake.FadeMinutes, 5, 60));
		SetSilently(WakeBri, Math.Clamp(settings.Wake.TargetBrightness, 20, 100));
		Anim.AttachSwitch(WakeToggle);
		Anim.AttachSwitch(NotifyToggle);
		WakeDays.Children.Clear();
		for (int i = 0; i < 7; i++)
		{
			int day = i;
			ToggleButton t = new ToggleButton { Content = DayShort[i], IsChecked = settings.Wake.Days[i], ToolTip = DayLong[i] };
			t.SetResourceReference(StyleProperty, "Chip");
			t.Click += (_, _) => { settings.Wake.Days[day] = t.IsChecked == true; SaveSettings(); UpdateTimerUi(); };
			WakeDays.Children.Add(t);
		}
		NotifyToggle.IsChecked = settings.Notify.Enabled;
		NotifyHex.Text = settings.Notify.Color;
		UpdateNotifyChip();
		LoadDaylightTimes();
		UpdateDaylightBar();
		RebuildSchedules();
		RebuildScenes();
		UpdateSpotifySubtitle();
		clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
		clock.Tick += (_, _) => ClockTick();
		clock.Start();
		UpdateTimerUi();
	}

	/// <summary>Kalıcı modda (gerçek uygulama) arka plan servisleri.</summary>
	internal async void StartServices()
	{
		if (!persistent) return;
		UpdateWheelHook();
		if (settings.Notify.Enabled)
		{
			await StartNotifier();
		}
	}

	private void DisposeAutomation()
	{
		clock?.Stop();
		notifier?.Dispose();
		wheelHook?.Dispose();
		rectTimer?.Stop();
		liveTimer?.Stop();
		quick?.Close();
		mini?.Close();
		osd?.Close();
	}

	// =====================================================================
	//  Saat: geri sayım, uyku solması, dakikalık olaylar
	// =====================================================================

	private void ClockTick()
	{
		if (closed) return;
		DateTime now = DateTime.Now;
		if (settings.SleepUntil is DateTime until)
		{
			TimeSpan rem = until - now;
			double fade = Math.Min(300, Math.Max(30, settings.SleepMinutes * 60 / 2));
			if (rem.TotalSeconds <= fade && !sleepFadeStarted && modeName != "Uyku")
			{
				sleepFadeStarted = true;
				_ = BeginSleepFade(until);
			}
			else if (rem.TotalSeconds < -30 && modeName != "Uyku")
			{
				settings.SleepUntil = null;
				sleepFadeStarted = false;
				SaveSettings();
			}
		}
		string key = now.ToString("yyyyMMddHHmm");
		if (key != lastMinuteKey)
		{
			bool firstTick = lastMinuteKey.Length == 0;
			lastMinuteKey = key;
			if (!firstTick || now.Second < 5) CheckMinuteEvents(now);
			if (selectedTab == "Gün ışığı") UpdateDaylightBar();
		}
		UpdateTimerUi();
		if (settings.SleepUntil.HasValue || modeName.Length > 0) RaiseLightState();
	}

	private void UpdateTimerUi()
	{
		string? remaining = settings.SleepUntil is DateTime u ? Remaining(u) : null;
		SleepRemaining.Text = remaining ?? "";
		SleepCancelButton.Visibility = remaining == null ? Visibility.Collapsed : Visibility.Visible;
		tray?.SetSleep(remaining);
		string badge;
		if (remaining != null) badge = "UYKU " + remaining;
		else if (settings.Wake.Enabled && NextWake() is DateTime w) badge = "ALARM " + w.ToString("HH:mm");
		else if (settings.Schedules.Any(s => s.Enabled)) badge = $"{settings.Schedules.Count(s => s.Enabled)} PROGRAM";
		else badge = "KAPALI";
		TimerBadge.Text = badge;
		if (settings.Wake.Enabled && NextWake() is DateTime next)
		{
			DateTime start = next - TimeSpan.FromMinutes(settings.Wake.FadeMinutes);
			string day = next.Date == DateTime.Today ? L.T("today") : next.Date == DateTime.Today.AddDays(1) ? L.T("tomorrow") : DayLong[DayIndex(next)];
			WakeNext.Text = L.F("wake_next", day, next, start);
		}
		else
		{
			WakeNext.Text = settings.Wake.Enabled ? L.T("no_days") : "";
		}
	}

	private DateTime? NextWake()
	{
		if (!Daylight.TryParseTime(settings.Wake.Time, out TimeSpan t)) return null;
		DateTime now = DateTime.Now;
		for (int d = 0; d < 8; d++)
		{
			DateTime at = now.Date.AddDays(d) + t;
			if (at - TimeSpan.FromMinutes(settings.Wake.FadeMinutes) <= now.AddSeconds(-59)) continue;
			if (settings.Wake.Days[DayIndex(at)]) return at;
		}
		return null;
	}

	private void CheckMinuteEvents(DateTime now)
	{
		// Uyandırma: gün doğumu, alarm saatinde bitecek şekilde başlar.
		if (settings.Wake.Enabled && Daylight.TryParseTime(settings.Wake.Time, out TimeSpan wt))
		{
			foreach (DateTime alarm in new[] { now.Date + wt, now.Date.AddDays(1) + wt })
			{
				DateTime start = alarm - TimeSpan.FromMinutes(settings.Wake.FadeMinutes);
				if (start.ToString("yyyyMMddHHmm") == now.ToString("yyyyMMddHHmm") && settings.Wake.Days[DayIndex(alarm)])
				{
					_ = RunWake(alarm);
				}
			}
		}
		foreach (ScheduleItem item in settings.Schedules.ToList())
		{
			if (!item.Enabled || !Daylight.TryParseTime(item.Time, out TimeSpan st)) continue;
			if (st.Hours == now.Hour && st.Minutes == now.Minute && item.Days[DayIndex(now)])
			{
				_ = RunSchedule(item);
			}
		}
	}

	/// <summary>Otomasyon tetiklendiğinde ampul bağlı değilse hatırlanan ampule bağlanır.</summary>
	private async Task<bool> EnsureConnectedAsync()
	{
		if (ble.Ready) return true;
		for (int i = 0; i < 20 && busy; i++) await Task.Delay(500);
		if (ble.Ready) return true;
		if (busy || Devices.SelectedItem is not Lamp lamp) return false;
		autoConnecting = true;
		try
		{
			await ConnectSelected(lamp);
		}
		finally
		{
			autoConnecting = false;
		}
		return ble.Ready;
	}

	private async Task RunWake(DateTime alarm)
	{
		Log("Uyandırma başlıyor: " + alarm.ToString("HH:mm"));
		if (!await EnsureConnectedAsync()) return;
		wakeStart = DateTime.Now;
		wakeDuration = alarm - wakeStart;
		if (wakeDuration < TimeSpan.FromMinutes(1)) wakeDuration = TimeSpan.FromMinutes(1);
		activeSceneId = null;
		await StartMode("Uyandırma");
	}

	private async Task RunSchedule(ScheduleItem item)
	{
		Log($"Program: {item.Time} {item.Action}");
		if (!await EnsureConnectedAsync()) return;
		switch (item.Action)
		{
			case "Aç":
				await SetPower(true);
				break;
			case "Kapat":
				await SetPower(false);
				break;
			default:
				if (item.SceneId != null) await ApplySceneById(item.SceneId);
				break;
		}
		if (!IsVisible) tray?.Notify("Program", $"{item.Time} • {ScheduleLabel(item)}");
	}

	// =====================================================================
	//  Uyku sayacı
	// =====================================================================

	internal void StartSleepTimer(double minutes)
	{
		sleepFadeStarted = false;
		if (minutes <= 0)
		{
			settings.SleepUntil = null;
			if (modeName == "Uyku") _ = StopModeAsync();
			DetailText.Text = L.T("sleep_off_msg");
		}
		else
		{
			settings.SleepMinutes = minutes;
			settings.SleepUntil = DateTime.Now.AddMinutes(minutes);
			DetailText.Text = L.F("sleep_set_msg", minutes);
		}
		SaveSettings();
		UpdateTimerUi();
		RaiseLightState();
	}

	private async Task BeginSleepFade(DateTime until)
	{
		if (!await EnsureConnectedAsync())
		{
			return;
		}
		if (powerOn == false)
		{
			settings.SleepUntil = null;
			SaveSettings();
			UpdateTimerUi();
			return;
		}
		sleepFadeFrom = Math.Max(1, CurrentBrightness);
		sleepFadeStart = DateTime.Now;
		sleepFadeEnd = until > sleepFadeStart.AddSeconds(5) ? until : sleepFadeStart.AddSeconds(5);
		activeSceneId = null;
		await StartMode("Uyku");
	}

	private void SleepPreset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string t } && double.TryParse(t, out double m)) StartSleepTimer(m);
	}

	private void SleepCustom_Click(object sender, RoutedEventArgs e)
	{
		if (double.TryParse(SleepCustom.Text.Trim(), out double m) && m >= 1 && m <= 600) StartSleepTimer(Math.Round(m));
		else SleepCustom.Text = "90";
	}

	private void SleepCustom_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) SleepCustom_Click(sender, e);
	}

	private void SleepCancel_Click(object sender, RoutedEventArgs e) => StartSleepTimer(0);

	// =====================================================================
	//  Uyandırma ayarları
	// =====================================================================

	private void Wake_Changed(object sender, RoutedEventArgs e)
	{
		if (!initialized) return;
		if (Daylight.TryParseTime(WakeTime.Text, out TimeSpan t))
		{
			settings.Wake.Time = $"{t.Hours:00}:{t.Minutes:00}";
		}
		WakeTime.Text = settings.Wake.Time;
		settings.Wake.Enabled = WakeToggle.IsChecked == true;
		SaveSettings();
		UpdateTimerUi();
	}

	private void WakeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!initialized || silentSliders > 0) return;
		settings.Wake.FadeMinutes = WakeFade.Value;
		settings.Wake.TargetBrightness = WakeBri.Value;
		SaveSettings();
		UpdateTimerUi();
	}

	private void TimeBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && sender is UIElement u)
		{
			u.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
			e.Handled = true;
		}
	}

	/// <summary>Gün doğumu rengi: koyu kızıl → turuncu → amber → sıcak beyaz → hedef beyaz.</summary>
	internal static Color SunriseColor(double t, double targetKelvin)
	{
		(double At, Color C)[] stops =
		{
			(0.00, Color.FromRgb(0x8B, 0x1A, 0x00)),
			(0.30, Color.FromRgb(0xFF, 0x4D, 0x00)),
			(0.55, Color.FromRgb(0xFF, 0x9A, 0x3C)),
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

	// =====================================================================
	//  Programlar
	// =====================================================================

	private string ScheduleLabel(ScheduleItem s) => s.Action switch
	{
		"Aç" => L.T("act_on"),
		"Kapat" => L.T("act_off"),
		_ => Scenes.FirstOrDefault(x => x.Id == s.SceneId)?.DisplayName ?? L.T("no_scene"),
	};

	private void ScheduleAdd_Click(object sender, RoutedEventArgs e)
	{
		settings.Schedules.Add(new ScheduleItem());
		SaveSettings();
		RebuildSchedules();
		UpdateTimerUi();
	}

	private void RebuildSchedules()
	{
		ScheduleList.Children.Clear();
		foreach (ScheduleItem item in settings.Schedules)
		{
			ScheduleList.Children.Add(MakeScheduleRow(item));
		}
		ScheduleEmpty.Visibility = settings.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private FrameworkElement MakeScheduleRow(ScheduleItem item)
	{
		Border row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 8, 6), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
		row.SetResourceReference(Border.BackgroundProperty, "Control");
		row.SetResourceReference(Border.BorderBrushProperty, "Stroke");
		StackPanel stack = new StackPanel();
		DockPanel top = new DockPanel();
		Button del = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("delete") };
		del.SetResourceReference(StyleProperty, "Small");
		DockPanel.SetDock(del, Dock.Right);
		del.Click += (_, _) => { settings.Schedules.Remove(item); SaveSettings(); RebuildSchedules(); UpdateTimerUi(); };
		top.Children.Add(del);
		CheckBox on = new CheckBox { IsChecked = item.Enabled, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("enabled") };
		on.Click += (_, _) => { item.Enabled = on.IsChecked == true; SaveSettings(); UpdateTimerUi(); };
		top.Children.Add(on);
		TextBox time = new TextBox { Text = item.Time, Width = 78, MaxLength = 5, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
		time.LostFocus += (_, _) =>
		{
			if (Daylight.TryParseTime(time.Text, out TimeSpan t)) item.Time = $"{t.Hours:00}:{t.Minutes:00}";
			time.Text = item.Time;
			SaveSettings();
		};
		time.KeyDown += TimeBox_KeyDown;
		top.Children.Add(time);
		ComboBox action = new ComboBox { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
		List<(string Action, string? Scene, string Label)> options = new List<(string, string?, string)> { ("Aç", null, L.T("act_on")), ("Kapat", null, L.T("act_off")) };
		options.AddRange(Scenes.Select(s => ("Sahne", (string?)s.Id, L.F("scene_option", s.DisplayName))));
		action.ItemsSource = options.Select(o => o.Label).ToList();
		int sel = options.FindIndex(o => o.Action == item.Action && (o.Action != "Sahne" || o.Scene == item.SceneId));
		action.SelectedIndex = sel >= 0 ? sel : 1;
		action.SelectionChanged += (_, _) =>
		{
			if (action.SelectedIndex < 0) return;
			(string a, string? s, string _) = options[action.SelectedIndex];
			item.Action = a;
			item.SceneId = s;
			SaveSettings();
		};
		top.Children.Add(action);
		stack.Children.Add(top);
		WrapPanel days = new WrapPanel { Margin = new Thickness(26, 8, 0, 0) };
		for (int i = 0; i < 7; i++)
		{
			int d = i;
			ToggleButton t = new ToggleButton { Content = DayShort[i], IsChecked = item.Days[i], ToolTip = DayLong[i], Height = 24, MinWidth = 30 };
			t.SetResourceReference(StyleProperty, "Chip");
			t.Click += (_, _) => { item.Days[d] = t.IsChecked == true; SaveSettings(); };
			days.Children.Add(t);
		}
		stack.Children.Add(days);
		row.Child = stack;
		return row;
	}

	// =====================================================================
	//  Gün ışığı
	// =====================================================================

	private void LoadDaylightTimes()
	{
		DayWake.Text = settings.Daylight.WakeTime;
		DaySleep.Text = settings.Daylight.SleepTime;
	}

	private void DaylightTime_Commit(object sender, RoutedEventArgs e)
	{
		if (Daylight.TryParseTime(DayWake.Text, out TimeSpan w)) settings.Daylight.WakeTime = $"{w.Hours:00}:{w.Minutes:00}";
		if (Daylight.TryParseTime(DaySleep.Text, out TimeSpan s)) settings.Daylight.SleepTime = $"{s.Hours:00}:{s.Minutes:00}";
		LoadDaylightTimes();
		UpdateDaylightBar();
		SaveSettings();
	}

	private void Daylight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (initialized) UpdateDaylightBar();
	}

	private void UpdateDaylightBar()
	{
		LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
		DaylightOptions o = settings.Daylight;
		for (int i = 0; i <= 48; i++)
		{
			(double k, double b) = Daylight.At(o, DateTime.Today.AddMinutes(i * 30));
			Color c = ColorMath.FromKelvin(k);
			double f = 0.25 + 0.75 * b / 100.0;
			g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)), i / 48.0));
		}
		g.Freeze();
		DaylightBar.Background = g;
		double w = DaylightCanvas.ActualWidth > 0 ? DaylightCanvas.ActualWidth : 400;
		Canvas.SetLeft(DaylightNow, DateTime.Now.TimeOfDay.TotalHours / 24.0 * w - 1.5);
	}

	// =====================================================================
	//  Spotify
	// =====================================================================

	private void SpotifyOnly_Click(object sender, RoutedEventArgs e) => UpdateSpotifySubtitle();

	private void UpdateSpotifySubtitle()
	{
		SpotifySubtitle.Text = settings.Spotify.OnlySpotify
			? L.T("spotify_desc")
			: L.T("spotify_desc_any");
	}

	private void ShowNowPlaying(MediaColorReader.Track? t, string? error = null)
	{
		if (t == null)
		{
			NowPlayingTitle.Text = error ?? L.T("nothing_playing");
			NowPlayingArtist.Text = error == null ? (settings.Spotify.OnlySpotify ? L.T("start_spotify") : L.T("start_player")) : L.T("media_session_needed");
			NowPlayingChip.Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
			return;
		}
		NowPlayingTitle.Text = t.Title.Length > 0 ? t.Title : L.T("unknown_track");
		string app = MediaColorReader.IsSpotify(t.App) ? "Spotify" : t.App.Split('!')[0].Split('\\').Last().Replace(".exe", "");
		NowPlayingArtist.Text = (t.Artist.Length > 0 ? t.Artist + " • " : "") + (t.Playing ? app : L.F("paused_suffix", app));
		if (t.Color is Color c) NowPlayingChip.Background = new SolidColorBrush(c);
	}

	// =====================================================================
	//  Bildirim ışığı
	// =====================================================================

	private async void NotifyToggle_Click(object sender, RoutedEventArgs e)
	{
		settings.Notify.Enabled = NotifyToggle.IsChecked == true;
		SaveSettings();
		if (settings.Notify.Enabled) await StartNotifier();
		else
		{
			notifier?.Stop();
			NotifyStatus.Text = L.T("off");
		}
	}

	private async Task StartNotifier()
	{
		if (!persistent)
		{
			NotifyStatus.Text = L.T("notify_listening_demo");
			return;
		}
		if (notifier == null)
		{
			notifier = new NotificationWatcher(Log);
			notifier.Arrived += app => Dispatcher.BeginInvoke((Action)(() => OnNotification(app)));
		}
		NotifyStatus.Text = L.T("starting");
		await notifier.StartAsync();
		NotifyStatus.Text = NotifyStatusText();
	}

	/// <summary>Bildirim izleyicisinin durumu, arayüz dilinde.</summary>
	private string NotifyStatusText()
	{
		if (notifier == null) return settings.Notify.Enabled && !persistent ? L.T("notify_listening_demo") : L.T("off");
		string status = L.FromTr(notifier.Status);
		return notifier.Method.Length > 0 ? $"{status} • {L.FromTr(notifier.Method)}" : status;
	}

	private void NotifyColor_Changed(object sender, RoutedEventArgs e)
	{
		if (ColorMath.TryParseHex(NotifyHex.Text, out Color c)) settings.Notify.Color = ColorMath.Hex(c);
		NotifyHex.Text = settings.Notify.Color;
		UpdateNotifyChip();
		SaveSettings();
	}

	private void UpdateNotifyChip()
	{
		ColorMath.TryParseHex(settings.Notify.Color, out Color c);
		NotifyChip.Background = new SolidColorBrush(c);
		NotifyHex.IsEnabled = true;
	}

	private void OnNotification(string appId)
	{
		if (closed || !settings.Notify.Enabled || !ble.Ready || busy) return;
		string lower = appId.ToLowerInvariant();
		if (lower.Contains("chiaro") || lower.Contains("huestudio") || lower.Contains("huebluetooth")) return;
		string app = NotificationWatcher.Classify(appId);
		NotifyOptions o = settings.Notify;
		bool allowed = app switch
		{
			"Discord" => o.Discord,
			"WhatsApp" => o.WhatsApp,
			"Teams" => o.Teams,
			"Outlook" => o.Outlook,
			_ => o.Others,
		};
		if (!allowed || (DateTime.Now - lastFlash).TotalSeconds < 2) return;
		string hex = o.UseAppColor && app != "Diğer" ? NotificationWatcher.AppColor(app) : o.Color;
		ColorMath.TryParseHex(hex, out Color c);
		Log("Bildirim: " + app);
		NotifyStatus.Text = L.F("last_notify", app == "Diğer" ? L.T("nw_other") : app, DateTime.Now);
		_ = FlashAsync(c, (int)Math.Round(o.Count), o.Restore);
	}

	private async void NotifyTest_Click(object sender, RoutedEventArgs e)
	{
		if (!ble.Ready)
		{
			NotifyStatus.Text = L.T("connect_first");
			return;
		}
		string hex = settings.Notify.UseAppColor ? NotificationWatcher.AppColor("Discord") : settings.Notify.Color;
		ColorMath.TryParseHex(hex, out Color c);
		await FlashAsync(c, (int)Math.Round(settings.Notify.Count), settings.Notify.Restore);
	}

	/// <summary>Işığı kısa süre yakıp söndürür; aktif modu bozmaz, sonra önceki ışığa döner.</summary>
	internal async Task FlashAsync(Color color, int count, bool restore)
	{
		if (flashing || !ble.Ready || busy) return;
		flashing = true;
		lastFlash = DateTime.Now;
		if (!await lightGate.WaitAsync(2500))
		{
			flashing = false;
			return;
		}
		bool wasOn = powerOn == true;
		Color prevColor = currentColor;
		bool prevKnown = colorKnown;
		double prevBri = CurrentBrightness;
		bool prevWhite = lastWasWhite;
		double prevK = TemperatureSlider.Value;
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			CancellationToken ct = cts.Token;
			bool hasBri = ble.Supports(3), hasColor = ble.Supports(5);
			if (hasColor) await ble.Write(5, Protocol.Rgb(color.R, color.G, color.B), ct);
			if (!wasOn) await ble.Write(2, Protocol.Power(on: true), ct);
			for (int i = 0; i < Math.Clamp(count, 1, 5); i++)
			{
				if (hasBri)
				{
					await ble.Write(3, Protocol.Brightness(100), ct);
					await Task.Delay(260, ct);
					await ble.Write(3, Protocol.Brightness(12), ct);
					await Task.Delay(220, ct);
				}
				else
				{
					await ble.Write(2, Protocol.Power(on: true), ct);
					await Task.Delay(300, ct);
					await ble.Write(2, Protocol.Power(on: false), ct);
					await Task.Delay(250, ct);
				}
			}
			if (restore)
			{
				if (hasBri) await ble.Write(3, Protocol.Brightness(prevBri), ct);
				if (prevWhite && ble.Supports(4)) await ble.Write(4, Protocol.Temperature(prevK), ct);
				else if (prevKnown && hasColor) await ble.Write(5, Protocol.Rgb(prevColor.R, prevColor.G, prevColor.B), ct);
				await ble.Write(2, Protocol.Power(wasOn), ct);
				powerOn = wasOn;
			}
			else
			{
				if (hasBri) await ble.Write(3, Protocol.Brightness(100), ct);
				await ble.Write(2, Protocol.Power(on: true), ct);
				powerOn = true;
				currentColor = color;
				colorKnown = true;
				lastWasWhite = false;
			}
			liveLast.Clear();
		}
		catch (Exception ex)
		{
			Log("Bildirim ışığı: " + ex.Message);
		}
		finally
		{
			lightGate.Release();
			flashing = false;
			UpdateBulbVisual(false, 200);
			UpdateControls();
		}
	}
}
