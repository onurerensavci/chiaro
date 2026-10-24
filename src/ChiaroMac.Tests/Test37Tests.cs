using HueStudio;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>3.7: "Son modu geri aç / Restore last mode".</summary>
[Collection("Loc")]
public class Test37Tests
{
	private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

	private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

	[Fact]
	public void Labels_and_tips_in_both_languages()
	{
		Assert.Equal("Son modu geri aç", Strings.Tr["restore_last_mode"]);
		Assert.Equal("Restore last mode", Strings.En["restore_last_mode"]);
		Assert.Equal("Bilgisayar açılınca Chiaro'da en son açık olan modu yeniden başlatır", Strings.Tr["restore_last_mode_tip"]);
		Assert.Equal("Son mod geri açıldı: {0}", Strings.Tr["last_mode_restored"]);
		Assert.Equal("Restored last mode: {0}", Strings.En["last_mode_restored"]);
		Assert.True(MacStrings.Tr.ContainsKey("restore_last_mode_tip") && MacStrings.En.ContainsKey("restore_last_mode_tip"));
		Assert.Equal(Strings.Tr.Count, Strings.En.Count);
		Assert.Equal(MacStrings.Tr.Count, MacStrings.En.Count);
		Assert.Empty(Strings.Tr.Keys.Except(Strings.En.Keys));
	}

