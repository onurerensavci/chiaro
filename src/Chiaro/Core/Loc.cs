#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Markup;

namespace HueStudio;

/// <summary>
/// Arayüz dili (tr / en). Metinler Strings.Tr ve Strings.En sözlüklerinde aynı anahtarlarla durur.
/// XAML: {l:T anahtar} (canlı bağlama), kod: L.T("anahtar") / L.F("anahtar", değerler).
/// Dil değişince Changed tetiklenir; XAML bağlamaları kendiliğinden, kodla yazılan metinler
/// MainWindow.ApplyLanguage ile yenilenir.
/// </summary>
public sealed class L : INotifyPropertyChanged
{
	public const string Auto = "auto";
	public const string Turkish = "tr";
	public const string English = "en";

	public static L I { get; } = new L();

	private static Dictionary<string, string> table = Strings.Tr;

	/// <summary>Etkin dil: "tr" ya da "en".</summary>
	public static string Lang { get; private set; } = Turkish;

	public static bool En => Lang == English;

	public static event Action? Changed;

	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Sözlükte olmayan anahtarlar (duman testi bunları raporlar).</summary>
	internal static readonly HashSet<string> Missing = new HashSet<string>();

	public string this[string key] => T(key);

	public static string T(string key)
	{
		if (table.TryGetValue(key, out string? v)) return v;
		lock (Missing) Missing.Add(key);
		return key;
	}

	public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

	/// <summary>Yüzde: Türkçede "%72", İngilizcede "72%".</summary>
	public static string Pct(double value) => En ? $"{value:F0}%" : $"%{value:F0}";

	private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");

	/// <summary>Dile uygun büyük harf (Türkçede i → İ).</summary>
	public static string Upper(string s) => En ? s.ToUpperInvariant() : s.ToUpper(TrCulture);

	/// <summary>Ayar değeri ("auto"/"tr"/"en") ve sistem diline göre etkin dil.</summary>
	public static string Resolve(string? setting, CultureInfo? ui = null)
	{
		if (setting == Turkish || setting == English) return setting;
		return SystemLanguage(ui);
	}

	/// <summary>Windows arayüz dili Türkçeyse "tr", değilse "en".</summary>
	public static string SystemLanguage(CultureInfo? ui = null)
	{
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
		if (!changed) return;
		I.PropertyChanged?.Invoke(I, new PropertyChangedEventArgs("Item[]"));
		Changed?.Invoke();
	}

	/// <summary>Değişmeyen kodun (ör. Bluetooth katmanı) Türkçe iletisini çevirir; bilinmiyorsa aynen döner.</summary>
	public static string FromTr(string? turkish)
	{
		if (string.IsNullOrEmpty(turkish) || !En) return turkish ?? "";
		if (reverse == null)
		{
			Dictionary<string, string> r = new Dictionary<string, string>();
			foreach (var kv in Strings.Tr) r.TryAdd(kv.Value, kv.Key);
			reverse = r;
		}
		return reverse.TryGetValue(turkish, out string? key) ? T(key) : turkish;
	}

	private static Dictionary<string, string>? reverse;

	/// <summary>
	/// Bluetooth katmanının birleşik iletisini ("aşama: neden (durum)") parça parça çevirir.
	/// Bilinmeyen parçalar olduğu gibi kalır.
	/// </summary>
	public static string Message(string? message)
	{
		if (string.IsNullOrEmpty(message) || !En) return message ?? "";
		return string.Join(": ", message.Split(": ").Select(Piece));
	}

	private static string Piece(string p)
	{
		string direct = FromTr(p);
		if (direct != p) return direct;
		// "Baş (ayrıntı). Kuyruk" ya da "Baş (ayrıntı)."
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

	// ---- Kimlikler Türkçe kalır (ayarlarda saklanır); yalnızca görünen ad çevrilir ----

	private static readonly Dictionary<string, string> ModeKeys = new Dictionary<string, string>
	{
		["Manuel"] = "mode_manual", ["Ekran"] = "mode_screen", ["Ses"] = "mode_audio", ["Spotify"] = "mode_spotify",
		["Gökkuşağı"] = "mode_rainbow", ["Şömine"] = "mode_fire", ["Gün ışığı"] = "mode_daylight",
		["Uyku"] = "mode_sleep", ["Uyandırma"] = "mode_wake",
	};

	/// <summary>Mod kimliğinin ("Ekran", "Şömine"…) görünen adı.</summary>
	public static string Mode(string? id) => id != null && ModeKeys.TryGetValue(id, out string? k) ? T(k) : id ?? "";

	/// <summary>Palet renginin (Türkçe kimlik) görünen adı.</summary>
	public static string Color(string? trName) => trName == null ? "" : Strings.ColorKeys.TryGetValue(trName, out string? k) ? T(k) : trName;

	/// <summary>Kısa gün adları, Pazartesi'den başlayarak.</summary>
	public static string[] DaysShort => T("days_short").Split(',');

	public static string[] DaysLong => T("days_long").Split(',');

	internal static IEnumerable<string> AllKeys => Strings.Tr.Keys.Union(Strings.En.Keys);
}

/// <summary>XAML: Text="{l:T anahtar}" — dil değişince kendiliğinden güncellenir.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
	public TExtension() { }

	public TExtension(string key) => Key = key;

	[ConstructorArgument("key")]
	public string Key { get; set; } = "";

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		Binding b = new Binding("[" + Key + "]") { Source = L.I, Mode = BindingMode.OneWay };
		return b.ProvideValue(serviceProvider);
	}
}

/// <summary>
/// Dile göre biçimlendirme: ConverterParameter "pct" → L.Pct, aksi hâlde L.F(anahtar, değer).
/// Dil değişince MainWindow bağlamaları UpdateTarget ile yeniler.
/// </summary>
public sealed class FmtConverter : IValueConverter
{
	public static FmtConverter I { get; } = new FmtConverter();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string key = parameter as string ?? "pct";
		double d = value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0;
		return key == "pct" ? L.Pct(d) : L.F(key, d);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
