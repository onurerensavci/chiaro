#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>GitHub Releases'te bulunan yeni sürüm (ayarlarda önbelleğe alınır).</summary>
public sealed class UpdateInfo
{
	public string Tag { get; set; } = "";
	public string VersionText { get; set; } = "";
	public string Notes { get; set; } = "";
	public string AssetName { get; set; } = "";
	public string AssetUrl { get; set; } = "";
	public long AssetSize { get; set; }
	/// <summary>"&lt;exe&gt;.sha256" varlığının adresi (varsa; en öncelikli doğrulama).</summary>
	public string? ShaUrl { get; set; }
	/// <summary>GitHub varlık özeti ya da sürüm notlarındaki SHA-256 (varsa).</summary>
	public string? Sha256 { get; set; }
	public string PageUrl { get; set; } = "";
}

public enum UpdateStatus
{
	UpToDate,
	Available,
	Offline,
	RateLimited,
	Error,
}

public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Info, string Message);

/// <summary>Kullanıcıya gösterilebilir (Türkçe) güncelleme hatası.</summary>
public sealed class UpdateException : Exception
{
	public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Uygulama içi güncelleme: GitHub Releases'ten son sürümü denetler, indirir, doğrular
/// ve çalışan exe'yi yerinde değiştirir (.new → asıl yol, eski → .old; hata olursa geri alır).
/// </summary>
public static class Updater
{
	public const string Repo = "onurerensavci/chiaro";
	/// <summary>3.2 bu adla yayımlandı (GitHub yeniden adlandırılan depoları yönlendirir).</summary>
	public const string LegacyRepo = "onurerensavci/hue-studio";
	public const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
	public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
	/// <summary>Gizli test kancası: farklı bir sürüm akışı (URL ya da yerel JSON dosyası).</summary>
	public const string FeedVariable = "CHIARO_UPDATE_FEED";
	public const string UpdatedArgument = "--updated";

	public static Version Current
	{
		get
		{
			Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
			return Normalize(v);
		}
	}

	public static string CurrentText => Display(Current);

	public static string FeedUrl
	{
		get
		{
			string? o = Environment.GetEnvironmentVariable(FeedVariable);
			return string.IsNullOrWhiteSpace(o) ? LatestUrl : o.Trim();
		}
	}

	// ------------------------------------------------------------------ sürüm

