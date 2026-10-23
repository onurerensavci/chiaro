#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

/// <summary>Menü çubuğu (status bar) simgesi ve hızlı menü; macOS uygulama menüsü; önizleme ekran görüntüsü.</summary>
public partial class MainWindow
{
	private TrayIcon? tray;
	private NativeMenuItem? trayStatus;
	private NativeMenuItem? trayLive;
	private string traySignature = "";
	private NativeMenuItem? appAbout;

	private void InitTray()
	{
		try
		{
			Application? app = Application.Current;
			if (app == null) return;
			bool mac = OperatingSystem.IsMacOS();
			using Stream s = AssetLoader.Open(new Uri(mac ? "avares://Chiaro/Assets/tray-template.png" : "avares://Chiaro/Assets/tray-color.png"));
			tray = new TrayIcon { Icon = new WindowIcon(new Bitmap(s)), ToolTipText = "Chiaro", Menu = new NativeMenu(), IsVisible = true };
			if (mac) MacOSProperties.SetIsTemplateIcon(tray, true);
			TrayIcon.SetIcons(app, new TrayIcons { tray });
			// macOS uygulama menüsü: "Chiaro Hakkında" (Gizle/Çık öğelerini Avalonia ekler).
			NativeMenu appMenu = new NativeMenu();
			appAbout = new NativeMenuItem(L.T("mac_menu_about"));
			appAbout.Click += (_, _) => About_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
			appMenu.Items.Add(appAbout);
			NativeMenu.SetMenu(app, appMenu);
			RebuildTrayMenu();
			Log("Menü çubuğu simgesi hazır.");
		}
		catch (Exception ex)
		{
			Log("Menü çubuğu simgesi kurulamadı: " + ex.Message);
			tray = null;
		}
	}

	private string TrayStatusLine()
	{
		if (busy) return L.T("busy");
		if (!ble.Ready) return L.T("tray_status_offline");
		string p = powerOn == true ? L.F("on_pct", L.Pct(liveBrightness ?? BrightnessSlider.Value)) : powerOn == false ? L.T("off") : L.T("connected");
		return connectedName + " • " + p;
	}

	private string? TrayLiveLine()
	{
		string? a = modeName.Length > 0 ? L.F("mode_live", L.Mode(modeName)) : null;
		string? b = settings.SleepUntil is DateTime u && modeName != "Uyku" ? L.F("sleep_remaining", Remaining(u)) : null;
		return a == null ? b : b == null ? a : a + "  ·  " + b;
	}

	private void RebuildTrayStatus()
	{
		if (trayStatus != null) trayStatus.Header = TrayStatusLine();
		if (trayLive != null)
		{
			string? live = TrayLiveLine();
			trayLive.Header = live ?? "";
			trayLive.IsVisible = live != null;
		}
		if (tray != null) tray.ToolTipText = "Chiaro • " + TrayStatusLine();
	}

	private void RebuildTrayMenu()
	{
		if (tray?.Menu == null) return;
		if (appAbout != null) appAbout.Header = L.T("mac_menu_about");
		string sig = string.Join("|", L.Lang, ble.Ready, powerOn, modeName, settings.SleepUntil.HasValue, busy,
			string.Join(",", Scenes.Select(s => s.Id + s.DisplayName + s.Emoji)), string.Join(",", settings.Favorites));
		if (sig == traySignature)
		{
			RebuildTrayStatus();
			return;
		}
		traySignature = sig;
		NativeMenu m = tray.Menu;
		m.Items.Clear();
		trayStatus = new NativeMenuItem(TrayStatusLine()) { IsEnabled = false };
		trayLive = new NativeMenuItem("") { IsEnabled = false };
		m.Items.Add(trayStatus);
		m.Items.Add(trayLive);
		m.Items.Add(new NativeMenuItemSeparator());

		NativeMenuItem power = new NativeMenuItem(powerOn == true ? L.T("light_turn_off") : L.T("light_turn_on")) { IsEnabled = ble.Ready };
		power.Click += async (_, _) => await SetPower(!(powerOn ?? false));
		m.Items.Add(power);

		NativeMenuItem bri = new NativeMenuItem(L.T("brightness")) { IsEnabled = ble.Ready && ble.Supports(3), Menu = new NativeMenu() };
		foreach (int p in new[] { 10, 25, 50, 75, 100 })
		{
			NativeMenuItem it = new NativeMenuItem(L.Pct(p));
			it.Click += (_, _) => LiveBrightness(p, final: true);
			bri.Menu.Items.Add(it);
		}
		m.Items.Add(bri);

		NativeMenuItem fav = new NativeMenuItem(L.T("mac_favorites")) { IsEnabled = ble.Ready && ble.Supports(5), Menu = new NativeMenu() };
		foreach (string hex in settings.Favorites.Take(16))
		{
			if (!ColorMath.TryParseHex(hex, out MColor c)) continue;
			NativeMenuItem it = new NativeMenuItem("● " + Palette.NameOf(c) + "  " + ColorMath.Hex(c));
			it.Click += async (_, _) => await ApplyColor(c);
			fav.Menu.Items.Add(it);
		}
		m.Items.Add(fav);

		NativeMenuItem scenes = new NativeMenuItem(L.T("scenes")) { Menu = new NativeMenu() };
		foreach (Scene s in Scenes)
		{
			NativeMenuItem it = new NativeMenuItem(s.Label + (s.Kind == "Mod" && !ModeAvailable(s.Mode) ? "  (" + L.T("mac_soon") + ")" : ""))
			{
				IsEnabled = !(s.Kind == "Mod" && !ModeAvailable(s.Mode)),
			};
			it.Click += async (_, _) => await ApplySceneAsync(s);
			scenes.Menu.Items.Add(it);
		}
		if (Scenes.Count == 0) scenes.Menu.Items.Add(new NativeMenuItem(L.T("mac_no_scenes_menu")) { IsEnabled = false });
		m.Items.Add(scenes);

		NativeMenuItem modes = new NativeMenuItem(L.T("modes")) { IsEnabled = ble.Ready, Menu = new NativeMenu() };
		foreach (string id in new[] { "Gökkuşağı", "Şömine", "Gün ışığı" })
		{
			NativeMenuItem it = new NativeMenuItem((modeName == id ? "● " : "") + L.Mode(id));
			it.Click += async (_, _) => { activeSceneId = null; ShowTab(id); await StartMode(id); };
			modes.Menu.Items.Add(it);
		}
		modes.Menu.Items.Add(new NativeMenuItemSeparator());
		NativeMenuItem stop = new NativeMenuItem(L.T("stop_mode")) { IsEnabled = modeName.Length > 0 };
		stop.Click += async (_, _) => await StopModeAsync();
		modes.Menu.Items.Add(stop);
		m.Items.Add(modes);

		m.Items.Add(new NativeMenuItemSeparator());
		NativeMenuItem sleep = new NativeMenuItem(settings.SleepUntil.HasValue ? L.T("sleep_cancel") : L.T("sleep_30"));
		sleep.Click += (_, _) => StartSleepTimer(settings.SleepUntil.HasValue ? 0 : 30);
		m.Items.Add(sleep);
		if (!ble.Ready)
		{
			NativeMenuItem rc = new NativeMenuItem(L.T("reconnect")) { IsEnabled = !busy && Devices.SelectedItem is Lamp };
			rc.Click += async (_, _) => { if (Devices.SelectedItem is Lamp l) await ConnectSelected(l); };
			m.Items.Add(rc);
		}
		m.Items.Add(new NativeMenuItemSeparator());
		NativeMenuItem open = new NativeMenuItem(L.T("open_studio"));
		open.Click += (_, _) => RestoreWindow();
		m.Items.Add(open);
		NativeMenuItem quit = new NativeMenuItem(L.T("mac_menu_quit"));
		quit.Click += (_, _) => ExitApplication();
		m.Items.Add(quit);
		RebuildTrayStatus();
	}

