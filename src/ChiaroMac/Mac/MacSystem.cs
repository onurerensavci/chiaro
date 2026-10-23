#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>Yalnızca Mac sürümüne ait tercihler (Windows ile ortak studio-v2.json'a dokunmadan).</summary>
public sealed class MacPrefs
{
	public int AudioSource { get; set; }          // 0 sistem sesi, 1 mikrofon
	public double? MiniLeft { get; set; }
	public double? MiniTop { get; set; }
	public string MiniPanel { get; set; } = "";
	public bool AutoUpdateCheck { get; set; } = true;
	public DateTime? LastUpdateCheck { get; set; }
	public string? SkippedVersion { get; set; }

	public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "HueBluetooth", "mac-prefs.json");

	public static MacPrefs Load()
	{
		try
		{
			if (File.Exists(FilePath)) return JsonSerializer.Deserialize<MacPrefs>(File.ReadAllText(FilePath)) ?? new MacPrefs();
		}
		catch (Exception ex)
		{
			AppLog.Write("Mac tercihleri okunamadı: " + ex.Message);
		}
		return new MacPrefs();
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception ex)
		{
			AppLog.Write("Mac tercihleri yazılamadı: " + ex.Message);
		}
	}
}

/// <summary>
/// Oturum açılışında başlatma: ~/Library/LaunchAgents içinde bir LaunchAgent. İmzasız/ad-hoc uygulamalarda
/// SMAppService'ten daha öngörülebilir. Uygulama "open" ile (LaunchServices) arka planda, menü çubuğunda açılır.
/// </summary>
public static class LoginItem
{
	public const string Label = "io.github.onurerensavci.chiaro";
	public const string BackgroundArgument = "--background";

	public static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

	/// <summary>Çalışan .app paketinin yolu (…/Chiaro.app); paket dışında çalışıyorsa null.</summary>
	public static string? BundlePath(string? processPath = null)
	{
		string? exe = processPath ?? Environment.ProcessPath;
		if (string.IsNullOrEmpty(exe)) return null;
		DirectoryInfo? macos = Directory.GetParent(exe);
		DirectoryInfo? contents = macos?.Parent;
		DirectoryInfo? app = contents?.Parent;
		if (macos?.Name != "MacOS" || contents?.Name != "Contents" || app == null || !app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
		return app.FullName;
	}

	/// <summary>Gatekeeper "App Translocation" ile rastgele salt-okunur bir yoldan çalışıyorsa (indirilenlerden açıldıysa).</summary>
	public static bool IsTranslocated(string? bundle) => bundle != null && bundle.Contains("/AppTranslocation/", StringComparison.Ordinal);

	public static bool IsEnabled() => File.Exists(PlistPath);

	public static string Plist(string bundle) => $"""
		<?xml version="1.0" encoding="UTF-8"?>
		<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
		<plist version="1.0">
		<dict>
			<key>Label</key>
			<string>{Label}</string>
			<key>ProgramArguments</key>
			<array>
				<string>/usr/bin/open</string>
				<string>-g</string>
				<string>-a</string>
				<string>{System.Security.SecurityElement.Escape(bundle)}</string>
				<string>--args</string>
				<string>{BackgroundArgument}</string>
			</array>
			<key>RunAtLoad</key>
			<true/>
			<key>ProcessType</key>
			<string>Interactive</string>
		</dict>
		</plist>
		""";

	/// <summary>Açar/kapatır; hata iletisi ya da null.</summary>
	public static string? Apply(bool enabled)
	{
		try
		{
			if (!enabled)
			{
				if (File.Exists(PlistPath)) File.Delete(PlistPath);
				return null;
			}
			string? bundle = BundlePath();
			if (bundle == null) return "Uygulama paketi bulunamadı.";
			if (IsTranslocated(bundle)) return "Önce Chiaro'yu Uygulamalar klasörüne taşı, sonra yeniden aç.";
			Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
			File.WriteAllText(PlistPath, Plist(bundle));
			return null;
		}
		catch (Exception ex)
		{
			return ex.Message;
		}
	}
}

public sealed record MacUpdate(string Version, string Tag, string AssetName, string AssetUrl, long Size, string? Sha256, string PageUrl, string Notes);

/// <summary>
/// GitHub Releases'te Mac paketi arar: Chiaro_vX.Y[.Z]_mac.zip. Yalnızca .exe olan sürümler Mac için "güncelleme yok" sayılır.
/// Bulunursa zip İndirilenler'e indirilir, SHA-256 doğrulanır ve Finder'da gösterilir (uygulama kendini değiştirmez).
/// </summary>
public static class MacUpdater
{
	public const string Repo = "onurerensavci/chiaro";
	public const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
	public static readonly Regex AssetRx = new Regex(@"^Chiaro_v?(\d+(?:\.\d+){1,3})_mac\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static Version Current
	{
		get
		{
			string v = Program.Version.Split('-')[0];
			return Version.TryParse(v, out Version? r) ? r : new Version(0, 0);
		}
	}

	public static bool TryParse(string? text, out Version version)
	{
		version = new Version(0, 0);
		if (string.IsNullOrWhiteSpace(text)) return false;
		Match m = Regex.Match(text, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
		if (!m.Success) return false;
		int P(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
		version = new Version(P(1), P(2), P(3), P(4));
		return true;
	}

	public static int Compare(Version a, Version b) =>
		new Version(a.Major, a.Minor, Math.Max(0, a.Build), Math.Max(0, a.Revision)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build), Math.Max(0, b.Revision)));

	/// <summary>Sürüm JSON'unu değerlendirir: (yeni Mac paketi | null, açıklama anahtarı).</summary>
	public static (MacUpdate? Update, string Status) Evaluate(string json, Version current)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement r = doc.RootElement;
		string tag = r.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
		if (!TryParse(tag, out Version latest)) return (null, "mac_upd_none");
		if (Compare(latest, current) <= 0) return (null, "mac_upd_latest");
		if (!r.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) return (null, "mac_upd_no_mac");
		foreach (JsonElement a in assets.EnumerateArray())
		{
			string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
			if (!AssetRx.IsMatch(name)) continue;
			string url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
			if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
			long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long sz) ? sz : 0;
			string? digest = a.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
			string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
			string page = r.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "https://github.com/" + Repo + "/releases";
			string notes = r.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
			return (new MacUpdate($"{latest.Major}.{latest.Minor}" + (latest.Build > 0 ? $".{latest.Build}" : ""), tag, name, url, size, sha, page, notes), "mac_upd_available");
		}
		return (null, "mac_upd_no_mac");
	}