	[Fact]
	public void Checkbox_sits_right_under_light_on_startup_on_both_platforms()
	{
		string axaml = Src("ChiaroMac", "MainWindow.axaml");
		string[] boxes = Regex.Matches(axaml, "<CheckBox x:Name=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
		int i = Array.IndexOf(boxes, "AutostartBox");
		Assert.True(i >= 0 && boxes[i + 1] == "LightOnStartupBox" && boxes[i + 2] == "RestoreLastModeBox", string.Join(",", boxes));
		Assert.Matches("RestoreLastModeBox\"[^>]*ToolTip.ShowOnDisabled=\"True\"", axaml);
		string xaml = Src("Chiaro", "MainWindow.xaml");
		string[] win = Regex.Matches(xaml, "<CheckBox x:Name=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
		int j = Array.IndexOf(win, "LightOnStartupBox");
		Assert.True(j >= 0 && win[j + 1] == "RestoreLastModeBox", string.Join(",", win));
		// Başlangıç kapalıyken soluk + ipucu (her iki platform aynı yöntemle).
		string mac = Src("ChiaroMac", "MainWindow.Mac.cs");
		Assert.Contains("RestoreLastModeBox.IsEnabled = startup;", mac);
		Assert.Contains("\"restore_last_mode_tip\" : \"light_on_startup_needs\"", mac);
	}

	[Fact]
	public void Default_off_and_old_settings_files_load_off()
	{
		Assert.False(new StudioSettings().RestoreLastMode);
		Assert.Null(new StudioSettings().LastMode);
		StudioSettings old = JsonSerializer.Deserialize<StudioSettings>("{\"AutoStart\":true,\"LightOnStartup\":true}")!;
		Assert.False(old.RestoreLastMode);
		Assert.Null(old.LastMode);
		StudioSettings rt = JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(new StudioSettings { RestoreLastMode = true }))!;
		Assert.True(rt.RestoreLastMode);
	}

	[Fact]
	public void Start_saves_everything_needed_to_restart()
	{
		StudioSettings s = new StudioSettings();
		s.Screen.Monitor = 1;
		s.Screen.Area = 4;
		s.Screen.IntervalMs = 50;
		DateTime now = new DateTime(2026, 10, 24, 5, 0, 0, DateTimeKind.Utc);
		Assert.True(LastMode.OnModeStarted(s, "Ekran", "oyun", 72.6, null, now));
		LastModeState l = s.LastMode!;
		Assert.Equal("Ekran", l.Mode);
		Assert.Equal("oyun", l.SceneId);
		Assert.Equal(73, l.Brightness);
		Assert.Equal(now, l.SavedAtUtc);
		ScreenOptions o = JsonSerializer.Deserialize<ScreenOptions>(l.Options!)!;
		Assert.Equal(1, o.Monitor);
		Assert.Equal(4, o.Area);
		Assert.Equal(50, o.IntervalMs);
		// Mac ses kaynağı (MacPrefs'te) ayrıca saklanır.
		Assert.True(LastMode.OnModeStarted(s, "Ses", null, null, 1, now));
		Assert.Equal(1, s.LastMode!.AudioSource);
		Assert.Null(s.LastMode.Brightness);
		// Spotify / efektler / gün ışığı da kendi ayarlarıyla.
		s.Rainbow.CycleSeconds = 13.5; s.Rainbow.Palette = 3;
		Assert.True(LastMode.OnModeStarted(s, "Gökkuşağı", null, 40, null, now));
		Assert.Contains("\"CycleSeconds\":13.5", s.LastMode!.Options); Assert.Contains("\"Palette\":3", s.LastMode.Options);
		foreach (string m in new[] { "Spotify", "Şömine", "Gün ışığı" })
		{
			Assert.True(LastMode.OnModeStarted(s, m, null, 40, null, now));
			Assert.False(string.IsNullOrEmpty(s.LastMode!.Options));
		}
	}

	[Fact]
	public void Sleep_and_wake_are_not_modes_to_restore()
	{
		StudioSettings s = new StudioSettings();
		LastMode.OnModeStarted(s, "Ekran", null, 50, null, DateTime.UtcNow);
		Assert.False(LastMode.OnModeStarted(s, "Uyku", null, 50, null, DateTime.UtcNow));
		Assert.False(LastMode.OnModeStarted(s, "Uyandırma", null, 50, null, DateTime.UtcNow));
		Assert.Equal("Ekran", s.LastMode!.Mode);
		Assert.Equal(new[] { "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" }, LastMode.RestorableModes);
		Assert.False(LastMode.IsRestorable(new LastModeState { Mode = "Manuel" }));
	}

	[Fact]
	public void Parameter_change_updates_the_record_once()
	{
		StudioSettings s = new StudioSettings();
		LastMode.OnModeStarted(s, "Ekran", null, 50, null, DateTime.UtcNow);
		Assert.False(LastMode.Refresh(s, "Ekran", null, DateTime.UtcNow));
		s.Screen.Area = 2;
		Assert.True(LastMode.Refresh(s, "Ekran", null, DateTime.UtcNow));
		Assert.False(LastMode.Refresh(s, "Ekran", null, DateTime.UtcNow));
		Assert.Equal(2, JsonSerializer.Deserialize<ScreenOptions>(s.LastMode!.Options!)!.Area);
		Assert.False(LastMode.Refresh(s, "Şömine", null, DateTime.UtcNow)); // başka mod: dokunma
		LastMode.OnModeStarted(s, "Ses", null, 50, 0, DateTime.UtcNow);
		Assert.True(LastMode.Refresh(s, "Ses", 1, DateTime.UtcNow));
		Assert.Equal(1, s.LastMode!.AudioSource);
	}

	[Fact]
	public void Survives_crash_exit_and_disconnect_but_not_a_deliberate_stop()
	{
		string dir = Path.Combine(Path.GetTempPath(), "chiaro37-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			StudioSettings s = new StudioSettings { RestoreLastMode = true };
			LastMode.OnModeStarted(s, "Ekran", null, 80, null, DateTime.UtcNow);
			// "Çökme": durdurma hiç çağrılmadan dosyadan geri okunur.
			string file = Path.Combine(dir, "studio-v2.json");
			File.WriteAllText(file, JsonSerializer.Serialize(s));
			StudioSettings after = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(file))!;
			Assert.Equal("Ekran", after.LastMode!.Mode);
			Assert.Equal(80, after.LastMode.Brightness);
			// Kapanış / bilgisayar kapanışı / kopma: bilinçli değil -> kalır.
			Assert.False(LastMode.OnModeStopped(s, "Ekran", deliberate: false));
			Assert.NotNull(s.LastMode);
			// Durdur: silinir.
			Assert.True(LastMode.OnModeStopped(s, "Ekran", deliberate: true));
			Assert.Null(s.LastMode);
			// Sabit renk / ışığı kapatma: silinir.
			LastMode.OnModeStarted(s, "Şömine", null, 80, null, DateTime.UtcNow);
			Assert.True(LastMode.OnStaticChange(s));
			Assert.Null(s.LastMode);
			Assert.False(LastMode.OnStaticChange(s));
			// Başka bir modun durması kaydı silmez (mod değiştirme: yeni mod kaydı yazar).
			LastMode.OnModeStarted(s, "Spotify", null, 80, null, DateTime.UtcNow);
			Assert.False(LastMode.OnModeStopped(s, "Ekran", deliberate: true));
			Assert.Equal("Spotify", s.LastMode!.Mode);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void Stop_paths_are_wired_on_both_platforms()
	{
		foreach (string[] f in new[] { new[] { "ChiaroMac", "MainWindow.Modes.cs" }, new[] { "Chiaro", "MainWindow.xaml.cs" } })
		{
			string src = Src(f);
			Assert.Contains("LastMode.OnModeStopped(settings, mode, cts.IsCancellationRequested && !closed && !keepLastModeOnStop)", src);
			Assert.Contains("RememberMode(mode, brightness);", src);
		}
		// Mac: bağlantı kopunca mod durur ama kayıt kalır.
		string mac = Src("ChiaroMac", "MainWindow.axaml.cs");
		Assert.Matches(@"if \(!connected\)\s*\{\s*//[^\n]*\n\s*_ = StopModeKeepingLastMode\(\);", mac);
		// Windows: kopma olayında da kayıt korunur.
		string win = Src("Chiaro", "MainWindow.xaml.cs");
		Assert.Matches(@"keepLastModeOnStop = true;\s*modeCancellation.Cancel\(\);\s*KeepLastModeUntil\(modeTask\);", win);
		// Kapanışta closed, mod iptalinden önce ayarlanır (kapanış kaydı silmez).
		int closedAt = mac.IndexOf("closed = true;", mac.IndexOf("Window_Closing", StringComparison.Ordinal), StringComparison.Ordinal);
		int cancelAt = mac.IndexOf("modeCancellation?.Cancel()", closedAt, StringComparison.Ordinal);
		Assert.True(closedAt > 0 && cancelAt > closedAt);
	}

	[Fact]
	public void Only_autostart_launch_restores_and_restore_beats_light_on()
	{
		LastModeState last = new LastModeState { Mode = "Ekran" };
		Assert.Equal(StartupAction.RestoreMode, LastMode.Decide(autostartLaunch: true, startupEnabled: true, lightOnStartup: true, restoreLastMode: true, last));
		Assert.Equal(StartupAction.RestoreMode, LastMode.Decide(true, true, false, true, last));
		Assert.Equal(StartupAction.None, LastMode.Decide(false, true, true, true, last));   // elle açılış
		Assert.Equal(StartupAction.None, LastMode.Decide(true, false, true, true, last));   // başlangıç kapalı
		Assert.Equal(StartupAction.PowerOn, LastMode.Decide(true, true, true, false, last)); // yalnız ışık seçeneği
		Assert.Equal(StartupAction.PowerOn, LastMode.Decide(true, true, true, true, null));  // kayıtlı mod yok
		Assert.Equal(StartupAction.None, LastMode.Decide(true, true, false, true, null));
		Assert.Equal(StartupAction.None, LastMode.Decide(true, true, false, true, new LastModeState { Mode = "Uyku" }));
	}

	[Fact]
	public void Fallbacks_after_a_failed_restore()
	{
		Assert.Equal(StartupAction.PowerOn, LastMode.AfterFailedRestore(lightOnStartup: true));
		Assert.Equal(StartupAction.None, LastMode.AfterFailedRestore(false));
		ScreenOptions o = new ScreenOptions { Monitor = 2 };
		Assert.True(LastMode.ClampMonitor(o, 1));   // kayıtlı ekran çıkarıldı -> birincil
		Assert.Equal(0, o.Monitor);
		o.Monitor = 1;
		Assert.False(LastMode.ClampMonitor(o, 2));
		Assert.False(LastMode.ClampMonitor(o, 0));  // liste alınamadı: dokunma (örnekleyici yine 0'a düşer)
		// Geri açma denemesinden sonraki ışık açma, StartMode'un koyduğu "müdahale" işaretini kullanıcıdan saymaz.
		Assert.Null(StartupLight.SkipReason(false, "", false));
		foreach (string[] f in new[] { new[] { "ChiaroMac", "MainWindow.axaml.cs" }, new[] { "Chiaro", "MainWindow.xaml.cs" } })
		{
			string src = Src(f);
			Assert.Contains("StartupLight.SkipReason(autoCancelled && !afterRestore, modeName, powerOn)", src);
			Assert.Contains("LastMode.AfterFailedRestore(settings.LightOnStartup) == StartupAction.PowerOn", src);
		}
		// Mac: izin yoksa oturum açılışında sistem penceresi yerine mevcut izin uyarısı.
		Assert.Contains("ShowPermission(Privacy.ScreenDenied());", Src("ChiaroMac", "MainWindow.Modes.cs"));
	}
}
