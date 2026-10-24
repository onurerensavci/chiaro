#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HueStudio;

/// <summary>
/// Işık kontrol katmanı: tek "sahip" kuralı, canlı kaydırıcı yazımları, sahneler,
/// hızlı panel, mini mod ve simgede tekerlek.
///
/// Sahiplik: Işığı aynı anda yalnızca bir otomasyon sürer (modeName / modeCancellation:
/// Ekran, Ses, Spotify, Gökkuşağı, Şömine, Gün ışığı, ayrıca iç modlar Uyku ve Uyandırma).
/// Elle yapılan her komut (Run) önce aktif modu durdurur. Canlı kaydırıcılar yalnızca
/// çakışan modu durdurur (ör. gökkuşağı sürerken parlaklık değiştirmek modu bozmaz).
/// Bildirim ışığı sahipliği almaz; lightGate ile kısa süre araya girer ve önceki ışığı geri yükler.
/// Tüm BLE yazımları lightGate altında yapılır, böylece iki yazım asla çakışmaz.
/// </summary>
public partial class MainWindow
{
	internal static readonly string[] UserModes = { "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" };

	private readonly SemaphoreSlim lightGate = new SemaphoreSlim(1, 1);
	private readonly SortedDictionary<int, byte[]> livePending = new SortedDictionary<int, byte[]>();
	private readonly Dictionary<int, byte[]> liveLast = new Dictionary<int, byte[]>();
	private DispatcherTimer? liveTimer;
	private DateTime lastLiveWrite = DateTime.MinValue;
	private bool liveFlushing;
	private bool liveCommitPending;
	private int silentSliders;
	private bool lastWasWhite;
	private string? activeSceneId;

	internal event Action? LightStateChanged;
	internal event Action? ScenesChanged;

	// ---- Paneller için durum ----
	private bool demo;
	internal bool IsReady => ble.Ready || demo;
	internal bool? PowerState => IsReady ? powerOn : null;
	internal double CurrentBrightness => liveBrightness ?? BrightnessSlider.Value;
	internal Color LiveColor => currentColor;
	internal bool HasColor => colorKnown;
	internal string LampLabel => IsReady ? connectedName : (Devices.SelectedItem as Lamp)?.Display ?? "Hue";
	internal IReadOnlyList<Scene> SceneList => Scenes;
	internal IEnumerable<Color> FavoriteColors => settings.Favorites.Select(h => ColorMath.TryParseHex(h, out Color c) ? c : Colors.White);
	internal bool SleepActive => settings.SleepUntil.HasValue;

	private List<Scene> Scenes
	{
		get
		{
			if (settings.Scenes == null)
			{
				settings.Scenes = Scene.Defaults();
				settings.ScenesVersion = 2;
			}
			return settings.Scenes;
		}
	}

	internal string StatusLine()
	{
		if (busy) return L.T("busy");
		if (!IsReady) return L.T("not_connected");
		string p = powerOn == true ? L.F("on_pct", L.Pct(CurrentBrightness)) : powerOn == false ? L.T("off") : L.T("connected");
		return p;
	}

	internal string? LiveLine()
	{
		List<string> parts = new List<string>();
		if (modeName.Length > 0)
		{
			Scene? s = activeSceneId == null ? null : Scenes.FirstOrDefault(x => x.Id == activeSceneId);
			string label = modeName switch { "Uyku" => L.T("sleep_dimming"), "Uyandırma" => L.Mode("Uyandırma"), _ => L.F("mode_live", L.Mode(modeName)) };
			parts.Add(s != null && s.Kind == "Mod" ? s.DisplayName + " • " + label : label);
		}
		if (settings.SleepUntil is DateTime until && modeName != "Uyku")
		{
			parts.Add(L.F("sleep_remaining", Remaining(until)));
		}
		return parts.Count == 0 ? null : string.Join("  ·  ", parts);
	}

