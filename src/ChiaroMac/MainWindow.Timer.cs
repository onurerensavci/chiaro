#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace HueStudio;

public partial class MainWindow
{
	private DispatcherTimer? clock;
	private bool sleepFadeStarted;
	private string lastMinuteKey = "";
	private readonly List<ToggleButton> wakeDayButtons = new List<ToggleButton>();

	private static int DayIndex(DateTime d) => ((int)d.DayOfWeek + 6) % 7; // Pazartesi = 0

	private void InitTimers()
	{
		InitWheel();
		SleepMinutesBox.Text = settings.SleepMinutes.ToString("F0", CultureInfo.InvariantCulture);
		WakeEnabled.IsChecked = settings.Wake.Enabled;
		WakeTime.Text = settings.Wake.Time;
		for (int i = 0; i < 7; i++)
		{
			int d = i;
			ToggleButton t = new ToggleButton { Classes = { "day" }, IsChecked = settings.Wake.Days[i] };
			t.Click += (_, _) => { settings.Wake.Days[d] = t.IsChecked == true; SaveSettings(); UpdateTimerUi(); };
			wakeDayButtons.Add(t);
			WakeDays.Children.Add(t);
		}
		Bind(WakeFade, () => settings.Wake.FadeMinutes, v => settings.Wake.FadeMinutes = Math.Round(v), WakeFadeLabel, v => L.F("fmt_min", v));
		Bind(WakeBri, () => settings.Wake.TargetBrightness, v => settings.Wake.TargetBrightness = Math.Round(v), WakeBriLabel, v => L.Pct(v));
		foreach (Action a in optionLoaders) a();
		RebuildSchedules();
		clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		clock.Tick += (_, _) => ClockTick();
		clock.Start();
		UpdateTimerUi();
	}

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
		}
		UpdateTimerUi();
	}

	private void UpdateTimerUi()
	{
		string? remaining = settings.SleepUntil is DateTime u ? Remaining(u) : null;
		SleepRemaining.Text = remaining ?? "";
		string badge;
		if (remaining != null) badge = L.F("badge_sleep", remaining);
		else if (settings.Wake.Enabled && NextWake() is DateTime w) badge = L.F("badge_alarm", w.ToString("HH:mm"));
		else if (settings.Schedules.Any(s => s.Enabled)) badge = L.F("badge_programs", settings.Schedules.Count(s => s.Enabled));
		else badge = L.T("off_upper");
		TimerBadge.IsVisible = true;
		TimerBadgeText.Text = badge;
		string[] shortDays = L.DaysShort;
		for (int i = 0; i < wakeDayButtons.Count && i < shortDays.Length; i++) wakeDayButtons[i].Content = shortDays[i];
		if (settings.Wake.Enabled && NextWake() is DateTime next)
		{
			DateTime start = next - TimeSpan.FromMinutes(settings.Wake.FadeMinutes);
			string day = next.Date == DateTime.Today ? L.T("today") : next.Date == DateTime.Today.AddDays(1) ? L.T("tomorrow") : L.DaysLong[DayIndex(next)];
			WakeNext.Text = L.F("wake_next", day, next, start);
		}
		else
		{
			WakeNext.Text = settings.Wake.Enabled ? L.T("no_days") : "";
		}
		RebuildTrayStatus();
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
		if (settings.Wake.Enabled && Daylight.TryParseTime(settings.Wake.Time, out TimeSpan wt))
		{
			foreach (DateTime alarm in new[] { now.Date + wt, now.Date.AddDays(1) + wt })
			{
				DateTime start = alarm - TimeSpan.FromMinutes(settings.Wake.FadeMinutes);
				if (start.ToString("yyyyMMddHHmm") == now.ToString("yyyyMMddHHmm") && settings.Wake.Days[DayIndex(alarm)]) _ = RunWake(alarm);
			}
		}
		foreach (ScheduleItem item in settings.Schedules.ToList())
		{
			if (!item.Enabled || !Daylight.TryParseTime(item.Time, out TimeSpan st)) continue;
			if (st.Hours == now.Hour && st.Minutes == now.Minute && item.Days[DayIndex(now)]) _ = RunSchedule(item);
		}
	}

	private async Task<bool> EnsureConnectedAsync()
	{
		if (ble.Ready) return true;
		for (int i = 0; i < 20 && busy; i++) await Task.Delay(500);
		if (ble.Ready) return true;
		if (busy || Devices.SelectedItem is not Lamp lamp) return false;
		try
		{
			await ConnectSelected(lamp);
		}
		finally
		{
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
			case "Aç": await SetPower(true); break;
			case "Kapat": await SetPower(false); break;
			default:
				if (item.SceneId != null) await ApplySceneById(item.SceneId);
				break;
		}
	}

	// ---- uyku sayacı

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
			SleepMinutesBox.Text = minutes.ToString("F0", CultureInfo.InvariantCulture);
			DetailText.Text = L.F("sleep_set_msg", minutes);
		}
		SaveSettings();
		UpdateTimerUi();
	}

	private async Task BeginSleepFade(DateTime until)
	{
		if (!await EnsureConnectedAsync()) return;
		if (powerOn == false)
		{
			settings.SleepUntil = null;
			SaveSettings();
			UpdateTimerUi();
			return;
		}
		sleepFadeFrom = Math.Max(1, liveBrightness ?? BrightnessSlider.Value);
		sleepFadeStart = DateTime.Now;
		sleepFadeEnd = until > sleepFadeStart.AddSeconds(5) ? until : sleepFadeStart.AddSeconds(5);
		activeSceneId = null;
		await StartMode("Uyku");
	}

	private void SleepPreset_Click(object? sender, RoutedEventArgs e)
	{
		if (sender is Control { Tag: string t } && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double m)) StartSleepTimer(m);
	}

	private void SleepStart_Click(object? sender, RoutedEventArgs e)
	{
		if (double.TryParse(SleepMinutesBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m >= 1 && m <= 600) StartSleepTimer(Math.Round(m));
		else SleepMinutesBox.Text = settings.SleepMinutes.ToString("F0", CultureInfo.InvariantCulture);
	}

	private void SleepCancel_Click(object? sender, RoutedEventArgs e) => StartSleepTimer(0);

	// ---- uyandırma

	private void WakeEnabled_Click(object? sender, RoutedEventArgs e)
	{
		settings.Wake.Enabled = WakeEnabled.IsChecked == true;
		SaveSettings();
		UpdateTimerUi();
	}

	private void WakeTime_LostFocus(object? sender, RoutedEventArgs e)
	{
		if (Daylight.TryParseTime(WakeTime.Text, out TimeSpan t)) settings.Wake.Time = $"{t.Hours:00}:{t.Minutes:00}";
		WakeTime.Text = settings.Wake.Time;
		SaveSettings();
		UpdateTimerUi();
	}

	// ---- programlar

	private string ScheduleLabel(ScheduleItem s) => s.Action switch
	{
		"Aç" => L.T("act_on"),
		"Kapat" => L.T("act_off"),
		_ => Scenes.FirstOrDefault(x => x.Id == s.SceneId)?.DisplayName ?? L.T("no_scene"),
	};

	private void AddSchedule_Click(object? sender, RoutedEventArgs e)
	{
		settings.Schedules.Add(new ScheduleItem());
		SaveSettings();
		RebuildSchedules();
		UpdateTimerUi();
	}

	private void RebuildSchedules()
	{
		SchedulesHost.Children.Clear();
		foreach (ScheduleItem item in settings.Schedules) SchedulesHost.Children.Add(MakeScheduleRow(item));
		SchedulesEmpty.IsVisible = settings.Schedules.Count == 0;
	}

	private Control MakeScheduleRow(ScheduleItem item)
	{
		Border row = new Border { Classes = { "inner" }, Padding = new Thickness(10, 8) };
		StackPanel stack = new StackPanel { Spacing = 6 };
		DockPanel top = new DockPanel();
		Button del = new Button { Classes = { "pill" }, Content = "✕", Padding = new Thickness(8, 2), Margin = new Thickness(6, 0, 0, 0) };
		ToolTip.SetTip(del, L.T("delete"));
		DockPanel.SetDock(del, Dock.Right);
		del.Click += (_, _) => { settings.Schedules.Remove(item); SaveSettings(); RebuildSchedules(); UpdateTimerUi(); };
		top.Children.Add(del);
		CheckBox on = new CheckBox { IsChecked = item.Enabled, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
		ToolTip.SetTip(on, L.T("enabled"));
		on.Click += (_, _) => { item.Enabled = on.IsChecked == true; SaveSettings(); UpdateTimerUi(); };
		top.Children.Add(on);
		TextBox time = new TextBox { Text = item.Time, Width = 70, MaxLength = 5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
		time.LostFocus += (_, _) =>
		{
			if (Daylight.TryParseTime(time.Text, out TimeSpan t)) item.Time = $"{t.Hours:00}:{t.Minutes:00}";
			time.Text = item.Time;
			SaveSettings();
		};
		top.Children.Add(time);
		ComboBox action = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
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
		StackPanel days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0) };
		string[] shortDays = L.DaysShort, longDays = L.DaysLong;
		for (int i = 0; i < 7; i++)
		{
			int d = i;
			ToggleButton t = new ToggleButton { Classes = { "day" }, Content = shortDays[i], IsChecked = item.Days[i] };
			ToolTip.SetTip(t, longDays[i]);
			t.Click += (_, _) => { item.Days[d] = t.IsChecked == true; SaveSettings(); };
			days.Children.Add(t);
		}
		stack.Children.Add(days);
		row.Child = stack;
		return row;
	}
}
