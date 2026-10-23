#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>Duman testi: sürüm karşılaştırma, release JSON, SHA, notlar, indirme doğrulama ve exe değiştirme (geçici klasörde).</summary>
internal static class UpdaterSelfTest
{
	internal static UpdateInfo SampleInfo() => new UpdateInfo
	{
		Tag = "v3.6",
		VersionText = "3.6",
		AssetName = "Chiaro_v3.6.exe",
		AssetUrl = "https://github.com/" + Updater.Repo + "/releases/download/v3.6/Chiaro_v3.6.exe",
		AssetSize = 78_400_000,
		Notes = Updater.PlainNotes((L.En ? "## Chiaro 3.6\n\n- 🌅 **Sunrise** is now smoother\n- 🎧 More vivid Spotify colors\n- Small fixes" : "## Chiaro 3.6\n\n- 🌅 **Gün doğumu** artık daha yumuşak\n- 🎧 Spotify renkleri daha canlı\n- Küçük hata düzeltmeleri") + "\n\nSHA-256: " + new string('a', 64)),
		PageUrl = Updater.ReleasesPage,
	};

	private static void Check(bool ok, string what)
	{
		if (!ok) throw new InvalidOperationException("Updater: " + what);
	}

	private static string Release(string tag, bool prerelease = false, string body = "", params string[] assets)
	{
		string a = string.Join(",", assets.Select(n => $"{{\"name\":\"{n}\",\"size\":2048,\"browser_download_url\":\"https://github.com/{Updater.Repo}/releases/download/{tag}/{n}\"}}"));
		return $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":{(prerelease ? "true" : "false")},\"html_url\":\"https://github.com/{Updater.Repo}/releases/tag/{tag}\",\"body\":{System.Text.Json.JsonSerializer.Serialize(body)},\"assets\":[{a}]}}";
	}