	private static string Remaining(DateTime until)
	{
		TimeSpan r = until - DateTime.Now;
		if (r < TimeSpan.Zero) r = TimeSpan.Zero;
		return r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes:00}:{r.Seconds:00}";
	}

	private void RaiseLightState() => LightStateChanged?.Invoke();

	// =====================================================================
	//  Mod gereksinimleri ve çakışma
	// =====================================================================

	private bool ModeSupported(string mode) => mode switch
	{
		"Ses" => ble.Supports(3),
		"Gün ışığı" => ble.Supports(4) || ble.Supports(5),
		"Uyku" => ble.Supports(2),
		"Uyandırma" => ble.Supports(2) && (ble.Supports(3) || ble.Supports(5)),
		_ => ble.Supports(5),
	};

	private string UnsupportedText(string mode) => mode switch
	{
		"Ses" => L.T("no_brightness_ctl"),
		"Gün ışığı" => L.T("no_white_ctl"),
		_ => L.T("no_color_ctl"),
	};

	/// <summary>Elle yapılan bir kaydırıcı değişikliği aktif modla çakışıyor mu?</summary>
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
				_ => false, // Spotify: parlaklık seçeneği güncellenir
			};
		}
		return modeName != "Ses";
	}

	// =====================================================================
	//  Canlı kaydırıcılar: ~100 ms'de bir en son değer + bırakınca son yazım
	// =====================================================================

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

	/// <summary>Parlaklık (kod 3), beyaz tonu (4) veya renk (5) için canlı yazım.</summary>
	internal async void LiveSet(int code, byte[] value, bool final)
	{
		if (closed || !ble.Ready || !ble.Supports(code)) return;
		activeSceneId = null;
		if (code == 3 && modeName == "Spotify")
		{
			settings.Spotify.Brightness = Math.Round(value[0] * 100.0 / 254.0);
		}
		if (ModeConflicts(code))
		{
			await StopModeAsync();
		}
		if (final && (code == 4 || code == 5) && modeName.Length == 0) ClearLastMode("sabit renk/beyaz verildi.");
		livePending[code] = value;
		if (final) liveCommitPending = true;
		liveTimer ??= CreateLiveTimer();
		if (!liveTimer.IsEnabled) liveTimer.Start();
		if (final || (DateTime.Now - lastLiveWrite).TotalMilliseconds >= 100)
		{
			await FlushLive();
		}
	}

	private DispatcherTimer CreateLiveTimer()
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
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
					AfterLiveWrite(next.Key, next.Value);
				}
				catch (Exception ex)
				{
					Log("Canlı yazım: " + ex.Message);
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

	private void AfterLiveWrite(int code, byte[] value)
	{
		if (code == 3)
		{
			liveBrightness = null;
		}
		else if (code == 4)
		{
			lastWasWhite = true;
		}
		else if (code == 5)
		{
			lastWasWhite = false;
		}
		UpdateBulbVisual(false, 160);
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
		UpdateBulbVisual(false, 120);
		LiveSet(3, Protocol.Brightness(percent), final);
	}

	private void LiveColorSet(Color c, bool final)
	{
		currentColor = c;
		colorKnown = true;
		LiveSet(5, Protocol.Rgb(c.R, c.G, c.B), final);
	}

	// Ampul bölümündeki kaydırıcılar (Uygula düğmeleri kaldırıldı)
	private void BrightnessSlider_Release(object sender, MouseButtonEventArgs e)
	{
		if (BrightnessPanel.IsEnabled) LiveBrightness(BrightnessSlider.Value, final: true);
	}

	private void BrightnessSlider_KeyUp(object sender, KeyEventArgs e)
	{
		if (BrightnessPanel.IsEnabled) LiveBrightness(BrightnessSlider.Value, final: true);
	}

	private void TemperatureSlider_Release(object sender, MouseButtonEventArgs e) => TemperatureLive(final: true);

	private void TemperatureSlider_KeyUp(object sender, KeyEventArgs e) => TemperatureLive(final: true);

	private void TemperatureLive(bool final)
	{
		if (!TemperaturePanel.IsEnabled) return;
		double k = TemperatureSlider.Value;
		currentColor = ColorMath.FromKelvin(k);
		colorKnown = true;
		LiveSet(4, Protocol.Temperature(k), final);
	}

	private void SatSlider_Release(object sender, MouseButtonEventArgs e)
	{
		if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: true);
	}

	private void SatSlider_KeyUp(object sender, KeyEventArgs e)
	{
		if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: true);
	}

	// =====================================================================
	//  Sahneler
	// =====================================================================

	private void RebuildScenes()
	{
		SceneTiles.Children.Clear();
		foreach (Scene s in Scenes)
		{
			SceneTiles.Children.Add(MakeSceneTile(s));
		}
		tray?.SetScenes(Scenes);
		ScenesChanged?.Invoke();
	}

	private Button MakeSceneTile(Scene s)
	{
		Color a = s.Accent;
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		g.Children.Add(MakeSceneBadge(s, 34));
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new TextBlock { Text = s.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
		TextBlock sub = new TextBlock { Text = s.Subtitle, FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
		sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		text.Children.Add(sub);
		Grid.SetColumn(text, 1);
		g.Children.Add(text);
		bool modified = s.IsModified;
		if (modified)
		{
			// Düzenlenmiş hazır sahne: köşede küçük nokta.
			Ellipse mark = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -3, 0), ToolTip = L.T("scene_edited") };
			mark.SetResourceReference(Shape.FillProperty, "AccentSoft");
			Grid.SetColumn(mark, 1);
			g.Children.Add(mark);
		}
		Button b = new Button
		{
			Content = g,
			Width = 160,
			Height = 62,
			Margin = new Thickness(0, 0, 10, 10),
			Padding = new Thickness(10, 6, 10, 6),
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			ToolTip = modified ? L.F("scene_edited_suffix", s.Subtitle) : s.Subtitle,
		};
		if (s.Id == activeSceneId)
		{
			b.SetResourceReference(BorderBrushProperty, "Accent");
		}
		b.Click += async (_, _) => await ApplySceneAsync(s);
		ContextMenu menu = new ContextMenu();
		void Add(string header, Action act, bool enabled = true)
		{
			MenuItem mi = new MenuItem { Header = header, IsEnabled = enabled };
			mi.Click += (_, _) => act();
			menu.Items.Add(mi);
		}
		int index = Scenes.IndexOf(s);
		Add(L.T("apply"), () => _ = ApplySceneAsync(s));
		menu.Items.Add(new Separator());
		Add(L.T("rename_dots"), () => RenameScene(s));
		Add(L.T("change_emoji"), () => ChangeSceneEmoji(s));
		Add(L.T("update_from_current"), () => UpdateSceneFromCurrent(s));
		if (modified) Add(L.T("reset_default"), () => ResetScene(s));
		menu.Items.Add(new Separator());
		Add(L.T("move_up"), () => MoveScene(s, -1), index > 0);
		Add(L.T("move_down"), () => MoveScene(s, +1), index < Scenes.Count - 1);
		menu.Items.Add(new Separator());
		Add(L.T("delete"), () => DeleteScene(s));
		b.ContextMenu = menu;
		return b;
	}

	/// <summary>Sahne rozeti: rengin hafif tonunda yuvarlak, içinde emoji (yoksa renk noktası).</summary>
	internal static FrameworkElement MakeSceneBadge(Scene s, double size)
	{
		Color a = s.Accent;
		Grid dot = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
		if (string.IsNullOrWhiteSpace(s.Emoji))
		{
			dot.Children.Add(new Ellipse { Margin = new Thickness(-5), Fill = new RadialGradientBrush(Color.FromArgb(150, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B)) });
			dot.Children.Add(new Ellipse { Margin = new Thickness(size * 0.1), Fill = new SolidColorBrush(a), Stroke = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255)), StrokeThickness = 1 });
			return dot;
		}
		dot.Children.Add(new Ellipse
		{
			Fill = new RadialGradientBrush(Color.FromArgb(120, a.R, a.G, a.B), Color.FromArgb(40, a.R, a.G, a.B)) { GradientOrigin = new Point(0.35, 0.3) },
			Stroke = new SolidColorBrush(Color.FromArgb(110, a.R, a.G, a.B)),
			StrokeThickness = 1,
		});
		FrameworkElement e = EmojiArt.View(s.Emoji, size * 0.58);
		e.HorizontalAlignment = HorizontalAlignment.Center;
		e.VerticalAlignment = VerticalAlignment.Center;
		dot.Children.Add(e);
		return dot;
	}

	private Scene CaptureScene(string name)
	{
		Scene s = new Scene { Name = name, Brightness = Math.Round(BrightnessSlider.Value) };
		if (modeName.Length > 0 && UserModes.Contains(modeName))
		{
			s.Kind = "Mod";
			s.Mode = modeName;
			s.ModeOptions = modeName switch
			{
				"Ekran" => JsonSerializer.Serialize(settings.Screen),
				"Ses" => JsonSerializer.Serialize(settings.Audio),
				"Spotify" => JsonSerializer.Serialize(settings.Spotify),
				"Gökkuşağı" => JsonSerializer.Serialize(settings.Rainbow),
				"Şömine" => JsonSerializer.Serialize(settings.Fire),
				_ => JsonSerializer.Serialize(settings.Daylight),
			};
		}
		else if (powerOn == false)
		{
			s.Kind = "Kapat";
		}
		else if (lastWasWhite)
		{
			s.Kind = "Beyaz";
			s.Kelvin = Math.Round(TemperatureSlider.Value);
		}
		else
		{
			s.Kind = "Renk";
			s.Color = ColorMath.Hex(colorKnown ? currentColor : customColor);
		}
		return s;
	}

	private void SceneSave_Click(object sender, RoutedEventArgs e)
	{
		var r = InputDialog.AskWithEmoji(this, L.T("scene_name"), L.F("scene_n", Scenes.Count + 1), "✨");
		if (r == null || string.IsNullOrWhiteSpace(r.Value.Name)) return;
		string name = r.Value.Name;
		Scene s = CaptureScene(name.Length > 24 ? name[..24] : name);
		s.Emoji = string.IsNullOrWhiteSpace(r.Value.Emoji) ? null : r.Value.Emoji;
		Scenes.Add(s);
		activeSceneId = s.Id;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("saved_x", s.DisplayName);
	}

	private void RenameScene(Scene s)
	{
		var r = InputDialog.AskWithEmoji(this, L.T("rename"), s.DisplayName, s.Emoji);
		if (r == null || string.IsNullOrWhiteSpace(r.Value.Name)) return;
		if (r.Value.Name != s.DisplayName) s.Name = r.Value.Name.Length > 24 ? r.Value.Name[..24] : r.Value.Name;
		s.Emoji = string.IsNullOrWhiteSpace(r.Value.Emoji) ? null : r.Value.Emoji;
		SaveSettings();
		RebuildScenes();
	}

	private void ChangeSceneEmoji(Scene s)
	{
		string? e = EmojiPicker.Ask(this, s.Emoji, L.F("emoji_for", s.DisplayName));
		if (e == null) return;
		s.Emoji = e.Length == 0 ? "" : e;
		SaveSettings();
		RebuildScenes();
	}

	private void UpdateSceneFromCurrent(Scene s)
	{
		// Hazır sahneler de üzerine yazılabilir; ad, emoji ve kimlik korunur (mod + mod ayarları dahil).
		Scene now = CaptureScene(s.Name);
		now.Id = s.Id;
		now.BuiltIn = s.BuiltIn;
		now.Emoji = s.Emoji;
		int i = Scenes.IndexOf(s);
		if (i >= 0) Scenes[i] = now;
		activeSceneId = now.Id;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("updated_x", s.DisplayName);
	}

	private void ResetScene(Scene s)
	{
		Scene? o = s.Original;
		if (o == null) return;
		int i = Scenes.IndexOf(s);
		if (i >= 0) Scenes[i] = o;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("reset_x", o.DisplayName);
	}

	private void MoveScene(Scene s, int delta)
	{
		int i = Scenes.IndexOf(s);
		int j = Math.Clamp(i + delta, 0, Scenes.Count - 1);
		if (i < 0 || i == j) return;
		Scenes.RemoveAt(i);
		Scenes.Insert(j, s);
		SaveSettings();
		RebuildScenes();
	}

	private void DeleteScene(Scene s)
	{
		Scenes.Remove(s);
		foreach (ScheduleItem item in settings.Schedules.Where(x => x.SceneId == s.Id))
		{
			item.SceneId = null;
		}
		SaveSettings();
		RebuildScenes();
		RebuildSchedules();
		DetailText.Text = L.F("deleted_x", s.DisplayName);
	}

	internal Task ApplySceneById(string id)
	{
		Scene? s = Scenes.FirstOrDefault(x => x.Id == id);
		return s == null ? Task.CompletedTask : ApplySceneAsync(s);
	}

	internal async Task ApplySceneAsync(Scene s)
	{
		if (!ble.Ready && !await EnsureConnectedAsync())
		{
			DetailText.Text = L.T("connect_first");
			return;
		}
		activeSceneId = s.Id;
		if (s.Kind == "Kapat")
		{
			await SetPower(false);
		}
		else if (s.Kind == "Mod" && s.Mode != null)
		{
			ApplyModeOptions(s.Mode, s.ModeOptions);
			if (!ModeSupported(s.Mode))
			{
				DetailText.Text = UnsupportedText(s.Mode);
				return;
			}
			await StartMode(s.Mode, s.Brightness);
		}
		else
		{
			bool white = s.Kind == "Beyaz";
			Color c = s.Accent;
			bool ok = await Run(s.DisplayName + "…", async ct =>
			{
				await ble.Write(2, Protocol.Power(on: true), ct);
				powerOn = true;
				if (ble.Supports(3)) await ble.Write(3, Protocol.Brightness(s.Brightness), ct);
				if (white && ble.Supports(4)) await ble.Write(4, Protocol.Temperature(s.Kelvin), ct);
				else if (ble.Supports(5)) await ble.Write(5, Protocol.Rgb(c.R, c.G, c.B), ct);
			});
			if (ok)
			{
				SetSilently(BrightnessSlider, Math.Clamp(s.Brightness, 1, 100));
				if (white) SetSilently(TemperatureSlider, Math.Clamp(s.Kelvin, 2200, 6500));
				lastWasWhite = white && ble.Supports(4);
				currentColor = c;
				colorKnown = true;
				liveLast.Clear();
				StatusText.Text = L.F("connected_to", connectedName);
				DetailText.Text = L.F("applied_x", s.DisplayName);
				StateText.Text = s.Subtitle;
				UpdateBulbVisual(true);
				UpdateControls();
			}
		}
		RebuildScenes();
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
			RebindModeOptions();
		}
		catch (Exception ex)
		{
			Log("Sahne seçenekleri: " + ex.Message);
		}
	}

	private void RebindModeOptions()
	{
		ClampSettings();
		ModeOptionsHost.DataContext = null;
		ModeOptionsHost.DataContext = settings;
		UpdateSpotifySubtitle();
		LoadDaylightTimes();
		UpdateRainbowBar();
		UpdateDaylightBar();
	}

	// =====================================================================
	//  Hızlı panel, mini mod, simgede tekerlek, OSD
	// =====================================================================

	private QuickPanel? quick;
	private MiniBar? mini;
	private BrightnessOsd? osd;
	private TrayWheelHook? wheelHook;
	private TrayIconRect.RECT? iconRect;
	private DispatcherTimer? rectTimer;
	private double? wheelTarget;
	private DispatcherTimer? wheelCommit;

	[DllImport("user32.dll")]
	private static extern uint GetDpiForSystem();

	private static double SystemScale()
	{
		try
		{
			return Math.Max(1.0, GetDpiForSystem() / 96.0);
		}
		catch (Exception)
		{
			return 1.0;
		}
	}

	private Rect? IconRectDip()
	{
		TrayIconRect.RECT? r = tray?.IconRect();
		if (r == null) return null;
		double s = SystemScale();
		return new Rect(r.Value.Left / s, r.Value.Top / s, (r.Value.Right - r.Value.Left) / s, (r.Value.Bottom - r.Value.Top) / s);
	}

	internal void ToggleQuickPanel()
	{
		quick ??= new QuickPanel(this);
		if (quick.IsVisible)
		{
			quick.HidePanel();
		}
		else if (!quick.JustHidden)
		{
			quick.ShowNear(IconRectDip());
		}
	}

	internal async Task TogglePowerAsync() => await SetPower(!(powerOn ?? false));

	internal Task ApplyColorAsync(Color c) => ApplyColor(c);

	internal async Task StopAutomationAsync()
	{
		if (settings.SleepUntil.HasValue) StartSleepTimer(0);
		if (modeCancellation != null) await StopModeAsync();
		activeSceneId = null;
		UpdateControls();
	}

	private void Mini_Click(object sender, RoutedEventArgs e) => ShowMini();

	internal void ShowMini()
	{
		mini ??= new MiniBar(this);
		mini.Rebuild();
		mini.Sync();
		Rect virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
		mini.Show();
		mini.UpdateLayout();
		if (settings.MiniLeft is double l && settings.MiniTop is double t && virt.Contains(new Point(l + 40, t + 20)))
		{
			mini.Place(l, t);
		}
		else
		{
			Rect work = SystemParameters.WorkArea;
			mini.Place(work.Left + (work.Width - mini.ActualWidth) / 2, work.Bottom - mini.BarHeight - 8);
		}
		if (IsVisible)
		{
			hideTipShown = true;
			HideToTray();
		}
	}

	internal void HideMini()
	{
		if (mini == null) return;
		SaveMiniPosition(mini.Left, mini.BarTop);
		mini.Hide();
	}

	internal void SaveMiniPosition(double left, double top)
	{
		settings.MiniLeft = left;
		settings.MiniTop = top;
		SaveSettings();
	}

	private void UpdateWheelHook()
	{
		bool want = settings.TrayWheel && tray != null && persistent;
		if (!want)
		{
			wheelHook?.Dispose();
			wheelHook = null;
			rectTimer?.Stop();
			SetHint(WheelHint, "");
			return;
		}
		iconRect = tray!.IconRect();
		if (iconRect == null)
		{
			SetHint(WheelHint, L.T("wheel_no_icon"));
			// Simge sonradan görünür olabilir: kısa aralıklarla yeniden dene.
			rectTimer ??= CreateRectTimer();
			rectTimer.Start();
			return;
		}
		wheelHook ??= new TrayWheelHook(() => iconRect, step => Dispatcher.BeginInvoke((Action)(() => WheelStep(step))));
		if (!wheelHook.Install())
		{
			wheelHook.Dispose();
			wheelHook = null;
			SetHint(WheelHint, L.T("wheel_no_hook"));
			return;
		}
		SetHint(WheelHint, "");
		rectTimer ??= CreateRectTimer();
		rectTimer.Start();
	}

	private DispatcherTimer CreateRectTimer()
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
		t.Tick += (_, _) =>
		{
			iconRect = tray?.IconRect();
			if (wheelHook == null && iconRect != null && settings.TrayWheel) UpdateWheelHook();
		};
		return t;
	}

	private void WheelStep(int step)
	{
		if (!ble.Ready || !ble.Supports(3))
		{
			return;
		}
		double target = Math.Clamp((wheelTarget ?? CurrentBrightness) + 5 * step, 1, 100);
		target = Math.Round(target / 5.0) * 5.0;
		if (target < 1) target = 1;
		wheelTarget = target;
		LiveBrightness(target, final: false);
		osd ??= new BrightnessOsd();
		osd.ShowValue(target, IconRectDip());
		wheelCommit ??= CreateWheelCommit();
		wheelCommit.Stop();
		wheelCommit.Start();
	}

	private DispatcherTimer CreateWheelCommit()
	{
		DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
		t.Tick += (_, _) =>
		{
			t.Stop();
			if (wheelTarget is double v) LiveBrightness(v, final: true);
			wheelTarget = null;
		};
		return t;
	}

	private void Anim_Click(object sender, RoutedEventArgs e)
	{
		settings.Animations = AnimBox.IsChecked == true;
		Anim.Enabled = settings.Animations;
		SaveSettings();
		UpdateAmbientMotion();
	}

	private void WheelBox_Click(object sender, RoutedEventArgs e)
	{
		settings.TrayWheel = WheelBox.IsChecked == true;
		SaveSettings();
		UpdateWheelHook();
	}

	private void About_Click(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(this,
			"Chiaro " + Updater.CurrentText + "\n\n" + L.T("about_body"),
			L.T("about"), MessageBoxButton.OK, MessageBoxImage.Information);
	}
}