	// =====================================================================
	//  Önizleme ekran görüntüsü (Linux / Xvfb doğrulaması için; normal kullanımda çalışmaz)
	//  CHIARO_SCREENSHOT=dosya.png  CHIARO_SCROLL=piksel  CHIARO_DEMO_SCRIPT=rainbow|fire|daylight|help
	// =====================================================================

	private async Task ScreenshotIfRequested()
	{
		string? path = Environment.GetEnvironmentVariable("CHIARO_SCREENSHOT");
		if (string.IsNullOrEmpty(path)) return;
		await Task.Delay(800);
		string script = Environment.GetEnvironmentVariable("CHIARO_DEMO_SCRIPT") ?? "";
		foreach (string step in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			switch (step)
			{
				case "rainbow": ShowTab("Gökkuşağı"); await StartMode("Gökkuşağı"); break;
				case "fire": ShowTab("Şömine"); await StartMode("Şömine"); break;
				case "daylight": ShowTab("Gün ışığı"); await StartMode("Gün ışığı"); break;
				case "screen": ShowTab("Ekran"); break;
				case "screenlive": ShowTab("Ekran"); await StartMode("Ekran"); break;
				case "audio": ShowTab("Ses"); await StartMode("Ses"); break;
				case "audiotab": ShowTab("Ses"); break;
				case "spotify": ShowTab("Spotify"); await StartMode("Spotify"); break;
				case "permscreen": ShowTab("Ekran"); ShowPermissionDemo(Privacy.ScreenPane); break;
				case "permauto": ShowTab("Spotify"); ShowPermissionDemo(Privacy.AutomationPane); break;
				case "mini": ShowMini(); break;
				case "miniscenes": ShowMini(); SetMiniPanel("scenes"); break;
				case "minimodes": ShowMini(); SetMiniPanel("modes"); break;
				case "color": await ApplyColor(MColor.FromRgb(0xFF, 0x8C, 0x1A)); break;
				case "scene": await ApplySceneById("okuma"); break;
				case "sleep": StartSleepTimer(30); break;
				case "program": if (settings.Schedules.Count == 0) { settings.Schedules.Add(new ScheduleItem { Time = "23:30" }); settings.Schedules.Add(new ScheduleItem { Time = "18:30", Action = "Sahne", SceneId = "okuma" }); RebuildSchedules(); } break;
				case "wake": settings.Wake.Enabled = true; WakeEnabled.IsChecked = true; UpdateTimerUi(); break;
			}
			await Task.Delay(400);
		}
		await Task.Delay(1500);
		if (int.TryParse(Environment.GetEnvironmentVariable("CHIARO_SCROLL"), out int scroll)) MainScroll.Offset = new Vector(0, scroll);
		await Task.Delay(500);
		try
		{
			Window target = mini is { IsVisible: true } ? mini : this;
			PixelSize size = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
			using RenderTargetBitmap bmp = new RenderTargetBitmap(size, new Vector(96, 96));
			bmp.Render(target);
			bmp.Save(path);
			Log("Ekran görüntüsü: " + Path.GetFileName(path));
		}
		catch (Exception ex)
		{
			Log("Ekran görüntüsü alınamadı: " + ex);
		}
		if (Environment.GetEnvironmentVariable("CHIARO_SCREENSHOT_KEEP") != "1")
		{
			await StopModeAsync();
			ExitApplication();
		}
	}
}