	/// <summary>"v3.4", "3.4.1", "V3.10-beta" gibi etiketleri sürüme çevirir.</summary>
	public static bool TryParseVersion(string? tag, out Version version)
	{
		version = new Version(0, 0);
		if (string.IsNullOrWhiteSpace(tag)) return false;
		Match m = Regex.Match(tag.Trim(), @"^[vV]?\s*(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
		if (!m.Success) return false;
		int Part(int i) => m.Groups[i].Success && int.TryParse(m.Groups[i].Value, out int x) ? x : 0;
		try
		{
			version = new Version(Part(1), Part(2), Part(3), Part(4));
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>Eksik parçaları 0 sayar: 3.3 == 3.3.0 == 3.3.0.0.</summary>
	public static Version Normalize(Version v) => new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

	public static int Compare(Version a, Version b) => Normalize(a).CompareTo(Normalize(b));

	public static bool IsNewer(string tag, Version current) => TryParseVersion(tag, out Version v) && Compare(v, current) > 0;

	/// <summary>3.4.0.0 → "3.4", 3.4.1.0 → "3.4.1".</summary>
	public static string Display(Version v)
	{
		v = Normalize(v);
		if (v.Revision > 0) return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
		if (v.Build > 0) return $"{v.Major}.{v.Minor}.{v.Build}";
		return $"{v.Major}.{v.Minor}";
	}

	// ------------------------------------------------------------------ denetim

	private static HttpClient CreateClient(TimeSpan timeout)
	{
		HttpClient http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
		{
			Timeout = timeout,
		};
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Chiaro", CurrentText));
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/" + Repo + ")"));
		return http;
	}

	private static bool IsLocal(string url, out string path)
	{
		path = url;
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
		{
			if (u.IsFile)
			{
				path = u.LocalPath;
				return true;
			}
			return false;
		}
		return true;
	}

	public static async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
	{
		string feed = FeedUrl;
		string json;
		try
		{
			if (IsLocal(feed, out string localFeed))
			{
				json = await File.ReadAllTextAsync(localFeed, token);
			}
			else
			{
				using HttpClient http = CreateClient(TimeSpan.FromSeconds(15));
				using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, feed);
				req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
				req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
				using HttpResponseMessage res = await http.SendAsync(req, token);
				if (IsRateLimited(res))
				{
					return new UpdateCheckResult(UpdateStatus.RateLimited, null, L.T("upd_rate_limited"));
				}
				if (res.StatusCode == HttpStatusCode.NotFound)
				{
					// Henüz yayınlanmış sürüm yok.
					return new UpdateCheckResult(UpdateStatus.UpToDate, null, L.T("upd_uptodate"));
				}
				if (!res.IsSuccessStatusCode)
				{
					return new UpdateCheckResult(UpdateStatus.Error, null, L.F("upd_check_failed_code", (int)res.StatusCode));
				}
				json = await res.Content.ReadAsStringAsync(token);
			}
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			return new UpdateCheckResult(UpdateStatus.Offline, null, L.T("upd_no_internet"));
		}
		catch (HttpRequestException)
		{
			return new UpdateCheckResult(UpdateStatus.Offline, null, L.T("upd_no_internet"));
		}
		catch (IOException)
		{
			return new UpdateCheckResult(UpdateStatus.Offline, null, L.T("upd_no_internet"));
		}
		try
		{
			return Evaluate(json, Current);
		}
		catch (Exception)
		{
			return new UpdateCheckResult(UpdateStatus.Error, null, L.T("upd_bad_info"));
		}
	}

	private static bool IsRateLimited(HttpResponseMessage res)
	{
		if ((int)res.StatusCode == 429) return true;
		if (res.StatusCode != HttpStatusCode.Forbidden) return false;
		if (res.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? v) && v.FirstOrDefault() == "0") return true;
		return res.Headers.Contains("retry-after");
	}

	/// <summary>Release JSON'unu değerlendirir (test edilebilir, ağsız).</summary>
	public static UpdateCheckResult Evaluate(string releaseJson, Version current)
	{
		using JsonDocument doc = JsonDocument.Parse(releaseJson);
		JsonElement r = doc.RootElement;
		string tag = Str(r, "tag_name");
		if (!TryParseVersion(tag, out Version latest))
		{
			return new UpdateCheckResult(UpdateStatus.Error, null, L.T("upd_bad_info"));
		}
		if (Bool(r, "draft") || Bool(r, "prerelease") || Compare(latest, current) <= 0)
		{
			return new UpdateCheckResult(UpdateStatus.UpToDate, null, L.T("upd_uptodate"));
		}
		List<(string Name, string Url, long Size, string Digest)> assets = new();
		if (r.TryGetProperty("assets", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement a in arr.EnumerateArray())
			{
				string name = Str(a, "name");
				string url = Str(a, "browser_download_url");
				long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long z) ? z : 0;
				if (name.Length > 0 && url.Length > 0) assets.Add((name, url, size, Str(a, "digest")));
			}
		}
		var exe = PickExe(assets.Select(a => a.Name), Display(latest), tag);
		if (exe == null)
		{
			return new UpdateCheckResult(UpdateStatus.Error, null, L.F("upd_no_exe", Display(latest)));
		}
		var asset = assets.First(a => a.Name == exe);
		string body = Str(r, "body");
		var sha = assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
		UpdateInfo info = new UpdateInfo
		{
			Tag = tag,
			VersionText = Display(latest),
			Notes = PlainNotes(body),
			AssetName = asset.Name,
			AssetUrl = asset.Url,
			AssetSize = asset.Size,
			ShaUrl = sha.Url,
			// GitHub'ın varlık özeti ("sha256:…") ya da sürüm notlarındaki SHA-256 satırı.
			Sha256 = DigestSha(asset.Digest) ?? ShaFromBody(body, asset.Name),
			PageUrl = Str(r, "html_url") is { Length: > 0 } page ? page : ReleasesPage,
		};
		return new UpdateCheckResult(UpdateStatus.Available, info, L.F("upd_new_version", info.VersionText));
	}