public partial class MainWindow
{
	// ---- Duman testi: ekran görüntüleri için sahte "bağlı" durum (ampule hiçbir şey gönderilmez) ----
	internal void PrepareDemoForScreens()
	{
		demo = true;
		connectedName = "Hue Play";
		LightTitle.Text = "Hue Play";
		powerOn = true;
		currentColor = Color.FromRgb(0xFF, 0x8C, 0x1A);
		colorKnown = true;
		SetSilently(BrightnessSlider, 72);
		SetSilently(TemperatureSlider, 3000);
		settings.SleepMinutes = 30;
		settings.SleepUntil = DateTime.Now.AddMinutes(23).AddSeconds(14);
		settings.Wake.Enabled = true;
		WakeToggle.IsChecked = true;
		settings.Schedules.Clear();
		settings.Schedules.Add(new ScheduleItem { Time = "23:30", Action = "Kapat" });
		settings.Schedules.Add(new ScheduleItem { Time = "18:30", Action = "Sahne", SceneId = "okuma", Days = new[] { true, true, true, true, true, false, false } });
		RebuildSchedules();
		Scene oyun = Scenes.First(x => x.Id == "oyun");
		ApplyModeOptions("Ekran", oyun.ModeOptions);
		activeSceneId = "oyun";
		Scenes.First(x => x.Id == "odak").Brightness = 85; // düzenlenmiş hazır sahne işareti için
		RebuildScenes();
		settings.Notify.Enabled = true;
		NotifyToggle.IsChecked = true;
		NotifyStatus.Text = L.T("notify_listening_demo");
		ShowNowPlaying(new MediaColorReader.Track("demo", "Blinding Lights", "The Weeknd", "Spotify.exe", true, Color.FromRgb(0xE0, 0x31, 0x2B)));
		modeName = "Ekran";
		LiveText.Text = L.F("live_value", "50 ms");
		UpdateBulbVisual(false);
		UpdateControls();
		ApplyDemoControls();
		UpdateTimerUi();
	}