	private static HttpClient Client()
	{
		HttpClient c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Chiaro-mac", Current.ToString()));
		c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		return c;
	}

	public static async Task<(MacUpdate? Update, string Status)> CheckAsync(CancellationToken ct)
	{
		string? feed = Environment.GetEnvironmentVariable("CHIARO_UPDATE_FEED");
		using HttpClient c = Client();
		using HttpResponseMessage resp = await c.GetAsync(string.IsNullOrEmpty(feed) ? LatestUrl : feed, ct);
		if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, "mac_upd_none");
		resp.EnsureSuccessStatusCode();
		return Evaluate(await resp.Content.ReadAsStringAsync(ct), Current);
	}

	/// <summary>Zip'i İndirilenler'e indirir ve doğrular; dosya yolunu döndürür.</summary>
	public static async Task<string> DownloadAsync(MacUpdate u, IProgress<double>? progress, CancellationToken ct)
	{
		string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		Directory.CreateDirectory(dir);
		string target = Path.Combine(dir, u.AssetName);
		string temp = target + ".part";
		using HttpClient c = Client();
		using (HttpResponseMessage resp = await c.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
		{
			resp.EnsureSuccessStatusCode();
			long total = resp.Content.Headers.ContentLength ?? u.Size;
			await using Stream src = await resp.Content.ReadAsStreamAsync(ct);
			await using FileStream dst = File.Create(temp);
			byte[] buf = new byte[81920];
			long done = 0;
			int n;
			while ((n = await src.ReadAsync(buf, ct)) > 0)
			{
				await dst.WriteAsync(buf.AsMemory(0, n), ct);
				done += n;
				if (total > 0) progress?.Report(done / (double)total);
			}
		}
		if (u.Sha256 != null)
		{
			string got = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temp))).ToLowerInvariant();
			if (got != u.Sha256)
			{
				File.Delete(temp);
				throw new InvalidOperationException("İndirilen dosyanın SHA-256 değeri tutmuyor.");
			}
		}
		File.Move(temp, target, overwrite: true);
		return target;
	}
}
