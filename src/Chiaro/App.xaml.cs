#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;

namespace HueStudio;

public partial class App : Application
{
	private Mutex? instanceMutex;

	private bool ownsMutex;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		bool smoke = e.Args.Length >= 2 && e.Args[0] == "--smoke-test";
		if (e.Args.Length >= 2 && e.Args[0] == "--language-probe")
		{
			// Dil algılama denetimi (Wine'da LANG ile): ayar yokmuş gibi "auto" çözülür.
			File.WriteAllText(e.Args[1], $"CurrentUICulture={System.Globalization.CultureInfo.CurrentUICulture.Name}; InstalledUICulture={System.Globalization.CultureInfo.InstalledUICulture.Name}; auto={L.Resolve(L.Auto)}\n");
			Shutdown(0);
			return;
		}
		// Dil, ilk iletiden (ör. "zaten açık") önce uygulanır. Duman testi Türkçe başlar, sonra İngilizceye geçer.
		L.Apply(smoke ? L.Turkish : StudioSettings.Load().Language);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		DispatcherUnhandledException += (_, args) =>
		{
			// Beklenmeyen bir arayüz hatası uygulamayı sessizce kapatmasın.
			try
			{
				string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueBluetooth");
				Directory.CreateDirectory(dir);
				File.AppendAllText(Path.Combine(dir, "hata-gunlugu.txt"), $"{DateTimeOffset.Now:O} {args.Exception}\n\n");
			}
			catch (Exception)
			{
			}
			if (!smoke)
			{
				args.Handled = true;
			}
		};
		if (!smoke)
		{
			// Tek kopya: iki pencere aynı ampule aynı anda bağlanmaya çalışmasın.
			// Güncellemeden sonra (--updated) eski kopya kapanırken kilidi birkaç saniye bekle.
			bool updated = e.Args.Any(a => a.Equals(Updater.UpdatedArgument, StringComparison.OrdinalIgnoreCase));
			ownsMutex = AcquireInstanceLock(updated ? 20000 : 0);
			if (!ownsMutex)
			{
				MessageBox.Show(L.T("already_running"), "Chiaro");
				Shutdown();
				return;
			}
		}
		Anim.InstallMicroInteractions();
		MainWindow window = new MainWindow(!smoke);
		MainWindow = window;
		SessionEnding += (object _, SessionEndingCancelEventArgs _) =>
		{
			window.PrepareForShutdown();
		};
		if (smoke)
		{
			await RunSmokeTest(window, Path.GetFullPath(e.Args[1]));
			return;
		}
		window.InitAutoStart();
		window.StartServices();
		window.StartUpdateServices(e.Args.Any(a => a.Equals(Updater.UpdatedArgument, StringComparison.OrdinalIgnoreCase)));
		if (AutoStart.IsTrayLaunch(e.Args))
		{
			window.StartInTray();
			return;
		}
		window.Show();
	}

	private async Task RunSmokeTest(MainWindow window, string folder)
	{
		Directory.CreateDirectory(folder);
		string result = "";
		try
		{
			window.AssertOfflineState();
			window.AddLampsForTest();
			await window.TestModeHandoff();
			window.ShowTabForTest("Gökkuşağı");
			window.ShowTabForTest("Şömine");
			window.ShowTabForTest("Ekran");
			window.ShowTabForTest("Manuel");
			await RenderContent(window, Path.Combine(folder, "0-cevrimdisi.png"), 1240, 1000);
			result += window.TestOyunScene();
			window.PrepareDemoForScreens();
			window.ShowTabForTest("Ekran");
			window.ApplyDemoControls();
			await RenderContent(window, Path.Combine(folder, "1-ana-pencere.png"), 1240, 1000,
				(window.SceneCardElement, Path.Combine(folder, "2-sahneler-karti.png")));
			await RenderContent(window, Path.Combine(folder, "0-tam-sayfa.png"), 1240, 0,
				(window.ModesCardElement, Path.Combine(folder, "4-modlar-karti-oyun.png")));
			foreach (string key in new[] { "device", "timer", "notify", "general", "wake" })
			{
				window.SetSectionForTest(key, false);
			}
			window.ApplyDemoControls();
			await RenderContent(window, Path.Combine(folder, "5-daraltilmis-bolumler.png"), 1240, 0);
			foreach (string key in new[] { "device", "timer", "notify", "general", "wake" })
			{
				window.SetSectionForTest(key, true);
			}
			await RenderContent(window, Path.Combine(folder, "0-tam-sayfa-daraltilmis.png"), 1240, 0);
			window.ApplyTheme(dark: false);
			window.ApplyDemoControls();
			await RenderContent(window, Path.Combine(folder, "6-acik-tema.png"), 1240, 1000);
			window.ApplyTheme(dark: true);
			window.QuickPanelTab = ScenesModes.ScenesTab;
			await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel.png"), 380);
			window.QuickPanelTab = ScenesModes.ModesTab;
			await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-modlar.png"), 380);
			await UpdateScreens(window, folder);
			await RenderFloating(window.MiniBarForTest(""), Path.Combine(folder, "8-mini-mod.png"), 0);
			await RenderFloating(window.MiniBarForTest("Sahneler"), Path.Combine(folder, "8-mini-mod-sahneler.png"), 0);
			await RenderFloating(window.MiniBarForTest("Modlar"), Path.Combine(folder, "8-mini-mod-modlar.png"), 0);
			window.MiniBarForTest("");
			await RenderElement(EmojiPicker.PreviewForTest("🎮"), Path.Combine(folder, "9-emoji-secici.png"), 0);
			window.ApplyTheme(dark: false);
			window.QuickPanelTab = ScenesModes.ScenesTab;
			await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-acik.png"), 380);
			window.QuickPanelTab = ScenesModes.ModesTab;
			await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-acik-modlar.png"), 380);
			window.QuickPanelTab = ScenesModes.ScenesTab;
			window.ApplyTheme(dark: true);
			result += await LanguageScreens(window, folder);
			window.EndDemo();
			result += CoreChecks();
			result += SceneChecks();
			result += await NotificationDbCheck(folder);
			result += await UpdaterSelfTest.RunAsync(folder);
			result += InstanceLockCheck();
			result += AutoStart.MigrationCheck();

			string path = Path.Combine(folder, "theme-test.json");
			HueStudio.MainWindow.SaveAppearance(path, dark: false);
			if (JsonSerializer.Deserialize<MainWindow.Appearance>(File.ReadAllText(path))?.Dark ?? true)
			{
				throw new InvalidOperationException("Light theme persistence failed");
			}
			if (HueStudio.MainWindow.RainbowColor(0.0) != HueStudio.MainWindow.RainbowColor(24.0) || HueStudio.MainWindow.RainbowColor(0.0) == HueStudio.MainWindow.RainbowColor(8.0))
			{
				throw new InvalidOperationException("Rainbow cycle failed");
			}
			double down = RainbowEngine.Position(0.25, 1.0, RainbowDirection.TopToBottom);
			double up = RainbowEngine.Position(0.25, 1.0, RainbowDirection.BottomToTop);
			if (Math.Abs(down - 0.25) > 1e-9 || Math.Abs(up - 0.75) > 1e-9)
			{
				throw new InvalidOperationException("Rainbow direction failed");
			}
			FireEngine fire = new FireEngine();
			for (int i = 0; i < 200; i++)
			{
				(System.Windows.Media.Color c, double b) = fire.Next(0.8, 6, 0.5, 70, true);
				(double h, double _, double _) = ColorMath.ToHsv(c);
				if (!(h <= 60 || h >= 350) || b < 1 || b > 100 || c.R < c.B)
				{
					throw new InvalidOperationException($"Fire palette out of range: {ColorMath.Hex(c)} {b}");
				}
			}
			if (new Lamp(123uL, BluetoothAddressType.Public, "Hue Play AABBCCDDEEFF", null, Paired: true, (short)(-55)).Display != "Hue Play")
			{
				throw new InvalidOperationException("Short display name failed");
			}
			StudioSettings s = new StudioSettings();
			string json = JsonSerializer.Serialize(s);
			if (JsonSerializer.Deserialize<StudioSettings>(json)?.Fire.Intensity != s.Fire.Intensity)
			{
				throw new InvalidOperationException("Settings roundtrip failed");
			}
			result += "\n" + TrayController.TestMenu() + "\nTheme persistence, rainbow cycle + direction, fire palette, settings roundtrip, short device name: PASS.";
			using (Bitmap bitmap = new Bitmap(20, 20))
			{
				using (Graphics graphics = Graphics.FromImage(bitmap))
				{
					graphics.Clear(System.Drawing.Color.FromArgb(240, 80, 20));
				}
				var (r, g, b) = AmbientInput.Average(bitmap);
				var s2 = ScreenSampler.Analyze(bitmap, 0, false);
				if (r != 240 || g != 80 || b != 20 || s2.R != 240 || s2.G != 80 || s2.B != 20)
				{
					throw new InvalidOperationException("Screen average failed");
				}
			}
			if (AmbientInput.AudioLevel(0.0, 5.0) != 5.0 || AmbientInput.AudioLevel(1.0, 5.0) != 100.0 || AmbientInput.AudioLevel(0.03, 5.0, 5, 100) != AmbientInput.AudioLevel(0.03, 5.0))
			{
				throw new InvalidOperationException("Audio response mapping failed");
			}
			result += "\nScreen sampler and audio mapping: PASS.";
			if (new Lamp(0x0A0B0C0D4F2AuL, BluetoothAddressType.Random, "Hue Play", null, Paired: true, (short)(-41)).ToString() != "Hue Play")
			{
				throw new InvalidOperationException("Lamp.ToString must be the short name");
			}
			TrayController.RenderMenuForTest(Path.Combine(folder, "9-tepsi-menusu-koyu.png"), dark: true);
			TrayController.RenderMenuForTest(Path.Combine(folder, "9-tepsi-menusu-acik.png"), dark: false);
			result += "\nLamp display + tray menu render: PASS.";
			window.TestTrayLifecycle();
			File.WriteAllText(Path.Combine(folder, "smoke-test.txt"), "WPF construction, offline controls and layout: PASS\n" + result + "\nNo pairing or writes attempted. No real bulb control tested.\n\n" + window.Report());
			window.Close();
			Shutdown(0);
		}
		catch (Exception ex)
		{
			File.WriteAllText(Path.Combine(folder, "smoke-test.txt"), ex.ToString());
			window.PrepareForShutdown();
			window.Close();
			Shutdown(1);
		}
	}

	internal const string InstanceLockName = "Local\\HueBluetoothDesktop-02";

	/// <summary>Tek kopya kilidi. waitMs &gt; 0 ise kilit boşalana kadar bekler (güncelleme sonrası).</summary>
	private bool AcquireInstanceLock(int waitMs)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(waitMs);
		while (true)
		{
			try
			{
				instanceMutex ??= new Mutex(initiallyOwned: false, InstanceLockName);
				if (instanceMutex.WaitOne(Math.Clamp((int)(until - DateTime.UtcNow).TotalMilliseconds, 0, 1000))) return true;
			}
			catch (AbandonedMutexException)
			{
				// Önceki kopya kilidi bırakmadan kapandı: kilit artık bizde.
				return true;
			}
			catch (Exception)
			{
				instanceMutex?.Dispose();
				instanceMutex = null;
				if (DateTime.UtcNow >= until) return false;
				Thread.Sleep(250);
			}
			if (DateTime.UtcNow >= until) return false;
		}
	}

	/// <summary>Güncellemede yeni kopya açılabilsin diye kilidi çıkmadan önce bırakır.</summary>
	internal void ReleaseInstanceLock()
	{
		if (!ownsMutex) return;
		try
		{
			instanceMutex?.ReleaseMutex();
		}
		catch (ApplicationException)
		{
		}
		ownsMutex = false;
	}

	protected override void OnExit(ExitEventArgs e)
	{
		ReleaseInstanceLock();
		instanceMutex?.Dispose();
		base.OnExit(e);
	}

	private static string CoreChecks()
	{
		// Gün ışığı eğrisi: gece sıcak/loş, öğlen soğuk/parlak.
		DaylightOptions d = new DaylightOptions();
		var noon = Daylight.At(d, DateTime.Today.AddHours(14));
		var night = Daylight.At(d, DateTime.Today.AddHours(2));
		var morning = Daylight.At(d, DateTime.Today.AddHours(8));
		if (noon.Kelvin < 5000 || noon.Brightness < 90 || night.Kelvin > 2300 || night.Brightness > 15 || !(morning.Kelvin > night.Kelvin && morning.Kelvin < noon.Kelvin))
		{
			throw new InvalidOperationException($"Daylight curve failed: {noon} {morning} {night}");
		}
		List<Scene> scenes = Scene.Defaults();
		string[] names = { "Film", "Oyun", "Okuma", "Uyku", "Odak", "Romantik" };
		if (!scenes.Select(x => x.Name).SequenceEqual(names) || scenes.Select(x => x.Id).Distinct().Count() != scenes.Count)
		{
			throw new InvalidOperationException("Default scenes failed");
		}
		System.Windows.Media.Color dawn = HueStudio.MainWindow.SunriseColor(0, 4500), day = HueStudio.MainWindow.SunriseColor(1, 4500);
		if (!(dawn.R > dawn.B + 100) || !(day.B > dawn.B))
		{
			throw new InvalidOperationException("Sunrise colors failed");
		}
		var classify = new[] { ("Discord", "Discord"), ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "WhatsApp"), ("MSTeams_8wekyb3d8bbwe!MSTeams", "Teams"), ("Microsoft.Office.OUTLOOK.EXE.15", "Outlook"), ("Microsoft.WindowsStore", "Diğer") };
		foreach (var (id, want) in classify)
		{
			if (NotificationWatcher.Classify(id) != want) throw new InvalidOperationException($"Classify {id} -> {NotificationWatcher.Classify(id)}");
		}
		using (Bitmap art = new Bitmap(60, 60))
		{
			using (Graphics g = Graphics.FromImage(art))
			{
				g.Clear(System.Drawing.Color.FromArgb(20, 20, 24));
				g.FillRectangle(new SolidBrush(System.Drawing.Color.FromArgb(30, 120, 230)), 0, 0, 60, 30);
				g.FillRectangle(new SolidBrush(System.Drawing.Color.FromArgb(200, 200, 200)), 0, 30, 60, 10);
			}
			System.Windows.Media.Color dc = MediaColorReader.Dominant(art);
			if (!(dc.B > dc.R + 60))
			{
				throw new InvalidOperationException("Album color failed: " + dc);
			}
		}
		StudioSettings s = new StudioSettings { Scenes = Scene.Defaults(), SleepUntil = DateTime.Now.AddMinutes(5) };
		s.Schedules.Add(new ScheduleItem { Time = "06:45", Action = "Sahne", SceneId = "odak" });
		StudioSettings back = JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(s))!;
		if (back.Scenes?.Count != 6 || back.Schedules.Count != 1 || back.Schedules[0].SceneId != "odak" || back.SleepUntil == null || back.Wake.Days.Length != 7)
		{
			throw new InvalidOperationException("settings roundtrip failed");
		}
		// Renk sıcaklığı geri okuması: ampul mired saklar, etiket istenen değeri (50 K adımı) göstermeli (3003 K değil).
		for (int k = 2200; k <= 6500; k += 50)
		{
			if (Protocol.Kelvin(Protocol.Temperature(k)) != k)
			{
				throw new InvalidOperationException($"Kelvin readback {k} -> {Protocol.Kelvin(Protocol.Temperature(k))}");
			}
		}
		return "\nCore: daylight curve, default scenes, sunrise colors, notification app match, album color, settings roundtrip, kelvin readback (3000 K -> 3000 K): PASS.";
	}

	private static async Task<string> NotificationDbCheck(string folder)
	{
		string db = Path.Combine(folder, "wpn-test.db");
		File.Delete(db);
		NotificationWatcher.ExecForTest(db, "CREATE TABLE NotificationHandler(RecordId INTEGER PRIMARY KEY, PrimaryId TEXT); CREATE TABLE Notification(Id INTEGER PRIMARY KEY, HandlerId INTEGER, Type TEXT); INSERT INTO NotificationHandler VALUES(1,'com.squirrel.Discord.Discord'),(2,'Microsoft.WindowsStore'); INSERT INTO Notification VALUES(1,2,'toast');");
		NotificationWatcher.TestDatabase = db;
		List<string> got = new List<string>();
		using NotificationWatcher w = new NotificationWatcher(_ => { }) { SkipListener = true };
		w.Arrived += a => { lock (got) got.Add(a); };
		await w.StartAsync();
		await Task.Delay(2200);
		NotificationWatcher.ExecForTest(db, "INSERT INTO Notification VALUES(2,1,'toast'); INSERT INTO Notification VALUES(3,2,'tile');");
		await Task.Delay(3500);
		w.Stop();
		NotificationWatcher.TestDatabase = null;
		lock (got)
		{
			if (got.Count != 1 || NotificationWatcher.Classify(got[0]) != "Discord" || w.Method != "Windows bildirim kaydı")
			{
				throw new InvalidOperationException("Notification DB fallback failed: " + string.Join(",", got) + " " + w.Method + " " + w.Status);
			}
		}
		return "\nNotification database fallback (new toast detected, old/tile ignored): PASS.";
	}

	private static string SceneChecks()
	{
		string old30 = JsonSerializer.Serialize(new ScreenOptions { IntervalMs = 60, Smoothing = 15, Method = 1, SaturationBoost = 45, IgnoreDark = true });
		List<Scene> saved = Scene.Defaults();
		foreach (Scene x in saved) x.Emoji = null;
		saved.First(x => x.Id == "oyun").ModeOptions = old30;
		Scene.Migrate(saved, 0);
		ScreenOptions m = JsonSerializer.Deserialize<ScreenOptions>(saved.First(x => x.Id == "oyun").ModeOptions!)!;
		if (m.IntervalMs != 50 || !m.FollowBrightness || saved.Any(x => string.IsNullOrEmpty(x.Emoji)) || saved.First(x => x.Id == "film").Emoji != "🎬")
		{
			throw new InvalidOperationException("scene migration failed");
		}
		List<Scene> custom = Scene.Defaults();
		custom.First(x => x.Id == "oyun").ModeOptions = JsonSerializer.Serialize(new ScreenOptions { IntervalMs = 120, Smoothing = 30 });
		custom.First(x => x.Id == "oyun").Emoji = "🕹️";
		Scene.Migrate(custom, 0);
		ScreenOptions keep = JsonSerializer.Deserialize<ScreenOptions>(custom.First(x => x.Id == "oyun").ModeOptions!)!;
		if (keep.IntervalMs != 120 || keep.FollowBrightness || custom.First(x => x.Id == "oyun").Emoji != "🕹️")
		{
			throw new InvalidOperationException("Migration clobbered a user-overwritten Oyun scene");
		}
		List<Scene> d = Scene.Defaults();
		if (d.Any(x => x.IsModified)) throw new InvalidOperationException("Fresh defaults reported as modified");
		d[0].Name = "Sinema";
		d[2].Kelvin = 4000;
		d[1].ModeOptions = JsonSerializer.Serialize(new ScreenOptions { IntervalMs = 50 });
		if (!d[0].IsModified || !d[2].IsModified || !d[1].IsModified || d[3].IsModified || d[0].Original?.Name != "Film")
		{
			throw new InvalidOperationException("Scene modified marker failed");
		}
		string[] emo = { "🎬", "🎮", "📖", "🌙", "🎯", "🌹" };
		if (!Scene.Defaults().Select(x => x.Emoji).SequenceEqual(emo) || Scene.EmojiChoices.Length < 40)
		{
			throw new InvalidOperationException("Default emojis failed");
		}
		return "\nScenes: scene migration (old default updated, user-overwritten Oyun kept), modified marker, default emojis: PASS."
			+ $"\nColor emoji renderer available: {EmojiArt.ColorAvailable}; own COLR={ColrEmoji.Available} {ColrEmoji.Error}; film={(ColrEmoji.Render("🎬") != null)}, heart={(ColrEmoji.Render("❤️") != null)}.";
	}

	/// <summary>3.3 dil desteği: sözlük testi, canlı Türkçe → English geçişi, kalıntı taraması, İngilizce ekranlar.</summary>
	private static async Task<string> LanguageScreens(MainWindow window, string folder)
	{
		string result = LocalizationSelfTest.DictionaryCheck() + LocalizationSelfTest.DetectionCheck();
		window.ShowTabForTest("Ekran");
		window.ApplyDemoControls();
		await RenderContent(window, Path.Combine(folder, "tmp-genel.png"), 1240, 0, (window.GeneralCardElement, Path.Combine(folder, "12-ayarlar-dil.png")));
		// Paneller Türkçe açıkken dil değişir: canlı yenilenmeleri gerekir.
		Window quick = window.QuickPanelForTest();
		window.MiniBarForTest("Modlar");
		Window mini = window.MiniBarForTest("Modlar");
		window.SetUpdateForTest(UpdaterSelfTest.SampleInfo());
		window.SetLanguage(L.English);
		if (L.Lang != L.English || window.LanguageSetting != L.English) throw new InvalidOperationException("Language switch to English failed");
		window.ShowTabForTest("Ekran");
		window.ApplyDemoControls();
		LocalizationSelfTest.StaleScan("main window", LocalizationSelfTest.Texts(window));
		LocalizationSelfTest.StaleScan("quick panel", LocalizationSelfTest.Texts(quick));
		LocalizationSelfTest.StaleScan("mini mode", LocalizationSelfTest.Texts(mini));
		LocalizationSelfTest.StaleScan("tray menu", TrayController.MenuTextsForTest());
		UpdateDialog probe = new UpdateDialog(null, UpdaterSelfTest.SampleInfo(), null);
		LocalizationSelfTest.StaleScan("update dialog", LocalizationSelfTest.Texts(probe).Where(t => !t.Contains("Gün doğumu") && !t.Contains("Küçük hata")));
		probe.Close();
		LocalizationSelfTest.StaleScan("emoji picker", LocalizationSelfTest.Texts(EmojiPicker.PreviewForTest("🎮")));
		string badgeEn = window.TimerBadgeForTest();
		if (!badgeEn.StartsWith("OFF | SLEEP | ") || System.Text.RegularExpressions.Regex.IsMatch(badgeEn, "KAPALI|UYKU|PROGRAM")) throw new InvalidOperationException("Timer badge not English: " + badgeEn);
		LocalizationSelfTest.StaleScan("built-in scenes", Scene.Defaults().SelectMany(s => new[] { s.DisplayName, s.Subtitle }));
		RenderFloatingKeep(mini);
		quick.Close();
		result += "\nLive switch Türkçe -> English (no restart): main window, quick panel, mini mode, tray menu, update dialog, emoji picker, built-in scenes have no Turkish text left: PASS.";

		window.SetUpdateForTest(null);
		await RenderContent(window, Path.Combine(folder, "1-ana-pencere-en.png"), 1240, 1000,
			(window.SceneCardElement, Path.Combine(folder, "2-sahneler-karti-en.png")),
			(window.HeaderElement, Path.Combine(folder, "10-guncellemeleri-denetle-en.png")));
		await RenderContent(window, Path.Combine(folder, "0-tam-sayfa-en.png"), 1240, 0,
			(window.ModesCardElement, Path.Combine(folder, "4-modlar-karti-en.png")),
			(window.GeneralCardElement, Path.Combine(folder, "12-ayarlar-dil-en.png")));
		window.ApplyTheme(dark: false);
		window.ApplyDemoControls();
		await RenderContent(window, Path.Combine(folder, "6-acik-tema-en.png"), 1240, 1000);
		window.ApplyTheme(dark: true);
		window.QuickPanelTab = ScenesModes.ModesTab;
		await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-modlar-en.png"), 380);
		window.QuickPanelTab = ScenesModes.ScenesTab;
		await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-en.png"), 380);
		await RenderFloating(window.MiniBarForTest(""), Path.Combine(folder, "8-mini-mod-en.png"), 0);
		await RenderFloating(window.MiniBarForTest("Modlar"), Path.Combine(folder, "8-mini-mod-modlar-en.png"), 0);
		await RenderFloating(window.MiniBarForTest("Sahneler"), Path.Combine(folder, "8-mini-mod-sahneler-en.png"), 0);
		window.MiniBarForTest("");
		UpdateInfo fake = UpdaterSelfTest.SampleInfo();
		window.SetUpdateForTest(fake);
		await RenderContent(window, Path.Combine(folder, "tmp-dock.png"), 1240, 1000, (window.HeaderElement, Path.Combine(folder, "10-guncelle-v3.5-en.png")));
		await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-guncelleme-en.png"), 380);
		await RenderFloating(new UpdateDialog(null, fake, null), Path.Combine(folder, "11-guncelleme-penceresi-en.png"), 0);
		UpdateDialog d = new UpdateDialog(null, fake, null);
		d.SetBusyLook(true);
		d.ShowProgress(0.42);
		await RenderFloating(d, Path.Combine(folder, "11-guncelleme-indiriliyor-en.png"), 0);
		window.SetUpdateForTest(null, L.T("upd_uptodate"));
		await RenderContent(window, Path.Combine(folder, "tmp-dock.png"), 1240, 1000, (window.HeaderElement, Path.Combine(folder, "10-en-guncel-surum-en.png")));
		window.SetUpdateForTest(null);
		await RenderElement(EmojiPicker.PreviewForTest("🎮"), Path.Combine(folder, "9-emoji-secici-en.png"), 0);
		TrayController.RenderMenuForTest(Path.Combine(folder, "9-tepsi-menusu-en.png"), dark: true);
		File.Delete(Path.Combine(folder, "tmp-dock.png"));
		File.Delete(Path.Combine(folder, "tmp-genel.png"));

		// Geri Türkçeye: canlı dönüş de çalışmalı.
		window.SetLanguage(L.Turkish);
		window.ShowTabForTest("Ekran");
		window.ApplyDemoControls();
		List<string> english = LocalizationSelfTest.Texts(window).Where(t => Strings.En.Values.Contains(t.Trim()) && !Strings.Tr.Values.Contains(t.Trim())).Distinct().ToList();
		if (english.Count > 0) throw new InvalidOperationException("English text left after switching back to Turkish: " + string.Join(" | ", english.Take(20)));
		string badgeTr = window.TimerBadgeForTest();
		if (!badgeTr.StartsWith("KAPALI | UYKU | ") || System.Text.RegularExpressions.Regex.IsMatch(badgeTr, "OFF|SLEEP|SCHEDULED")) throw new InvalidOperationException("Timer badge not Turkish: " + badgeTr);
		window.SetLanguage(L.Auto);
		string autoLang = L.Lang;
		window.SetLanguage(L.Turkish);
		return result + $"\nLive switch back English -> Türkçe: PASS. Otomatik (Sistem) resolved to '{autoLang}' in this process."
			+ $"\nTimer badge localized: en [{badgeEn}], tr [{badgeTr}]: PASS.";
	}

	private static void RenderFloatingKeep(Window w)
	{
		w.Close();
	}

	/// <summary>3.3 güncelleme arayüzü durumları (sahte v3.5; ağ kullanılmaz).</summary>
	private static async Task UpdateScreens(MainWindow window, string folder)
	{
		UpdateInfo fake = UpdaterSelfTest.SampleInfo();
		window.ShowTabForTest("Ekran");
		window.ApplyDemoControls();
		window.SetUpdateForTest(null);
		await RenderContent(window, Path.Combine(folder, "10-tam-ekran-guncel.png"), 1240, 1000, (window.HeaderElement, Path.Combine(folder, "10-guncellemeleri-denetle.png")));
		window.SetUpdateForTest(null, L.T("upd_uptodate"));
		await RenderContent(window, Path.Combine(folder, "tmp-dock.png"), 1240, 1000, (window.HeaderElement, Path.Combine(folder, "10-en-guncel-surum.png")));
		window.SetUpdateForTest(fake);
		await RenderContent(window, Path.Combine(folder, "10-tam-ekran-guncelleme-var.png"), 1240, 1000, (window.HeaderElement, Path.Combine(folder, "10-guncelle-v3.5.png")));
		File.Delete(Path.Combine(folder, "tmp-dock.png"));
		window.QuickPanelTab = ScenesModes.ScenesTab;
		await RenderFloating(new QuickPanel(window), Path.Combine(folder, "7-hizli-panel-guncelleme.png"), 380);
		UpdateDialog d = new UpdateDialog(null, fake, null);
		await RenderFloating(d, Path.Combine(folder, "11-guncelleme-penceresi.png"), 0);
		d = new UpdateDialog(null, fake, null);
		d.SetBusyLook(true);
		d.ShowProgress(0.42);
		await RenderFloating(d, Path.Combine(folder, "11-guncelleme-indiriliyor.png"), 0);
		d = new UpdateDialog(null, fake, null);
		d.ShowError(L.T("upd_download_interrupted"));
		await RenderFloating(d, Path.Combine(folder, "11-guncelleme-hata.png"), 0);
		window.SetUpdateForTest(null);
	}

	/// <summary>--updated: kilit başka kopyadayken bekleyip devralma.</summary>
	private string InstanceLockCheck()
	{
		using ManualResetEventSlim held = new ManualResetEventSlim();
		Thread other = new Thread(() =>
		{
			using Mutex m = new Mutex(initiallyOwned: false, InstanceLockName);
			m.WaitOne();
			held.Set();
			Thread.Sleep(1500);
			m.ReleaseMutex();
		}) { IsBackground = true };
		other.Start();
		held.Wait(5000);
		if (AcquireInstanceLock(0))
		{
			throw new InvalidOperationException("Instance lock acquired while held by another owner");
		}
		DateTime t0 = DateTime.UtcNow;
		ownsMutex = AcquireInstanceLock(8000);
		double waited = (DateTime.UtcNow - t0).TotalSeconds;
		if (!ownsMutex || waited < 0.8)
		{
			throw new InvalidOperationException($"Instance lock wait failed: owns={ownsMutex} waited={waited:F1}s");
		}
		ReleaseInstanceLock();
		instanceMutex?.Dispose();
		instanceMutex = null;
		other.Join(3000);
		return $"\nSingle-instance lock: busy -> refused; --updated wait took over after {waited:F1}s and released: PASS.";
	}

	private static Task RenderFloating(Window floating, string path, double width)
	{
		FrameworkElement content = (FrameworkElement)floating.Content;
		floating.Content = null;
		floating.Close();
		return RenderElement(content, path, width);
	}

	private static async Task RenderElement(FrameworkElement content, string path, double width)
	{
		Border surface = new Border
		{
			Child = content,
			Padding = new Thickness(24),
			Background = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x2A, 0x4A), System.Windows.Media.Color.FromRgb(0x5A, 0x2D, 0x52), 35),
		};
		TextElement.SetFontFamily(surface, new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"));
		TextElement.SetFontSize(surface, 13);
		surface.SetResourceReference(TextElement.ForegroundProperty, "Text");
		await SaveElement(surface, path, width > 0 ? width + 48 : double.PositiveInfinity);
		surface.Child = null;
	}

	private static async Task SaveElement(FrameworkElement surface, string path, double width)
	{
		surface.Measure(new System.Windows.Size(width, double.PositiveInfinity));
		double w = double.IsInfinity(width) ? Math.Ceiling(surface.DesiredSize.Width) : width;
		surface.Measure(new System.Windows.Size(w, double.PositiveInfinity));
		double h = Math.Ceiling(surface.DesiredSize.Height);
		surface.Arrange(new Rect(0, 0, w, h));
		surface.UpdateLayout();
		await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
		await Task.Delay(500);
		surface.UpdateLayout();
		RenderTargetBitmap bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
		bmp.Render(surface);
		Save(bmp, path);
	}

	private static void Save(BitmapSource bmp, string path)
	{
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream stream = File.Create(path);
		encoder.Save(stream);
	}

	private static async Task RenderContent(MainWindow window, string path, int width, int height, params (FrameworkElement El, string Path)[] parts)
	{
		FrameworkElement content = (FrameworkElement)window.Content;
		window.Content = null;
		Border surface = new Border
		{
			Child = content,
		};
		surface.SetResourceReference(Border.BackgroundProperty, "Backdrop");
		TextElement.SetFontFamily(surface, window.FontFamily);
		TextElement.SetFontSize(surface, window.FontSize);
		TextElement.SetForeground(surface, window.Foreground);
		if (height <= 0)
		{
			// Tam sayfa: kaydırma alanını içeriğin tamamı kadar uzat.
			window.SetFullHeightForTest(true);
			surface.Measure(new System.Windows.Size(width, double.PositiveInfinity));
			height = (int)Math.Min(4000, Math.Ceiling(surface.DesiredSize.Height));
		}
		surface.Measure(new System.Windows.Size(width, height));
		surface.Arrange(new Rect(0.0, 0.0, width, height));
		surface.UpdateLayout();
		await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
		await Task.Delay(700);
		surface.UpdateLayout();
		RenderTargetBitmap bmp = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		bmp.Render(surface);
		Save(bmp, path);
		foreach (var (el, p) in parts)
		{
			// Kartı tam sayfadan kırp (gölge için 14 px pay).
			Rect r = el.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
			r.Inflate(14, 14);
			r.Intersect(new Rect(0, 0, width, height));
			if (r.Width < 30 || r.Height < 30) continue;
			Save(new CroppedBitmap(bmp, new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)), p);
		}
		window.SetFullHeightForTest(false);
		surface.Child = null;
		window.Content = content;
	}
}
