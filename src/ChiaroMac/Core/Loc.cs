#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace HueStudio;

/// <summary>
/// Arayüz dili (tr / en) — Windows'taki L sınıfının WPF'siz eşleniği.
/// Metinler Windows ile ortak Strings.Tr / Strings.En sözlüklerinden gelir; macOS'a özgü
/// ifadeler MacStrings ile üstüne yazılır (aynı anahtar varsa Mac metni önceliklidir).
/// </summary>
public sealed class L : INotifyPropertyChanged
{
	public const string Auto = "auto";
	public const string Turkish = "tr";
	public const string English = "en";

	public static L I { get; } = new L();

	private static Dictionary<string, string> table = Strings.Tr;
	private static Dictionary<string, string> overlay = MacStrings.Tr;

	public static string Lang { get; private set; } = Turkish;

	public static bool En => Lang == English;

	public static event Action? Changed;

	public event PropertyChangedEventHandler? PropertyChanged;

	internal static readonly HashSet<string> Missing = new HashSet<string>();

	public string this[string key] => T(key);

	public static string T(string key)
	{
		if (overlay.TryGetValue(key, out string? m)) return m;
		if (table.TryGetValue(key, out string? v)) return v;
		lock (Missing) Missing.Add(key);
		return key;
	}

	public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

	public static string Pct(double value) => En ? $"{value:F0}%" : $"%{value:F0}";

	private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");

	public static string Upper(string s) => En ? s.ToUpperInvariant() : s.ToUpper(TrCulture);

	public static string Resolve(string? setting, CultureInfo? ui = null)
	{
		if (setting == Turkish || setting == English) return setting;
		return SystemLanguage(ui);
	}

	/// <summary>Sistem dili Türkçeyse "tr", değilse "en". macOS'ta tercih edilen dil listesi de okunur.</summary>
	public static string SystemLanguage(CultureInfo? ui = null)
	{
		if (ui == null && SystemLanguageProbe.PreferredLanguage() is string pref)
		{
			return pref.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? Turkish : English;
		}
		CultureInfo c = ui ?? CultureInfo.CurrentUICulture;
		if (c == null || c.Equals(CultureInfo.InvariantCulture)) c = CultureInfo.InstalledUICulture;
		return c.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase) ? Turkish : English;
	}

	public static void Apply(string? setting)
	{
		string lang = Resolve(setting);
		bool changed = lang != Lang;
		Lang = lang;
		table = lang == English ? Strings.En : Strings.Tr;
		overlay = lang == English ? MacStrings.En : MacStrings.Tr;
		if (!changed) return;
		I.PropertyChanged?.Invoke(I, new PropertyChangedEventArgs("Item[]"));
		Changed?.Invoke();
	}

	/// <summary>Bluetooth katmanının Türkçe iletisini çevirir; bilinmiyorsa aynen döner.</summary>
	public static string FromTr(string? turkish)
	{
		if (string.IsNullOrEmpty(turkish) || !En) return turkish ?? "";
		if (reverse == null)
		{
			Dictionary<string, string> r = new Dictionary<string, string>();
			foreach (var kv in MacStrings.Tr) r.TryAdd(kv.Value, kv.Key);
			foreach (var kv in Strings.Tr) r.TryAdd(kv.Value, kv.Key);
			reverse = r;
		}
		return reverse.TryGetValue(turkish, out string? key) ? T(key) : turkish;
	}

	private static Dictionary<string, string>? reverse;

	public static string Message(string? message)
	{
		if (string.IsNullOrEmpty(message) || !En) return message ?? "";
		return string.Join(": ", message.Split(": ").Select(Piece));
	}

	private static string Piece(string p)
	{
		string direct = FromTr(p);
		if (direct != p) return direct;
		var m = System.Text.RegularExpressions.Regex.Match(p, @"^(.*?) (\([^()]*\))(.*)$");
		if (m.Success)
		{
			string head = FromTr(m.Groups[1].Value);
			string tail = m.Groups[3].Value;
			if (tail.StartsWith(". ")) tail = ". " + FromTr(tail[2..]);
			return head + " " + m.Groups[2].Value + tail;
		}
		return p.Replace("  •  Yazılım", "  •  " + T("ble_firmware"));
	}

	private static readonly Dictionary<string, string> ModeKeys = new Dictionary<string, string>
	{
		["Manuel"] = "mode_manual", ["Ekran"] = "mode_screen", ["Ses"] = "mode_audio", ["Spotify"] = "mode_spotify",
		["Gökkuşağı"] = "mode_rainbow", ["Şömine"] = "mode_fire", ["Gün ışığı"] = "mode_daylight",
		["Uyku"] = "mode_sleep", ["Uyandırma"] = "mode_wake",
	};

	public static string Mode(string? id) => id != null && ModeKeys.TryGetValue(id, out string? k) ? T(k) : id ?? "";

	public static string Color(string? trName) => trName == null ? "" : Strings.ColorKeys.TryGetValue(trName, out string? k) ? T(k) : trName;

	public static string[] DaysShort => T("days_short").Split(',');

	public static string[] DaysLong => T("days_long").Split(',');

	internal static IEnumerable<string> AllKeys => Strings.Tr.Keys.Union(Strings.En.Keys).Union(MacStrings.Tr.Keys).Union(MacStrings.En.Keys);
}

/// <summary>macOS'ta sistemin tercih ettiği ilk dili okur (AppleLanguages); diğer sistemlerde LANG.</summary>
public static class SystemLanguageProbe
{
	public static Func<string?>? Override;

	public static string? PreferredLanguage()
	{
		if (Override != null) return Override();
		try
		{
			if (OperatingSystem.IsMacOS())
			{
				string? l = MacInterop.Objc.PreferredLanguage();
				if (!string.IsNullOrEmpty(l)) return l;
			}
		}
		catch
		{
		}
		return null;
	}
}