	internal void ApplyDemoControls()
	{
		PowerToggle.IsEnabled = true;
		DockPower.IsEnabled = true;
		BrightnessPanel.IsEnabled = true;
		TemperaturePanel.IsEnabled = true;
		ModeStartButton.IsEnabled = true;
		ModeStartButton.Content = L.T("stop");
		ConnDot.Fill = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
		ConnText.Text = L.F("connected_to", "Hue Play");
		StatusText.Text = L.F("connected_to", "Hue Play");
		DetailText.Text = L.T("ready");
		StateText.Text = L.F("on_pct", L.Pct(72));
		PowerToggle.IsChecked = true;
	}

	internal void EndDemo()
	{
		demo = false;
		modeName = "";
		settings.SleepUntil = null;
		UpdateControls();
	}

	internal FrameworkElement SceneCardElement => SceneCard;
	internal Window MiniBarForTest(string panel)
	{
		settings.MiniPanel = panel;
		return mini = new MiniBar(this);
	}

	/// <summary>Duman testi: ana pencereye bağlı hızlı panel (dil değişince yenilenir).</summary>
	internal Window QuickPanelForTest() => quick = new QuickPanel(this);

	internal FrameworkElement SettingsCardElement => (FrameworkElement)((FrameworkElement)SettingsBody.Parent).Parent;

