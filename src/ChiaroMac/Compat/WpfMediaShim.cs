#nullable enable
// Windows çekirdeğindeki (Scenes.cs, Effects.cs) System.Windows.Media.Color kullanımını
// Windows kaynağına dokunmadan macOS/Linux'ta derleyebilmek için asgari eşlenik.
// Yalnızca paylaşılan çekirdeğin kullandığı üyeler vardır.
using System;
using System.Globalization;

namespace System.Windows.Media
{
	public readonly struct Color : IEquatable<Color>
	{
		public byte A { get; init; }
		public byte R { get; init; }
		public byte G { get; init; }
		public byte B { get; init; }

		public static Color FromRgb(byte r, byte g, byte b) => new Color { A = 255, R = r, G = g, B = b };

		public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color { A = a, R = r, G = g, B = b };

		public bool Equals(Color other) => A == other.A && R == other.R && G == other.G && B == other.B;

		public override bool Equals(object? obj) => obj is Color c && Equals(c);

		public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;

		public static bool operator ==(Color a, Color b) => a.Equals(b);

		public static bool operator !=(Color a, Color b) => !a.Equals(b);

		public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
	}

	public static class Colors
	{
		public static Color White => Color.FromRgb(255, 255, 255);
		public static Color Black => Color.FromRgb(0, 0, 0);
		public static Color DimGray => Color.FromRgb(105, 105, 105);
		public static Color Transparent => Color.FromArgb(0, 255, 255, 255);
	}

	public static class ColorConverter
	{
		/// <summary>WPF gibi: "#RGB", "#RRGGBB", "#AARRGGBB"; geçersizse FormatException.</summary>
		public static object ConvertFromString(string value)
		{
			string t = (value ?? throw new FormatException("null")).Trim();
			if (!t.StartsWith('#')) throw new FormatException("Renk biçimi: " + value);
			string h = t[1..];
			if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
			if (h.Length == 6) h = "FF" + h;
			if (h.Length != 8 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
				throw new FormatException("Renk biçimi: " + value);
			return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
		}
	}
}

namespace HueStudio
{
	/// <summary>StudioSettings.KnownUpdate için (Windows'taki Updater.cs ile aynı alanlar; Mac test sürümünde güncelleyici kapalı).</summary>
	public sealed class UpdateInfo
	{
		public string Tag { get; set; } = "";
		public string VersionText { get; set; } = "";
		public string Notes { get; set; } = "";
		public string AssetName { get; set; } = "";
		public string AssetUrl { get; set; } = "";
		public long AssetSize { get; set; }
		public string? ShaUrl { get; set; }
		public string? Sha256 { get; set; }
		public string PageUrl { get; set; } = "";
	}
}
