#nullable enable
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace HueStudio;

/// <summary>Renk dönüşümleri (yalnızca arayüz/efekt hesapları; cihaz protokolü Protocol.cs içinde değişmeden duruyor).</summary>
public static class ColorMath
{
	public static Color FromHsv(double h, double s, double v)
	{
		h = ((h % 360.0) + 360.0) % 360.0;
		s = Math.Clamp(s, 0.0, 1.0);
		v = Math.Clamp(v, 0.0, 1.0);
		double c = v * s;
		double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
		double m = v - c;
		(double r, double g, double b) = (int)(h / 60.0) switch
		{
			0 => (c, x, 0.0),
			1 => (x, c, 0.0),
			2 => (0.0, c, x),
			3 => (0.0, x, c),
			4 => (x, 0.0, c),
			_ => (c, 0.0, x),
		};
		return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
	}

	public static (double H, double S, double V) ToHsv(Color c)
	{
		double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		double d = max - min;
		double h = 0;
		if (d > 1e-9)
		{
			if (max == r) h = 60 * (((g - b) / d) % 6);
			else if (max == g) h = 60 * (((b - r) / d) + 2);
			else h = 60 * (((r - g) / d) + 4);
		}
		if (h < 0) h += 360;
		return (h, max <= 0 ? 0 : d / max, max);
	}

	public static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);

	public static Color Lerp(Color a, Color b, double t)
	{
		t = Math.Clamp(t, 0, 1);
		return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
	}

	/// <summary>Yaklaşık Kelvin → RGB (yalnızca ekrandaki önizleme için).</summary>
	public static Color FromKelvin(double kelvin)
	{
		double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
		double r, g, b;
		if (t <= 66) r = 255; else r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
		if (t <= 66) g = 99.4708025861 * Math.Log(t) - 161.1195681661; else g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
		if (t >= 66) b = 255; else if (t <= 19) b = 0; else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
		return Color.FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
	}

	public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

	public static bool TryParseHex(string? text, out Color color)
	{
		color = Colors.White;
		if (string.IsNullOrWhiteSpace(text)) return false;
		string t = text.Trim();
		if (!t.StartsWith('#')) t = "#" + t;
		if (t.Length == 4) t = $"#{t[1]}{t[1]}{t[2]}{t[2]}{t[3]}{t[3]}";
		if (t.Length != 7) return false;
		try
		{
			color = (Color)ColorConverter.ConvertFromString(t);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}
}

public sealed record NamedColor(string Name, string Hex, string Group)
{
	public Color Color => (Color)ColorConverter.ConvertFromString(Hex);
}

public static class Palette
{
	public static readonly IReadOnlyList<NamedColor> All = new List<NamedColor>
	{
		new("Kırmızı", "#FF2D2D", "Kırmızılar"),
		new("Kiraz", "#D2042D", "Kırmızılar"),
		new("Mercan", "#FF654D", "Kırmızılar"),
		new("Somon", "#FA8072", "Kırmızılar"),
		new("Gül", "#FF3F7F", "Kırmızılar"),
		new("Nar", "#C0392B", "Kırmızılar"),

		new("Turuncu", "#FF8C1A", "Turuncu ve sarı"),
		new("Gün batımı", "#FF5E3A", "Turuncu ve sarı"),
		new("Kehribar", "#FFB000", "Turuncu ve sarı"),
		new("Bal", "#F5B041", "Turuncu ve sarı"),
		new("Altın", "#FFD166", "Turuncu ve sarı"),
		new("Limon", "#FFF44F", "Turuncu ve sarı"),
		new("Şeftali", "#FFB38A", "Turuncu ve sarı"),

		new("Yeşil", "#2ECC40", "Yeşiller"),
		new("Nane", "#65D28A", "Yeşiller"),
		new("Zümrüt", "#00A86B", "Yeşiller"),
		new("Çimen", "#7CFC00", "Yeşiller"),
		new("Fıstık", "#A8E063", "Yeşiller"),
		new("Zeytin", "#8DB600", "Yeşiller"),
		new("Deniz yeşili", "#20B2AA", "Yeşiller"),

		new("Turkuaz", "#40E0D0", "Maviler"),
		new("Camgöbeği", "#00E5FF", "Maviler"),
		new("Gökyüzü", "#87CEEB", "Maviler"),
		new("Okyanus", "#528DFF", "Maviler"),
		new("Mavi", "#1E5BFF", "Maviler"),
		new("Safir", "#0F52BA", "Maviler"),
		new("Lacivert", "#1A2A8C", "Maviler"),
		new("Buz mavisi", "#A5F2F3", "Maviler"),

		new("Lavanta", "#BD77F4", "Mor ve pembe"),
		new("Mor", "#8A2BE2", "Mor ve pembe"),
		new("Ametist", "#9966CC", "Mor ve pembe"),
		new("Eflatun", "#C154C1", "Mor ve pembe"),
		new("Leylak", "#C8A2C8", "Mor ve pembe"),
		new("Pembe", "#FF69B4", "Mor ve pembe"),
		new("Fuşya", "#FF00FF", "Mor ve pembe"),
		new("Magenta", "#E0218A", "Mor ve pembe"),
		new("Pudra", "#FFC0CB", "Mor ve pembe"),

		new("Mum ışığı", "#FFB46B", "Beyaz ve özel"),
		new("Sıcak beyaz", "#FFD8A8", "Beyaz ve özel"),
		new("Gün ışığı", "#FFF4E5", "Beyaz ve özel"),
		new("Soğuk beyaz", "#E8F1FF", "Beyaz ve özel"),
		new("Ay ışığı", "#C9D6FF", "Beyaz ve özel"),
		new("Neon pembe", "#FF10F0", "Beyaz ve özel"),
		new("Neon yeşil", "#39FF14", "Beyaz ve özel"),
		new("Elektrik mavisi", "#7DF9FF", "Beyaz ve özel"),
	};