	internal void SetSectionForTest(string key, bool expanded) => SetSectionState(key, expanded, animate: false);

	internal void SetStartupForTest(bool on)
	{
		AutoStartBox.IsChecked = on;
		UpdateLightOnStartupBox();
	}

	/// <summary>Duman testi: "Ayarlar" kartı başlığı ve "Bilgisayar açılınca ışığı aç" seçeneği (kayıt/başlangıç kaydı değiştirilmez).</summary>
	internal string SettingsCardCheck()
	{
		void Check(bool ok, string what)
		{
			if (!ok) throw new InvalidOperationException("Settings card: " + what);
		}
		string title = SettingsTitle.Text;
		Check(title == L.T("settings_title") && title == (L.Lang == L.English ? "Settings" : "Ayarlar"), "title is " + title);
		Check(!Strings.Tr.ContainsKey("general") && !Strings.En.ContainsKey("general"), "old 'general' key still present");
		Check(!Strings.Tr.Values.Any(v => v == "Genel") && !Strings.En.Values.Any(v => v == "General"), "old label still present");
		Check((LightOnStartupBox.Content as string) == (L.Lang == L.English ? "Turn on light at startup" : "Bilgisayar açılınca ışığı aç"), "checkbox label " + LightOnStartupBox.Content);
		SettingsBody.UpdateLayout();
		int iAuto = SettingsBody.Children.IndexOf(AutoStartBox), iLight = SettingsBody.Children.IndexOf(LightOnStartupBox);
		Check(iLight == iAuto + 1, "checkbox must sit right under the startup checkbox");
		Check(!new StudioSettings().LightOnStartup, "default must be off");
		StudioSettings rt = System.Text.Json.JsonSerializer.Deserialize<StudioSettings>(System.Text.Json.JsonSerializer.Serialize(new StudioSettings { LightOnStartup = true }))!;
		Check(rt.LightOnStartup, "setting must persist (JSON roundtrip)");
		Check(System.Text.Json.JsonSerializer.Deserialize<StudioSettings>("{\"AutoStart\":true}")!.LightOnStartup == false, "old settings file -> off");
		bool savedAuto = AutoStartBox.IsChecked == true, savedOpt = settings.LightOnStartup;
		try
		{
			AutoStartBox.IsChecked = true;
			UpdateLightOnStartupBox();
			Check(LightOnStartupBox.IsEnabled && (LightOnStartupBox.ToolTip as string) == L.T("light_on_startup_tip"), "enabled when startup on");
			AutoStartBox.IsChecked = false;
			UpdateLightOnStartupBox();
			Check(!LightOnStartupBox.IsEnabled && LightOnStartupBox.Visibility == Visibility.Visible && (LightOnStartupBox.ToolTip as string) == L.T("light_on_startup_needs"), "visible but disabled with tip when startup off");
			Check(ToolTipService.GetShowOnDisabled(LightOnStartupBox), "tooltip must show while disabled");
			// Başlangıç kapalıyken işaretlenirse: seçenek açılmaz, başlangıç da sessizce açılmaz.
			LightOnStartupBox.IsChecked = true;
			LightOnStartup_Click(LightOnStartupBox, new RoutedEventArgs());
			Check(!settings.LightOnStartup && LightOnStartupBox.IsChecked == false && AutoStartBox.IsChecked == false && DetailText.Text == L.T("light_on_startup_needs"), "ticking while startup off must require startup first");
		}
		finally
		{
			AutoStartBox.IsChecked = savedAuto;
			settings.LightOnStartup = savedOpt;
			UpdateLightOnStartupBox();
		}
		// Algılama ve yeniden deneme planı
		Check(StartupLight.ShouldRun(true, true, true) && !StartupLight.ShouldRun(false, true, true) && !StartupLight.ShouldRun(true, false, true) && !StartupLight.ShouldRun(true, true, false), "only autostart launch + option + startup");
		Check(AutoStart.IsTrayLaunch(new[] { "--tray" }) && !AutoStart.IsTrayLaunch(Array.Empty<string>()) && !AutoStart.IsTrayLaunch(new[] { Updater.UpdatedArgument }), "--tray = autostart; manual / --updated are not");
		Check(AutoStart.Command.EndsWith(" " + AutoStart.TrayArgument), "Run key command carries --tray");
		TimeSpan t = TimeSpan.Zero;
		int tries = 1;
		List<double> waits = new List<double>();
		while (StartupLight.NextDelay(tries - 1, t) is TimeSpan d)
		{
			waits.Add(d.TotalSeconds);
			t += d + TimeSpan.FromSeconds(1); // her deneme ~1 sn sürsün
			tries++;
		}
		Check(tries >= 5 && t <= StartupLight.Window + TimeSpan.FromSeconds(1) && waits[0] < waits[1] && waits[1] < waits[2], "backoff schedule " + string.Join(",", waits));
		Check(StartupLight.SkipReason(false, "", false) == null && StartupLight.SkipReason(false, "", null) == null, "power on when off/unknown");
		Check(StartupLight.SkipReason(false, "Ekran", false) != null && StartupLight.SkipReason(false, "", true) != null && StartupLight.SkipReason(true, "", false) != null, "skip when mode running / already on / user acted");
		return $"\nSettings card: title \"{title}\" (no 'Genel/General' left), \"{LightOnStartupBox.Content}\" right under startup, default off, persisted, disabled+tip when startup off, ticking needs startup first; "
			+ $"autostart detection (--tray only), backoff {string.Join("/", waits)} s = {tries} tries within {StartupLight.Window.TotalSeconds:F0} s, no power-on over a running mode: PASS.";
	}

