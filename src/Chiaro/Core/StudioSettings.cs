#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HueStudio;

public sealed class ScreenOptions
{
	public double IntervalMs { get; set; } = 160;
	public int Area { get; set; }            // 0 tüm ekran, 1 orta, 2 üst, 3 alt, 4 sol, 5 sağ
	public int Monitor { get; set; }         // Screen.AllScreens dizini; 0 = birincil
	public double Smoothing { get; set; } = 45;  // %
	public int Method { get; set; }          // 0 ortalama, 1 canlı renkler
	public double SaturationBoost { get; set; }  // %
	public bool IgnoreDark { get; set; }
	public bool FollowBrightness { get; set; }
}

public sealed class AudioOptions
{
	public double Sensitivity { get; set; } = 5;
	public double Attack { get; set; } = 85;    // %
	public double Release { get; set; } = 40;   // %
	public double MinBrightness { get; set; } = 5;
	public double MaxBrightness { get; set; } = 100;
	public double IntervalMs { get; set; } = 160;
}

public sealed class RainbowOptions
{
	public double CycleSeconds { get; set; } = 24;
	public int Direction { get; set; }
	public int Palette { get; set; }
	public double Saturation { get; set; } = 86;
	public double IntervalMs { get; set; } = 160;
	public bool UseBrightness { get; set; }
	public double Brightness { get; set; } = 80;
}

public sealed class FireOptions
{
	public double Intensity { get; set; } = 60;
	public double Speed { get; set; } = 5;
	public double Warmth { get; set; } = 50;
	public double Brightness { get; set; } = 70;
	public bool Sparks { get; set; } = true;
}

public sealed class SpotifyOptions
{
	public bool OnlySpotify { get; set; } = true;
	public double Brightness { get; set; } = 80;
	public double TransitionMs { get; set; } = 1500;
	public double Saturation { get; set; } = 30;   // % canlılık artışı
}

public sealed class DaylightOptions
{
	public string WakeTime { get; set; } = "07:00";
	public string SleepTime { get; set; } = "23:00";
	public double MinKelvin { get; set; } = 2200;
	public double MaxKelvin { get; set; } = 5500;
	public double MinBrightness { get; set; } = 15;
	public double MaxBrightness { get; set; } = 100;
}

public sealed class WakeAlarm
{
	public bool Enabled { get; set; }
	public string Time { get; set; } = "07:30";
	/// <summary>Pazartesi..Pazar</summary>
	public bool[] Days { get; set; } = { true, true, true, true, true, false, false };
	public double FadeMinutes { get; set; } = 20;
	public double TargetKelvin { get; set; } = 4500;
	public double TargetBrightness { get; set; } = 100;
}

public sealed class ScheduleItem
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
	public bool Enabled { get; set; } = true;
	public string Time { get; set; } = "22:30";
	public bool[] Days { get; set; } = { true, true, true, true, true, true, true };
	/// <summary>"Aç", "Kapat" veya "Sahne"</summary>
	public string Action { get; set; } = "Kapat";
	public string? SceneId { get; set; }
}

public sealed class NotifyOptions
{
	public bool Enabled { get; set; }
	public bool Discord { get; set; } = true;
	public bool WhatsApp { get; set; } = true;
	public bool Teams { get; set; } = true;
	public bool Outlook { get; set; } = true;
	public bool Others { get; set; }
	public bool UseAppColor { get; set; } = true;
	public string Color { get; set; } = "#FFFFFF";
	public double Count { get; set; } = 2;
	public bool Restore { get; set; } = true;
}