	public static readonly string[] DefaultFavorites =
	{
		"#FF2D2D", "#FF8C1A", "#FFD166", "#65D28A", "#40E0D0", "#528DFF", "#8A2BE2", "#FF69B4", "#FFB46B", "#E8F1FF"
	};

	public static string NameOf(Color c)
	{
		string hex = ColorMath.Hex(c);
		foreach (NamedColor n in All)
		{
			if (string.Equals(n.Hex, hex, StringComparison.OrdinalIgnoreCase))
			{
				return L.Color(n.Name);
			}
		}
		return hex;
	}
}

public enum RainbowDirection
{
	TopToBottom = 0,
	BottomToTop = 1,
	PingPong = 2,
}

public static class RainbowEngine
{
	public static string[] PaletteNames => L.T("rb_palettes").Split(',');

	public static string[] DirectionNames => L.T("rb_directions").Split(',');

	/// <summary>0..1 arası konum (önizleme çubuğunda yukarıdan aşağı).</summary>
	public static double Position(double seconds, double cycleSeconds, RainbowDirection direction)
	{
		double cycle = Math.Max(1.0, cycleSeconds);
		double t = seconds / cycle;
		double frac = ((t % 1.0) + 1.0) % 1.0;
		return direction switch
		{
			RainbowDirection.BottomToTop => 1.0 - frac,
			RainbowDirection.PingPong => Triangle(seconds / (cycle / 2.0) / 2.0),
			_ => frac,
		};
	}

	private static double Triangle(double t)
	{
		double f = ((t % 1.0) + 1.0) % 1.0 * 2.0;
		return f <= 1.0 ? f : 2.0 - f;
	}

	/// <summary>Konum (0 = üst, 1 = alt) için paletteki renk.</summary>
	public static Color ColorAt(double position, int palette, double saturation)
	{
		double p = Math.Clamp(position, 0.0, 1.0);
		double s = Math.Clamp(saturation, 0.05, 1.0);
		switch (palette)
		{
			case 1: // Sıcak: kırmızı → turuncu → sarı → turuncu → kırmızı (dikişsiz)
				return ColorMath.FromHsv(2 + 52 * Bump(p), s, 1);
			case 2: // Soğuk: camgöbeği → mavi → mor
				return ColorMath.FromHsv(170 + 110 * Bump(p), s, 1);
			case 3: // Pastel
				return ColorMath.FromHsv(p * 360.0, s * 0.42, 1);
			case 4: // Gün batımı: mor → pembe → turuncu → altın
				return ColorMath.FromHsv(285 + 120 * Bump(p), s, 1);
			case 5: // Orman: sarı-yeşil → yeşil → deniz yeşili
				return ColorMath.FromHsv(70 + 100 * Bump(p), s, 1);
			default:
				return FullSpectrum(p, s);
		}
	}

	private static Color FullSpectrum(double p, double s) => ColorMath.FromHsv(p * 360.0, s, 1);

	/// <summary>0→1→0 yumuşak gidiş-dönüş; sınırlı paletlerde başa dönerken renk sıçramasını önler.</summary>
	private static double Bump(double p) => 0.5 - 0.5 * Math.Cos(p * Math.PI * 2.0);
}

public sealed class FireEngine
{
	private readonly Random random = new Random();
	private double level = 0.6;
	private double target = 0.6;
	private double flare;
	private double ember = 0.5;

	/// <param name="intensity">0..1 titreme şiddeti</param>
	/// <param name="speed">1..10</param>
	/// <param name="warmth">0 = kor kırmızısı, 1 = sarı alev</param>
	/// <param name="baseBrightness">10..100 yüzde</param>
	public (Color Color, double Brightness) Next(double intensity, double speed, double warmth, double baseBrightness, bool sparks)
	{
		double k = 0.25 + 0.05 * Math.Clamp(speed, 1, 10);
		if (random.NextDouble() < 0.35 + 0.04 * speed)
		{
			target = 0.15 + 0.85 * random.NextDouble();
		}
		level += (target - level) * k;
		ember += ((random.NextDouble()) - ember) * 0.08;
		if (sparks && flare <= 0.01 && random.NextDouble() < 0.035)
		{
			flare = 0.6 + 0.4 * random.NextDouble();
		}
		flare *= 0.55;

		double l = Math.Clamp(level + flare * 0.5, 0, 1);
		double w = Math.Clamp(warmth, 0, 1);
		double hueLow = 1 + 9 * w;     // koyu kırmızı / turuncu-kırmızı
		double hueHigh = 22 + 30 * w;  // turuncu / sarı
		double hue = hueLow + (hueHigh - hueLow) * (0.65 * l + 0.35 * ember);
		double sat = 1.0 - 0.12 * flare;
		Color color = ColorMath.FromHsv(hue, sat, 1.0);

		double i = Math.Clamp(intensity, 0, 1);
		double b = baseBrightness * (1.0 - i * 0.65 * (1.0 - l)) + baseBrightness * 0.35 * flare;
		return (color, Math.Clamp(b, 1, 100));
	}

	public static int FrameMilliseconds(double speed) => (int)Math.Round(400 - Math.Clamp(speed, 1, 10) * 28);
}