	/// <summary>Chiaro_vX.Y.exe'yi tercih eder; yoksa eski ad HueStudio_vX.Y.exe, sonra Chiaro*/HueStudio*.exe, o da yoksa tek .exe.</summary>
	public static string? PickExe(IEnumerable<string> names, string version, string tag)
	{
		List<string> exes = names.Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
		string bare = tag.TrimStart('v', 'V');
		foreach (string want in new[] { $"Chiaro_v{version}.exe", $"Chiaro_v{bare}.exe", $"Chiaro_{tag}.exe", $"HueStudio_v{version}.exe", $"HueStudio_v{bare}.exe", $"HueStudio_{tag}.exe" })
		{
			string? hit = exes.FirstOrDefault(n => n.Equals(want, StringComparison.OrdinalIgnoreCase));
			if (hit != null) return hit;
		}
		string? studio = exes.FirstOrDefault(n => n.StartsWith("Chiaro", StringComparison.OrdinalIgnoreCase))
			?? exes.FirstOrDefault(n => n.StartsWith("HueStudio", StringComparison.OrdinalIgnoreCase));
		if (studio != null) return studio;
		return exes.Count == 1 ? exes[0] : null;
	}

	private static string? DigestSha(string? digest)
	{
		if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
		string h = digest.Substring(7).Trim().ToLowerInvariant();
		return HashRx.IsMatch(h) && h.Length == 64 ? h : null;
	}

