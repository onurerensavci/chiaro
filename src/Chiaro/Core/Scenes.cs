#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace HueStudio;

/// <summary>Bir sahne: güç + parlaklık + (renk | beyaz tonu | seçenekleriyle bir mod).</summary>
public sealed class Scene
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
	public string Name { get; set; } = "Sahne";
	/// <summary>"Renk", "Beyaz", "Mod" veya "Kapat"</summary>
	public string Kind { get; set; } = "Renk";
	public double Brightness { get; set; } = 80;
	public string Color { get; set; } = "#FF8C1A";
	public double Kelvin { get; set; } = 2700;
	public string? Mode { get; set; }
	public string? ModeOptions { get; set; }
	public bool BuiltIn { get; set; }
	/// <summary>Adın yanında gösterilen emoji (boş olabilir).</summary>
	public string? Emoji { get; set; }

	[System.Text.Json.Serialization.JsonIgnore]
	public string Label => string.IsNullOrWhiteSpace(Emoji) ? DisplayName : Emoji + "  " + DisplayName;

	/// <summary>Görünen ad: hazır sahnenin adı değiştirilmemişse arayüz dilinde, değilse kullanıcının yazdığı gibi.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public string DisplayName => BuiltIn && DefaultName(Id) is string d && Name == d ? L.T("scene_" + Id) : Name;

	/// <summary>Hazır sahnenin ilk (Türkçe, kayıtlı) adı.</summary>
	public static string? DefaultName(string id) => id switch
	{
		"film" => "Film",
		"oyun" => "Oyun",
		"okuma" => "Okuma",
		"uyku" => "Uyku",
		"odak" => "Odak",
		"romantik" => "Romantik",
		_ => null,
	};

	/// <summary>Seçicide önerilen emojiler.</summary>
	public static readonly string[] EmojiChoices =
	{
		"🎬", "🍿", "🎮", "🕹️", "📖", "📚", "💻", "🎯", "🧠", "✍️",
		"🌙", "😴", "🛏️", "🌃", "🌅", "☀️", "🌤️", "🌈", "✨", "💡",
		"🔥", "🕯️", "🌹", "❤️", "💜", "🥂", "🍷", "☕", "🍽️", "🎵",
		"🎧", "🎤", "🎉", "🪩", "🧘", "🏋️", "🛁", "🌿", "🌊", "❄️",
		"🏠", "📺", "🎨", "🎄", "⚡",
	};

	public static string? DefaultEmoji(string id) => id switch
	{
		"film" => "🎬",
		"oyun" => "🎮",
		"okuma" => "📖",
		"uyku" => "🌙",
		"odak" => "🎯",
		"romantik" => "🌹",
		_ => null,
	};

	/// <summary>
	/// Kayıtlı sahneleri günceller: yerleşik sahnelere emoji,
	/// Oyun'a 50 ms güncelleme + parlaklık ekrana uysun. Diğer ayarlar korunur.
	/// </summary>
	public static void Migrate(List<Scene> scenes, int fromVersion)
	{
		if (fromVersion >= 2) return;
		foreach (Scene s in scenes)
		{
			if (s == null || !s.BuiltIn) continue;
			if (string.IsNullOrWhiteSpace(s.Emoji)) s.Emoji = DefaultEmoji(s.Id);
			if (s.Id == "oyun" && s.Mode == "Ekran")
			{
				// Yalnızca önceki varsayılan hiç değiştirilmemişse güncelle (kullanıcı üzerine yazdıysa dokunma).
				ScreenOptions? o = null;
				try { o = string.IsNullOrEmpty(s.ModeOptions) ? null : JsonSerializer.Deserialize<ScreenOptions>(s.ModeOptions); }
				catch { }
				bool old30 = o != null && o.IntervalMs == 60 && o.Smoothing == 15 && o.Method == 1 && o.SaturationBoost == 45 && o.IgnoreDark && !o.FollowBrightness
					&& o.Area == 0 && o.Monitor == 0 && s.Brightness == 90;
				if (old30)
				{
					o!.IntervalMs = 50;
					o.FollowBrightness = true;
					s.ModeOptions = JsonSerializer.Serialize(o);
				}
			}
		}
	}

	[System.Text.Json.Serialization.JsonIgnore]
	public Color Accent
	{
		get
		{
			if (Kind == "Kapat") return Colors.DimGray;
			if (Kind == "Beyaz") return ColorMath.FromKelvin(Kelvin);
			if (Kind == "Mod")
			{
				return Mode switch
				{
					"Ekran" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#64D2FF"),
					"Ses" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#30D158"),
					"Spotify" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#1ED760"),
					"Gökkuşağı" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#BF5AF2"),
					"Şömine" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#FF7B00"),
					"Gün ışığı" => (System.Windows.Media.Color)ColorConverter.ConvertFromString("#FFD27A"),
					_ => Colors.White,
				};
			}
			return ColorMath.TryParseHex(Color, out var c) ? c : Colors.White;
		}
	}

	[System.Text.Json.Serialization.JsonIgnore]
	public string Subtitle => Kind switch
	{
		"Kapat" => L.T("off"),
		"Beyaz" => $"{Kelvin:F0} K • {L.Pct(Brightness)}",
		"Mod" => $"{L.Mode(Mode)} • {L.Pct(Brightness)}",
		_ => $"{Palette.NameOf(Accent)} • {L.Pct(Brightness)}",
	};

	/// <summary>Hazır sahnenin ilk tanımı (yoksa null).</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public Scene? Original => BuiltIn ? Defaults().FirstOrDefault(d => d.Id == Id) : null;

	/// <summary>Hazır sahne ilk hâlinden farklı mı (ad, emoji, ışık veya mod ayarları)?</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsModified
	{
		get
		{
			Scene? o = Original;
			if (o == null) return false;
			if (Name != o.Name || (Emoji ?? "") != (o.Emoji ?? "") || Kind != o.Kind || Math.Abs(Brightness - o.Brightness) > 0.5) return true;
			if (Kind == "Beyaz" && Math.Abs(Kelvin - o.Kelvin) > 1) return true;
			if (Kind == "Renk" && !string.Equals(Color, o.Color, StringComparison.OrdinalIgnoreCase)) return true;
			if (Kind == "Mod" && (Mode != o.Mode || Normalize(Mode, ModeOptions) != Normalize(o.Mode, o.ModeOptions))) return true;
			return false;
		}
	}

	private static string Normalize(string? mode, string? json)
	{
		if (string.IsNullOrEmpty(json)) return "";
		try
		{
			object? o = mode switch
			{
				"Ekran" => JsonSerializer.Deserialize<ScreenOptions>(json),
				"Ses" => JsonSerializer.Deserialize<AudioOptions>(json),
				"Spotify" => JsonSerializer.Deserialize<SpotifyOptions>(json),
				"Gökkuşağı" => JsonSerializer.Deserialize<RainbowOptions>(json),
				"Şömine" => JsonSerializer.Deserialize<FireOptions>(json),
				"Gün ışığı" => JsonSerializer.Deserialize<DaylightOptions>(json),
				_ => null,
			};
			return o == null ? json : JsonSerializer.Serialize(o, o.GetType());
		}
		catch
		{
			return json;
		}
	}

	public Scene Clone() => JsonSerializer.Deserialize<Scene>(JsonSerializer.Serialize(this))!;

	public static List<Scene> Defaults()
	{
		string Opt<T>(T o) => JsonSerializer.Serialize(o);
		return new List<Scene>
		{
			new Scene { Id = "film", BuiltIn = true, Emoji = "🎬", Name = "Film", Kind = "Mod", Mode = "Ekran", Brightness = 45,
				ModeOptions = Opt(new ScreenOptions { IntervalMs = 120, Smoothing = 65, Method = 1, SaturationBoost = 20, IgnoreDark = true, FollowBrightness = true }) },
			new Scene { Id = "oyun", BuiltIn = true, Emoji = "🎮", Name = "Oyun", Kind = "Mod", Mode = "Ekran", Brightness = 90,
				ModeOptions = Opt(new ScreenOptions { IntervalMs = 50, Smoothing = 15, Method = 1, SaturationBoost = 45, IgnoreDark = true, FollowBrightness = true }) },
			new Scene { Id = "okuma", BuiltIn = true, Emoji = "📖", Name = "Okuma", Kind = "Beyaz", Kelvin = 4300, Brightness = 90 },
			new Scene { Id = "uyku", BuiltIn = true, Emoji = "🌙", Name = "Uyku", Kind = "Beyaz", Kelvin = 2200, Brightness = 8 },
			new Scene { Id = "odak", BuiltIn = true, Emoji = "🎯", Name = "Odak", Kind = "Beyaz", Kelvin = 5600, Brightness = 100 },
			new Scene { Id = "romantik", BuiltIn = true, Emoji = "🌹", Name = "Romantik", Kind = "Mod", Mode = "Şömine", Brightness = 40,
				ModeOptions = Opt(new FireOptions { Intensity = 35, Speed = 3, Warmth = 15, Brightness = 40, Sparks = false }) },
		};
	}
}