	/// <summary>Duman testi: "Son modu geri aç" seçeneği, son modun kaydı/silinmesi ve açılış kararı (kayıt/başlangıç kaydı değiştirilmez).</summary>
	internal string RestoreModeCheck()
	{
		void Check(bool ok, string what)
		{
			if (!ok) throw new InvalidOperationException("Restore last mode: " + what);
		}
		bool en = L.Lang == L.English;
		Check((RestoreLastModeBox.Content as string) == (en ? "Restore last mode" : "Son modu geri aç"), "checkbox label " + RestoreLastModeBox.Content);
		SettingsBody.UpdateLayout();
		Check(SettingsBody.Children.IndexOf(RestoreLastModeBox) == SettingsBody.Children.IndexOf(LightOnStartupBox) + 1, "must sit right under 'light on startup'");
		Check(!new StudioSettings().RestoreLastMode && new StudioSettings().LastMode == null, "default off, nothing saved");
		Check(System.Text.Json.JsonSerializer.Deserialize<StudioSettings>("{\"AutoStart\":true,\"LightOnStartup\":true}")!.RestoreLastMode == false, "old settings file -> off");
		bool savedAuto = AutoStartBox.IsChecked == true, savedOpt = settings.RestoreLastMode;
		LastModeState? savedLast = settings.LastMode;
		try
		{
			AutoStartBox.IsChecked = true;
			UpdateLightOnStartupBox();
			Check(RestoreLastModeBox.IsEnabled && (RestoreLastModeBox.ToolTip as string) == L.T("restore_last_mode_tip"), "enabled with tip when startup on");
			AutoStartBox.IsChecked = false;
			UpdateLightOnStartupBox();
			Check(!RestoreLastModeBox.IsEnabled && RestoreLastModeBox.Visibility == Visibility.Visible && (RestoreLastModeBox.ToolTip as string) == L.T("light_on_startup_needs"), "greyed with tip when startup off");
			Check(ToolTipService.GetShowOnDisabled(RestoreLastModeBox), "tooltip must show while disabled");
			RestoreLastModeBox.IsChecked = true;
			RestoreLastMode_Click(RestoreLastModeBox, new RoutedEventArgs());
			Check(!settings.RestoreLastMode && RestoreLastModeBox.IsChecked == false && AutoStartBox.IsChecked == false, "ticking while startup off must require startup first");
			// Kayıt: başla -> ayar değişikliği -> (çökme: JSON'da kalır) -> kapanış korur -> bilerek durdurma siler.
			StudioSettings s = new StudioSettings();
			s.Screen.Monitor = 1;
			s.Screen.Area = 2;
			Check(LastMode.OnModeStarted(s, "Ekran", "oyun", 64.4, null, DateTime.UtcNow) && s.LastMode!.Mode == "Ekran" && s.LastMode.SceneId == "oyun" && s.LastMode.Brightness == 64, "start saves mode + scene + brightness");
			s.Screen.Area = 3;
			Check(LastMode.Refresh(s, "Ekran", null, DateTime.UtcNow) && !LastMode.Refresh(s, "Ekran", null, DateTime.UtcNow), "parameter change updates once");
			StudioSettings crashed = System.Text.Json.JsonSerializer.Deserialize<StudioSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
			ScreenOptions restored = System.Text.Json.JsonSerializer.Deserialize<ScreenOptions>(crashed.LastMode!.Options!)!;
			Check(crashed.LastMode.Mode == "Ekran" && restored.Monitor == 1 && restored.Area == 3, "survives a crash (saved file has monitor/area)");
			Check(!LastMode.OnModeStopped(s, "Ekran", deliberate: false) && s.LastMode != null, "app exit / shutdown / disconnect keeps it");
			Check(LastMode.OnModeStopped(s, "Ekran", deliberate: true) && s.LastMode == null, "deliberate stop clears it");
			LastMode.OnModeStarted(s, "Şömine", null, 50, null, DateTime.UtcNow);
			Check(LastMode.OnStaticChange(s) && s.LastMode == null, "static colour / light off clears it");
			Check(!LastMode.OnModeStarted(s, "Uyku", null, 50, null, DateTime.UtcNow) && !LastMode.OnModeStarted(s, "Uyandırma", null, 50, null, DateTime.UtcNow) && s.LastMode == null, "sleep/wake are not restorable");
			Check(LastMode.ClampMonitor(restored, 1) && restored.Monitor == 0 && !LastMode.ClampMonitor(restored, 2), "missing monitor -> primary");
			// Karar: yalnız otomatik başlatma; geri açma ışığı açmaya göre öncelikli; başarısızsa yalnız ışık seçeneği açıksa ışık açılır.
			LastModeState last = new LastModeState { Mode = "Ekran" };
			Check(LastMode.Decide(true, true, true, true, last) == StartupAction.RestoreMode, "both on -> restore wins");
			Check(LastMode.Decide(true, true, false, true, last) == StartupAction.RestoreMode && LastMode.Decide(true, true, true, true, null) == StartupAction.PowerOn && LastMode.Decide(true, true, false, true, null) == StartupAction.None, "nothing saved -> light option decides");
			Check(LastMode.Decide(false, true, true, true, last) == StartupAction.None && LastMode.Decide(true, false, true, true, last) == StartupAction.None, "manual launch / startup off -> nothing");
			Check(LastMode.AfterFailedRestore(true) == StartupAction.PowerOn && LastMode.AfterFailedRestore(false) == StartupAction.None, "fallback");
			Check(L.F("last_mode_restored", L.Mode("Ekran")) == (en ? "Restored last mode: " : "Son mod geri açıldı: ") + L.Mode("Ekran"), "status line " + L.F("last_mode_restored", L.Mode("Ekran")));
		}
		finally
		{
			AutoStartBox.IsChecked = savedAuto;
			settings.RestoreLastMode = savedOpt;
			settings.LastMode = savedLast;
			UpdateLightOnStartupBox();
		}
		return $"\nRestore last mode: \"{RestoreLastModeBox.Content}\" right under \"{LightOnStartupBox.Content}\", default off, greyed+tip when startup off; saved on start/change, survives crash/exit, cleared on deliberate stop/static colour; autostart-only, restore beats light-on, fallback to light-on: PASS.";
	}

