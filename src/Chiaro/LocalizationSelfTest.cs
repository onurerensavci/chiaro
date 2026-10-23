#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace HueStudio;

/// <summary>3.3 dil desteği duman testleri: sözlük eşliği ve canlı geçişte Türkçe kalıntı taraması.</summary>
internal static class LocalizationSelfTest
{
	/// <summary>Kullanıcı verisi / özel adlar: dil fark etmeksizin aynı kalır.</summary>
	private static readonly string[] Allowed = { "Onur Eren Savcı", "Türkçe", "Dil / Language" };

	private static readonly Regex Placeholder = new Regex(@"\{(\d+)(:[^}]*)?\}");

	internal static string DictionaryCheck()
	{
		List<string> problems = new List<string>();
		foreach (string k in Strings.Tr.Keys.Except(Strings.En.Keys)) problems.Add("only in Tr: " + k);
		foreach (string k in Strings.En.Keys.Except(Strings.Tr.Keys)) problems.Add("only in En: " + k);
		foreach (var kv in Strings.Tr.Where(x => string.IsNullOrWhiteSpace(x.Value))) problems.Add("empty Tr: " + kv.Key);
		foreach (var kv in Strings.En.Where(x => string.IsNullOrWhiteSpace(x.Value))) problems.Add("empty En: " + kv.Key);
		foreach (string k in Strings.Tr.Keys.Intersect(Strings.En.Keys))
		{
			string a = string.Join(",", Placeholder.Matches(Strings.Tr[k]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
			string b = string.Join(",", Placeholder.Matches(Strings.En[k]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
			if (a != b) problems.Add($"placeholders differ: {k} ({a} vs {b})");
			if (Strings.Tr[k].Split(',').Length != Strings.En[k].Split(',').Length && (k.StartsWith("days_") || k.StartsWith("rb_") || k.StartsWith("sc_")))
				problems.Add("list length differs: " + k);
		}
		if (problems.Count > 0) throw new InvalidOperationException("Strings: " + string.Join("; ", problems));
		return $"\nLocalization dictionaries: {Strings.Tr.Count} keys, Tr == En key sets, no empty values, placeholders match: PASS.";
	}

	internal static string DetectionCheck()
	{
		Check(L.Resolve("auto", CultureInfo.GetCultureInfo("tr-TR")) == "tr", "auto + tr-TR -> tr");
		Check(L.Resolve("auto", CultureInfo.GetCultureInfo("en-US")) == "en", "auto + en-US -> en");
		Check(L.Resolve("auto", CultureInfo.GetCultureInfo("de-DE")) == "en", "auto + de-DE -> en");
		Check(L.Resolve("tr", CultureInfo.GetCultureInfo("en-US")) == "tr", "tr forced");
		Check(L.Resolve("en", CultureInfo.GetCultureInfo("tr-TR")) == "en", "en forced");
		Check(L.Resolve("garbage", CultureInfo.GetCultureInfo("tr-TR")) == "tr", "unknown -> auto");
		return $"\nLanguage auto-detect: tr-TR->tr, en-US/de-DE->en, forced tr/en honored; this process: CurrentUICulture={CultureInfo.CurrentUICulture.Name}, InstalledUICulture={CultureInfo.InstalledUICulture.Name} -> auto={L.SystemLanguage()}: PASS.";
	}

	/// <summary>Türkçe metin kalmış mı? (Türkçe harf ya da İngilizcesi farklı bir Türkçe sözlük değeri.)</summary>
	internal static List<string> FindTurkish(IEnumerable<string> texts)
	{
		HashSet<string> trOnly = new HashSet<string>(Strings.Tr.Where(kv => Strings.En.TryGetValue(kv.Key, out string? en) && en != kv.Value && kv.Value.Length > 2).Select(kv => kv.Value));
		List<string> bad = new List<string>();
		foreach (string raw in texts)
		{
			string t = raw.Trim();
			if (t.Length == 0) continue;
			string probe = Allowed.Aggregate(t, (s, a) => s.Replace(a, ""));
			// Türkçe harf, İngilizcesi farklı bir Türkçe sözlük değeri ya da Türkçe yüzde biçimi (%72).
			if (Regex.IsMatch(probe, "[çğıöşüÇĞİÖŞÜ]") || trOnly.Contains(t) || Regex.IsMatch(t, @"(^|\s)%\d")) bad.Add(t);
		}
		return bad.Distinct().ToList();
	}

	/// <summary>Bir pencerenin (kapalı bölümler dahil) tüm görünen metinleri.</summary>
	internal static IEnumerable<string> Texts(DependencyObject root)
	{
		foreach (DependencyObject o in MainWindow.LogicalAndVisual(root))
		{
			switch (o)
			{
				case TextBlock tb:
					yield return tb.Text;
					break;
				case HeaderedItemsControl h when h.Header is string hs:
					yield return hs;
					break;
				case ContentControl cc when cc.Content is string cs:
					yield return cs;
					break;
			}
			if (o is ComboBox cb && cb.ItemsSource != null)
			{
				foreach (object item in cb.ItemsSource) if (item is string s) yield return s;
			}
			if (o is FrameworkElement fe)
			{
				if (fe.ToolTip is string tip) yield return tip;
				if (fe.ContextMenu != null)
				{
					foreach (object mi in fe.ContextMenu.Items) if (mi is MenuItem m && m.Header is string mh) yield return mh;
				}
			}
			if (o is Window w) yield return w.Title;
		}
	}

	internal static string StaleScan(string label, IEnumerable<string> texts)
	{
		List<string> bad = FindTurkish(texts);
		if (bad.Count > 0) throw new InvalidOperationException($"{label}: Turkish text left after switching to English: " + string.Join(" | ", bad.Take(25)));
		if (L.Missing.Count > 0) throw new InvalidOperationException("Missing string keys: " + string.Join(", ", L.Missing));
		return label;
	}

	private static void Check(bool ok, string name)
	{
		if (!ok) throw new InvalidOperationException("Language test: " + name);
	}
}