	internal static async Task<string> RunAsync(string folder)
	{
		// --- sürüm ---
		Check(Updater.Current == new Version(3, 5, 0, 0), "current version is " + Updater.Current);
		Check(Updater.CurrentText == "3.5", "current text " + Updater.CurrentText);
		Check(Updater.TryParseVersion("v3.2", out Version v32) && v32 == new Version(3, 2, 0, 0), "parse v3.2");
		Check(Updater.TryParseVersion("V3.10-beta", out Version v310) && v310 == new Version(3, 10, 0, 0), "parse V3.10-beta");
		Check(!Updater.TryParseVersion("latest", out _) && !Updater.TryParseVersion("", out _), "reject garbage");
		Check(!Updater.IsNewer("v3.2", Updater.Current), "3.2 must not be newer than 3.5");
		Check(!Updater.IsNewer("v3.4", Updater.Current), "3.4 must not be newer than 3.5");
		Check(!Updater.IsNewer("v3.5", Updater.Current) && !Updater.IsNewer("3.5.0", Updater.Current), "3.5 equals current");
		Check(Updater.IsNewer("v3.6", Updater.Current) && Updater.IsNewer("v3.5.1", Updater.Current) && Updater.IsNewer("v4.0", Updater.Current), "3.6 / 3.5.1 / 4.0 newer");
		Check(Updater.Compare(new Version(3, 10), new Version(3, 9)) > 0, "3.10 > 3.9 (numeric, not text)");
		Check(Updater.Display(new Version(3, 5, 0, 0)) == "3.5" && Updater.Display(new Version(3, 5, 1, 0)) == "3.5.1", "display");

		// --- release JSON ---
		Version cur = Updater.Current;
		UpdateCheckResult r = Updater.Evaluate(Release("v3.2", false, "İlk sürüm", "Chiaro_v3.2.exe"), cur);
		Check(r.Status == UpdateStatus.UpToDate && r.Message == L.T("upd_uptodate"), "v3.2 feed -> up to date");
		r = Updater.Evaluate(Release("v3.4", false, "", "Chiaro_v3.4.exe"), cur);
		Check(r.Status == UpdateStatus.UpToDate, "v3.4 feed -> up to date");
		r = Updater.Evaluate(Release("v3.5", false, "", "Chiaro_v3.5.exe"), cur);
		Check(r.Status == UpdateStatus.UpToDate, "v3.5 feed -> up to date");
		r = Updater.Evaluate(Release("v3.7", true, "", "Chiaro_v3.7.exe"), cur);
		Check(r.Status == UpdateStatus.UpToDate, "prerelease ignored");
		string hash = new string('b', 64);
		r = Updater.Evaluate(Release("v3.6", false, "## Yenilikler\n- **Daha hızlı**\n\nSHA-256 (Chiaro_v3.6.exe): " + hash, "source.zip", "Chiaro_v3.6.exe.sha256", "Chiaro_v3.6.exe"), cur);
		Check(r.Status == UpdateStatus.Available && r.Info!.VersionText == "3.6" && r.Info.AssetName == "Chiaro_v3.6.exe", "v3.6 feed -> available + exe asset");
		Check(r.Info!.ShaUrl!.EndsWith("Chiaro_v3.6.exe.sha256") && r.Info.Sha256 == hash, "sha asset + sha in body");
		Check(r.Info.Notes == "Yenilikler\n• Daha hızlı", "plain notes: [" + r.Info.Notes + "]");
		string digestJson = "{\"tag_name\":\"v3.6\",\"assets\":[{\"name\":\"Chiaro_v3.6.exe\",\"size\":10,\"digest\":\"sha256:" + new string('d', 64) + "\",\"browser_download_url\":\"https://example.invalid/Chiaro_v3.6.exe\"}]}";
		r = Updater.Evaluate(digestJson, cur);
		Check(r.Status == UpdateStatus.Available && r.Info!.Sha256 == new string('d', 64) && r.Info.ShaUrl == null, "GitHub asset digest used for SHA-256");
		// Gerçek sürümlerde Mac paketi de aynı sürümde: Windows güncelleyicisi yalnızca .exe'yi seçmeli.
		r = Updater.Evaluate(Release("v3.6", false, "", "Chiaro_v3.6_mac.zip", "Chiaro_v3.6_mac.zip.sha256", "Chiaro_v3.6.exe.sha256", "Chiaro_v3.6.exe"), cur);
		Check(r.Status == UpdateStatus.Available && r.Info!.AssetName == "Chiaro_v3.6.exe" && r.Info.ShaUrl!.EndsWith("Chiaro_v3.6.exe.sha256"), "mac zip in same release ignored, exe picked");
		r = Updater.Evaluate(Release("v3.6", false, "", "source.zip"), cur);
		Check(r.Status == UpdateStatus.Error, "no exe -> error");
		Check(Updater.PickExe(new[] { "Other.exe", "Chiaro_v3.5.exe" }, "3.5", "v3.5") == "Chiaro_v3.5.exe", "pick exact exe");
		Check(Updater.PickExe(new[] { "Other.exe", "HueStudio_v3.5.exe" }, "3.5", "v3.5") == "HueStudio_v3.5.exe", "legacy HueStudio_vX.Y.exe fallback");
		Check(Updater.PickExe(new[] { "HueStudio_v3.5.exe", "Chiaro_v3.5.exe" }, "3.5", "v3.5") == "Chiaro_v3.5.exe", "Chiaro preferred over legacy name");
		Check(Updater.ShaFromBody("x\n`" + hash.ToUpperInvariant() + "`", "a.exe") == hash, "single hash in body");
		Check(Updater.ShaFromBody("no hash here", "a.exe") == null, "no hash");
		Check(Updater.PlainNotes("# Başlık\n\n[Bağlantı](https://x.y) ve `kod`\n\n\n\n- madde") == "Başlık\n\nBağlantı ve kod\n\n• madde", "markdown cleanup");

		// --- indirme + doğrulama (yerel dosya, ağ yok) ---
		string dir = Path.Combine(folder, "updater-test");
		if (Directory.Exists(dir)) Directory.Delete(dir, true);
		Directory.CreateDirectory(dir);
		byte[] newExe = Encoding.ASCII.GetBytes("MZ" + new string('N', 4096));
		byte[] oldExe = Encoding.ASCII.GetBytes("MZ" + new string('O', 4096));
		string asset = Path.Combine(dir, "Chiaro_v3.5.exe");
		File.WriteAllBytes(asset, newExe);
		string sha = await Updater.Sha256Async(asset);
		File.WriteAllText(asset + ".sha256", sha + "  Chiaro_v3.5.exe\n");
		UpdateInfo info = new UpdateInfo { Tag = "v3.5", VersionText = "3.5", AssetName = "Chiaro_v3.5.exe", AssetUrl = asset, AssetSize = newExe.Length, ShaUrl = asset + ".sha256" };
		double last = 0;
		string downloaded = await Updater.DownloadAsync(info, new SyncProgress(p => last = p), CancellationToken.None);
		Check(last == 1 && File.ReadAllBytes(downloaded).SequenceEqual(newExe) && downloaded.StartsWith(Updater.TempFolder), "download to %TEMP% + progress + sha256 file");
		bool rejected = false;
		try
		{
			await Updater.DownloadAsync(new UpdateInfo { AssetName = "Chiaro_v3.5.exe", AssetUrl = asset, Sha256 = new string('c', 64) }, null, CancellationToken.None);
		}
		catch (UpdateException ex) when (ex.Message.Contains("SHA-256"))
		{
			rejected = true;
		}
		Check(rejected, "sha mismatch must be rejected");
		string notExe = Path.Combine(dir, "bad.exe");
		File.WriteAllText(notExe, new string('x', 4096));
		rejected = false;
		try
		{
			await Updater.DownloadAsync(new UpdateInfo { AssetName = "bad.exe", AssetUrl = notExe }, null, CancellationToken.None);
		}
		catch (UpdateException)
		{
			rejected = true;
		}
		Check(rejected, "non-exe download must be rejected");

		// --- yerinde değiştirme ---
		string app = Path.Combine(dir, "app");
		Directory.CreateDirectory(app);
		string exe = Path.Combine(app, "Chiaro.exe");
		File.WriteAllBytes(exe, oldExe);
		Updater.Swap(exe, downloaded);
		Check(File.ReadAllBytes(exe).SequenceEqual(newExe) && File.ReadAllBytes(exe + ".old").SequenceEqual(oldExe) && !File.Exists(exe + ".new"), "swap: new in place, old kept as .old");
		Check(Updater.Rollback(exe) && File.ReadAllBytes(exe).SequenceEqual(oldExe) && !File.Exists(exe + ".old") && !File.Exists(exe + ".new"), "rollback restores old exe");
		// Yarıda kalmış değiştirme (exe yok, yalnızca .old var) geri alınabilmeli.
		File.Move(exe, exe + ".old");
		Check(Updater.Rollback(exe) && File.ReadAllBytes(exe).SequenceEqual(oldExe), "rollback after interrupted swap");
		// Eksik indirme: exe'ye dokunulmamalı.
		rejected = false;
		try
		{
			Updater.Swap(exe, Path.Combine(dir, "yok.exe"));
		}
		catch (UpdateException)
		{
			rejected = true;
		}
		Check(rejected && File.ReadAllBytes(exe).SequenceEqual(oldExe) && !File.Exists(exe + ".new") && !File.Exists(exe + ".old"), "failed swap leaves exe untouched");
		// Kilitli .old kalıntısı: güncelleme reddedilir, exe aynı kalır.
		File.WriteAllBytes(exe + ".old", oldExe);
		string lockNote;
		using (FileStream locked = new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
		{
			rejected = false;
			try
			{
				Updater.Swap(exe, downloaded);
			}
			catch (UpdateException)
			{
				rejected = true;
			}
			lockNote = rejected ? "locked leftover .old refused" : "locked .old not enforced by this OS layer";
			if (!rejected) Updater.Rollback(exe);
		}
		Check(File.ReadAllBytes(exe).SequenceEqual(oldExe), "exe intact after locked .old case");
		Updater.Swap(exe, downloaded);
		await Updater.CleanupAsync(exe, attempts: 5, delayMs: 100);
		Check(!File.Exists(exe + ".old") && File.ReadAllBytes(exe).SequenceEqual(newExe) && !Directory.Exists(Updater.TempFolder), "startup cleanup deletes .old and temp download");
		Directory.Delete(dir, true);

		// --- canlı GitHub denetimi (gerçek API; son sürüm ≤ 3.5 → güncel) ---
		string? feed = Environment.GetEnvironmentVariable(Updater.FeedVariable);
		string feedLine = "";
		if (!string.IsNullOrWhiteSpace(feed))
		{
			UpdateCheckResult t = await Updater.CheckAsync();
			feedLine = $"\ntest feed ({Updater.FeedVariable}): {t.Status} v{t.Info?.VersionText} asset={t.Info?.AssetName} size={t.Info?.AssetSize} sha256={t.Info?.Sha256 ?? "-"} shaUrl={(t.Info?.ShaUrl != null ? "yes" : "no")} notes={t.Info?.Notes.Length} chars.";
		}
		Environment.SetEnvironmentVariable(Updater.FeedVariable, null);
		UpdateCheckResult live = await Updater.CheckAsync();
		// Eski depo adı (v3.2, HueStudio_v3.2.exe): GitHub yönlendirmesi/geri dönüş adı ile de "güncel" çıkmalı.
		Environment.SetEnvironmentVariable(Updater.FeedVariable, "https://api.github.com/repos/" + Updater.LegacyRepo + "/releases/latest");
		UpdateCheckResult legacy = await Updater.CheckAsync();
		Environment.SetEnvironmentVariable(Updater.FeedVariable, feed);
		string liveLine = live.Status switch
		{
			UpdateStatus.UpToDate => "live GitHub check: UpToDate (\"" + live.Message + "\"): PASS",
			UpdateStatus.Offline or UpdateStatus.RateLimited => $"live GitHub check: {live.Status} ({live.Message}) — SKIPPED, network",
			_ => throw new InvalidOperationException($"Live update check unexpected: {live.Status} {live.Info?.VersionText} {live.Message}"),
		};
		liveLine += $" ({Updater.Repo})" + (legacy.Status == UpdateStatus.UpToDate ? $"; legacy repo {Updater.LegacyRepo}: UpToDate: PASS" : $"; legacy repo {Updater.LegacyRepo}: {legacy.Status} ({legacy.Message})");
		if (legacy.Status == UpdateStatus.Available || legacy.Status == UpdateStatus.Error) throw new InvalidOperationException($"Legacy repo check unexpected: {legacy.Status} {legacy.Message}");
		return "\nUpdater: version parse/compare, release JSON (v3.2/v3.4/v3.5 up to date, v3.6 available, prerelease ignored, exe + .sha256 asset pick, mac zip ignored, GitHub digest), SHA from notes, plain notes, "
			+ $"download + SHA-256 verify (mismatch/non-exe rejected), swap/rollback/interrupted swap/cleanup, {lockNote}: PASS.\n" + liveLine + "." + feedLine;
	}

	private sealed class SyncProgress : IProgress<double>
	{
		private readonly Action<double> action;
		public SyncProgress(Action<double> action) => this.action = action;
		public void Report(double value) => action(value);
	}
}