	/// <summary>Oyun sahnesinin mod ayarları gerçekten uygulanıyor mu (50 ms + parlaklık ekrana uysun)?</summary>
	internal string TestOyunScene()
	{
		ScreenOptions before = settings.Screen;
		settings.Screen = new ScreenOptions { IntervalMs = 300, FollowBrightness = false };
		RebindModeOptions();
		Scene oyun = Scene.Defaults().First(x => x.Id == "oyun");
		ApplyModeOptions(oyun.Mode!, oyun.ModeOptions);
		CheckBox? follow = FindFollowBox();
		if (settings.Screen.IntervalMs != 50 || !settings.Screen.FollowBrightness || ScreenInterval.Value != 50 || follow?.IsChecked != true)
		{
			throw new InvalidOperationException($"Oyun scene options not applied: {settings.Screen.IntervalMs} {settings.Screen.FollowBrightness} ui={ScreenInterval.Value} cb={follow?.IsChecked}");
		}
		settings.Screen = before;
		RebindModeOptions();
		return "\nOyun scene applies Güncelleme 50 ms + Parlaklık ekrana uysun (settings + UI): PASS.";
	}

	private CheckBox? FindFollowBox()
	{
		foreach (object o in OptScreen.Children)
		{
			if (o is CheckBox cb && cb.Content as string == L.T("follow_brightness")) return cb;
		}
		return null;
	}
	internal FrameworkElement TimerCardElement => TimerCard;
	internal FrameworkElement NotifyCardElement => NotifyCard;
	internal FrameworkElement ModesCardElement => ModesCard;
}