public sealed class StudioSettings
{
	public SpotifyOptions Spotify { get; set; } = new SpotifyOptions();
	public DaylightOptions Daylight { get; set; } = new DaylightOptions();
	public WakeAlarm Wake { get; set; } = new WakeAlarm();
	public List<ScheduleItem> Schedules { get; set; } = new List<ScheduleItem>();
	public NotifyOptions Notify { get; set; } = new NotifyOptions();
	public List<Scene>? Scenes { get; set; }
	/// <summary>Sahne tanımlarının sürümü (emoji + güncel Oyun = 2).</summary>
	public int ScenesVersion { get; set; }
	/// <summary>Kart/bölüm açık mı (anahtar → açık).</summary>
	public Dictionary<string, bool>? Sections { get; set; }
	/// <summary>Birleşik kartta son sekme: "Sahneler" veya "Modlar".</summary>
	public string StudioTab { get; set; } = "Sahneler";
	/// <summary>Mini moddaki açık panel: "", "Sahneler" veya "Modlar".</summary>
	public string MiniPanel { get; set; } = "";
	/// <summary>Tepsi hızlı panelinde son sekme: "Sahneler" veya "Modlar".</summary>
	public string QuickTab { get; set; } = "Sahneler";
	/// <summary>Arayüz dili: "auto" (Windows diline göre), "tr" veya "en".</summary>
	public string Language { get; set; } = "auto";
	/// <summary>Son başarılı güncelleme denetimi (açılışta en çok 24 saatte bir).</summary>
	public DateTimeOffset? LastUpdateCheck { get; set; }
	/// <summary>Son denetimde bulunan yeni sürüm (rozet yeniden açılışta da görünsün).</summary>
	public UpdateInfo? KnownUpdate { get; set; }
	public DateTime? SleepUntil { get; set; }
	public double SleepMinutes { get; set; } = 30;
	public bool Animations { get; set; } = true;
	public bool TrayWheel { get; set; } = true;
	public double? MiniLeft { get; set; }
	public double? MiniTop { get; set; }

	public ScreenOptions Screen { get; set; } = new ScreenOptions();
	public AudioOptions Audio { get; set; } = new AudioOptions();
	public RainbowOptions Rainbow { get; set; } = new RainbowOptions();
	public FireOptions Fire { get; set; } = new FireOptions();
	public bool BulbExpanded { get; set; } = true;
	public bool ModesExpanded { get; set; } = true;
	public bool ColorsExpanded { get; set; } = true;
	public string SelectedTab { get; set; } = "Manuel";
	public bool GlassEnabled { get; set; } = true;
	public bool AutoStart { get; set; } = true;
	public List<string> Favorites { get; set; } = new List<string>(Palette.DefaultFavorites);
	public List<string> Recent { get; set; } = new List<string>();

	// Ayar klasörü adı, mevcut kullanıcı verisi korunsun diye sabit kalır.
	public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueBluetooth", "studio-v2.json");

	public static StudioSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				StudioSettings? s = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(FilePath));
				if (s != null)
				{
					s.Screen ??= new ScreenOptions();
					s.Audio ??= new AudioOptions();
					s.Rainbow ??= new RainbowOptions();
					s.Fire ??= new FireOptions();
					s.Favorites ??= new List<string>(Palette.DefaultFavorites);
					s.Recent ??= new List<string>();
					s.SelectedTab ??= "Manuel";
					s.Spotify ??= new SpotifyOptions();
					s.Daylight ??= new DaylightOptions();
					s.Wake ??= new WakeAlarm();
					if (s.Wake.Days == null || s.Wake.Days.Length != 7) s.Wake.Days = new WakeAlarm().Days;
					s.Schedules ??= new List<ScheduleItem>();
					s.Schedules.RemoveAll(x => x == null || x.Days == null || x.Days.Length != 7);
					s.Notify ??= new NotifyOptions();
					if (s.Scenes != null)
					{
						s.Scenes.RemoveAll(x => x == null);
						Scene.Migrate(s.Scenes, s.ScenesVersion);
					}
					s.ScenesVersion = 2;
					if (s.Sections == null)
					{
						// Eski anahtarlardan taşı.
						s.Sections = new Dictionary<string, bool> { ["bulb"] = s.BulbExpanded, ["studio"] = s.ModesExpanded, ["colors"] = s.ColorsExpanded };
					}
					if (s.StudioTab != "Modlar") s.StudioTab = "Sahneler";
					if (s.MiniPanel != "Sahneler" && s.MiniPanel != "Modlar") s.MiniPanel = "";
					if (s.QuickTab != "Modlar") s.QuickTab = "Sahneler";
					if (s.Language != L.Turkish && s.Language != L.English) s.Language = L.Auto;
					return s;
				}
			}
		}
		catch (Exception)
		{
		}
		return new StudioSettings();
	}

	public void Save()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
		File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		File.Move(FilePath + ".tmp", FilePath, overwrite: true);
	}
}