	private static readonly Regex HashRx = new Regex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])", RegexOptions.Compiled);

	/// <summary>Sürüm notlarından SHA-256: önce exe adının geçtiği satır, sonra "sha" geçen satır, sonra tek değer.</summary>
	public static string? ShaFromBody(string? body, string assetName)
	{
		if (string.IsNullOrWhiteSpace(body)) return null;
		string[] lines = body.Replace("\r", "").Split('\n');
		foreach (Func<string, bool> pick in new Func<string, bool>[]
		{
			l => l.Contains(assetName, StringComparison.OrdinalIgnoreCase),
			l => l.Contains("sha", StringComparison.OrdinalIgnoreCase),
		})
		{
			foreach (string l in lines.Where(pick))
			{
				Match m = HashRx.Match(l);
				if (m.Success) return m.Value.ToLowerInvariant();
			}
		}
		List<string> all = HashRx.Matches(body).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
		return all.Count == 1 ? all[0] : null;
	}

	/// <summary>Markdown'ı sade metne çevirir (başlık, kalın, bağlantı, kod, SHA satırları gider).</summary>
	public static string PlainNotes(string? body, int max = 900)
	{
		if (string.IsNullOrWhiteSpace(body)) return "";
		List<string> outLines = new();
		bool inCode = false;
		foreach (string raw in body.Replace("\r", "").Split('\n'))
		{
			string l = raw.TrimEnd();
			if (l.TrimStart().StartsWith("```"))
			{
				inCode = !inCode;
				continue;
			}
			if (inCode || HashRx.IsMatch(l)) continue;
			l = Regex.Replace(l, @"<[^>]+>", "");
			l = Regex.Replace(l, @"!\[[^\]]*\]\([^)]*\)", "");
			l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]*\)", "$1");
			l = Regex.Replace(l, @"^\s{0,3}#{1,6}\s*", "");
			l = Regex.Replace(l, @"^\s*>\s?", "");
			l = Regex.Replace(l, @"^(\s*)[-*+]\s+", "$1• ");
			l = Regex.Replace(l, @"(\*\*|__)(.+?)\1", "$2");
			l = Regex.Replace(l, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
			l = l.Replace("`", "");
			if (Regex.IsMatch(l, @"^\s*([-*_]\s*){3,}$")) continue;
			if (l.Trim().Length == 0 && (outLines.Count == 0 || outLines[^1].Length == 0)) continue;
			outLines.Add(l.Trim().Length == 0 ? "" : l);
		}
		string text = string.Join("\n", outLines).Trim();
		if (text.Length > max)
		{
			int cut = text.LastIndexOf('\n', max);
			text = text.Substring(0, cut > max / 2 ? cut : max).TrimEnd() + "\n…";
		}
		return text;
	}

	private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

	private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

	// ------------------------------------------------------------------ indirme

	public static string TempFolder => Path.Combine(Path.GetTempPath(), "Chiaro-guncelleme");

	/// <summary>Yeni exe'yi %TEMP%'e indirir ve doğrular. İlerleme 0..1.</summary>
	public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken token)
	{
		Directory.CreateDirectory(TempFolder);
		string target = Path.Combine(TempFolder, SafeName(info.AssetName));
		string part = target + ".part";
		TryDelete(part);
		try
		{
			if (IsLocal(info.AssetUrl, out string local))
			{
				await CopyWithProgress(File.OpenRead(local), new FileInfo(local).Length, part, progress, token);
			}
			else
			{
				using HttpClient http = CreateClient(TimeSpan.FromMinutes(15));
				using HttpResponseMessage res = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token);
				if (IsRateLimited(res)) throw new UpdateException(L.T("upd_rate_limited"));
				if (!res.IsSuccessStatusCode) throw new UpdateException(L.F("upd_download_failed_code", (int)res.StatusCode));
				long total = res.Content.Headers.ContentLength ?? info.AssetSize;
				await CopyWithProgress(await res.Content.ReadAsStreamAsync(token), total, part, progress, token);
			}
		}
		catch (HttpRequestException ex)
		{
			TryDelete(part);
			throw new UpdateException(L.T("upd_download_interrupted"), ex);
		}
		catch (IOException ex) when (ex is not FileNotFoundException)
		{
			TryDelete(part);
			throw new UpdateException(L.T("upd_download_interrupted"), ex);
		}
		catch (Exception)
		{
			TryDelete(part);
			throw;
		}
		try
		{
			await VerifyAsync(info, part, token);
			File.Move(part, target, overwrite: true);
		}
		catch (Exception)
		{
			TryDelete(part);
			throw;
		}
		return target;
	}

	private static string SafeName(string name)
	{
		string n = Path.GetFileName(name);
		foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
		return n.Length == 0 ? "Chiaro_yeni.exe" : n;
	}

	private static async Task CopyWithProgress(Stream source, long total, string path, IProgress<double>? progress, CancellationToken token)
	{
		await using Stream src = source;
		await using FileStream dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
		byte[] buffer = new byte[1 << 16];
		long done = 0;
		int n;
		double last = -1;
		while ((n = await src.ReadAsync(buffer, token)) > 0)
		{
			await dst.WriteAsync(buffer.AsMemory(0, n), token);
			done += n;
			if (total > 0)
			{
				double p = Math.Min(1, (double)done / total);
				if (p - last >= 0.005 || p >= 1)
				{
					last = p;
					progress?.Report(p);
				}
			}
		}
		progress?.Report(1);
	}

	/// <summary>Boyut, exe imzası (MZ) ve varsa SHA-256 denetimi.</summary>
	public static async Task VerifyAsync(UpdateInfo info, string path, CancellationToken token)
	{
		FileInfo fi = new FileInfo(path);
		if (info.AssetSize > 0 && fi.Length != info.AssetSize)
		{
			throw new UpdateException(L.T("upd_file_incomplete"));
		}
		using (FileStream fs = File.OpenRead(path))
		{
			if (fs.Length < 1024 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
			{
				throw new UpdateException(L.T("upd_file_invalid"));
			}
		}
		string? expected = info.Sha256;
		if (!string.IsNullOrEmpty(info.ShaUrl))
		{
			string text;
			try
			{
				if (IsLocal(info.ShaUrl, out string local))
				{
					text = await File.ReadAllTextAsync(local, token);
				}
				else
				{
					using HttpClient http = CreateClient(TimeSpan.FromSeconds(30));
					text = await http.GetStringAsync(info.ShaUrl, token);
				}
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException)
			{
				throw new UpdateException(L.T("upd_sha_download_failed"), ex);
			}
			Match m = HashRx.Match(text);
			if (!m.Success) throw new UpdateException(L.T("upd_sha_unreadable"));
			expected = m.Value.ToLowerInvariant();
		}
		if (!string.IsNullOrEmpty(expected))
		{
			string actual = await Sha256Async(path, token);
			if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
			{
				throw new UpdateException(L.T("upd_sha_mismatch"));
			}
		}
	}

	public static async Task<string> Sha256Async(string path, CancellationToken token = default)
	{
		await using FileStream fs = File.OpenRead(path);
		byte[] hash = await SHA256.HashDataAsync(fs, token);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	// ------------------------------------------------------------------ yerinde değiştirme

	public static string NewPath(string exe) => exe + ".new";

	public static string OldPath(string exe) => exe + ".old";

	/// <summary>
	/// Yeni exe'yi yerine koyar: indirilen → &lt;exe&gt;.new, çalışan &lt;exe&gt; → &lt;exe&gt;.old (Windows
	/// çalışan exe'nin adını değiştirmeye izin verir), .new → &lt;exe&gt;. Hata olursa eski hâline döner.
	/// </summary>
	public static void Swap(string exe, string downloaded)
	{
		string next = NewPath(exe), old = OldPath(exe);
		if (!File.Exists(exe)) throw new UpdateException(L.T("upd_exe_missing"));
		TryDelete(next);
		if (File.Exists(old) && !DeleteWithRetries(old, 5, 200))
		{
			throw new UpdateException(L.T("upd_old_locked"));
		}
		try
		{
			File.Copy(downloaded, next, overwrite: true);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			TryDelete(next);
			throw new UpdateException(L.T("upd_folder_readonly"), ex);
		}
		try
		{
			File.Move(exe, old);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			TryDelete(next);
			throw new UpdateException(L.T("upd_replace_failed"), ex);
		}
		try
		{
			File.Move(next, exe);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			Rollback(exe);
			throw new UpdateException(L.T("upd_swap_rolled_back"), ex);
		}
	}

	/// <summary>Eski exe'yi (.old) geri koyar; yerine konmuş yeni dosya .new olarak kenara alınır.</summary>
	public static bool Rollback(string exe)
	{
		string old = OldPath(exe);
		if (!File.Exists(old)) return File.Exists(exe);
		try
		{
			if (File.Exists(exe))
			{
				string next = NewPath(exe);
				TryDelete(next);
				File.Move(exe, next);
			}
			File.Move(old, exe);
			TryDelete(NewPath(exe));
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Açılışta önceki güncellemeden kalan .old/.new dosyalarını ve indirmeleri siler.</summary>
	public static async Task CleanupAsync(string exe, int attempts = 40, int delayMs = 500)
	{
		string old = OldPath(exe), next = NewPath(exe);
		for (int i = 0; i < attempts && (File.Exists(old) || File.Exists(next)); i++)
		{
			if (i > 0) await Task.Delay(delayMs);
			TryDelete(old);
			TryDelete(next);
		}
		try
		{
			if (Directory.Exists(TempFolder)) Directory.Delete(TempFolder, recursive: true);
		}
		catch (Exception)
		{
		}
	}

	public static bool DeleteWithRetries(string path, int attempts, int delayMs)
	{
		for (int i = 0; i < attempts; i++)
		{
			if (TryDelete(path)) return true;
			Thread.Sleep(delayMs);
		}
		return !File.Exists(path);
	}

	private static bool TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Tek dosya, kendi başına çalışan exe mi (dotnet.exe üzerinden değil)?</summary>
	public static string? RunningExe
	{
		get
		{
			string? p = Environment.ProcessPath;
			if (string.IsNullOrEmpty(p)) return null;
			string name = Path.GetFileNameWithoutExtension(p);
			if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
			return p;
		}
	}
}