public partial class MainWindow
{
	internal void SetFullHeightForTest(bool full)
	{
		MainScroll.VerticalScrollBarVisibility = full ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
	}
}

public partial class MainWindow
{
	internal void SetLiveTextForTest()
	{
		(double k, double b) = Daylight.At(settings.Daylight, DateTime.Now);
		LiveText.Text = L.F("preview_daylight", k, L.Pct(b));
	}
}

public partial class MainWindow
{
	// ---- Mini mod paneli için ----

	internal string ActiveMode => modeName;
	internal string? ActiveSceneId => activeSceneId;
	internal string MiniPanel
	{
		get => settings.MiniPanel ?? "";
		set { settings.MiniPanel = value; SaveSettings(); }
	}

	internal static readonly string[] MiniModes = { "Manuel", "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" };

	internal bool CanRunMode(string mode) => mode == "Manuel" || !IsReady || ModeSupported(mode);

	/// <summary>Mini moddan: aynı mod çalışıyorsa durdurur, değilse kayıtlı ayarlarıyla başlatır.</summary>
	internal async Task ToggleModeAsync(string mode)
	{
		activeSceneId = null;
		if (mode == "Manuel" || modeName == mode)
		{
			if (modeCancellation != null) await StopModeAsync();
			ShowTab("Manuel", animate: false);
			UpdateControls();
			return;
		}
		if (!ble.Ready && !await EnsureConnectedAsync())
		{
			DetailText.Text = L.T("connect_to_start");
			RaiseLightState();
			return;
		}
		if (!ModeSupported(mode))
		{
			DetailText.Text = UnsupportedText(mode);
			RaiseLightState();
			return;
		}
		ShowTab(mode, animate: false);
		await StartMode(mode);
	}
}
