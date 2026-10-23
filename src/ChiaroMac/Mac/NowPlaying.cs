#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HueStudio.MacInterop;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

/// <summary>Çalan parça (Windows MediaColorReader.Track karşılığı).</summary>
public sealed record NowPlayingTrack(string Key, string Title, string Artist, string App, bool Playing, MColor? Color);

internal interface INowPlaying
{
	Task<NowPlayingTrack?> ReadAsync(bool onlySpotify, CancellationToken ct);
}

/// <summary>
/// macOS: Spotify masaüstü uygulamasını (ve isteğe bağlı Apple Müzik'i) AppleScript ile sorgular; Windows'taki medya oturumu
/// okuyucusunun karşılığı. API anahtarı gerekmez. İlk sorguda macOS "Chiaro, Spotify'ı denetlemek istiyor" izni ister.
/// Kapak rengi: Spotify kapak adresinden (i.scdn.co) indirilir; Müzik'te kapak verisi geçici dosyaya yazılır.
/// </summary>
internal sealed class MacNowPlaying : INowPlaying
{
	public const string SpotifyId = "com.spotify.client";
	public const string MusicId = "com.apple.Music";
	private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
	private string lastKey = "";
	private MColor? lastColor;
	private readonly string artFile = Path.Combine(Path.GetTempPath(), "chiaro-cover.dat");

	// "is running" denetimi: uygulama denetimle sorgu arasında kapanırsa AppleScript onu yeniden başlatmasın.
	internal const string SpotifyScript = """
		if application id "com.spotify.client" is not running then return "stopped"
		tell application id "com.spotify.client"
			set s to player state as string
			if s is "stopped" then return "stopped"
			set t to current track
			return s & linefeed & (name of t) & linefeed & (artist of t) & linefeed & (album of t) & linefeed & (artwork url of t)
		end tell
		""";

	internal static string MusicScript(string? artPath) => """
		if application id "com.apple.Music" is not running then return "stopped"
		tell application id "com.apple.Music"
			set s to player state as string
			if s is "stopped" then return "stopped"
			set t to current track
			set r to s & linefeed & (name of t) & linefeed & (artist of t) & linefeed & (album of t) & linefeed
		""" + (artPath == null ? "" : """

			try
				if (count of artworks of t) > 0 then set d to raw data of artwork 1 of t
			end try
		""") + """

		end tell
		""" + (artPath == null ? "" : $$"""

		try
			set f to open for access (POSIX file "{{artPath}}") with write permission
			set eof f to 0
			write d to f
			close access f
			set r to r & "file"
		end try
		""") + "\nreturn r\n";

	public static bool IsRunning(string bundleId)
	{
		if (!OperatingSystem.IsMacOS()) return false;
		Native.Load(Native.AppKit);
		IntPtr pool = Native.Pool();
		try
		{
			IntPtr arr = Objc.Send(Objc.Cls("NSRunningApplication"), Objc.Sel("runningApplicationsWithBundleIdentifier:"), Objc.NSString(bundleId));
			return Objc.Items(arr).Count > 0;
		}
		catch (Exception ex)
		{
			AppLog.Write("Çalışan uygulama denetimi: " + ex.Message);
			return false;
		}
		finally
		{
			Native.Drain(pool);
		}
	}

	public async Task<NowPlayingTrack?> ReadAsync(bool onlySpotify, CancellationToken ct)
	{
		if (IsRunning(SpotifyId))
		{
			NowPlayingTrack? t = await ReadSpotify(ct);
			if (t != null && (t.Playing || onlySpotify || !IsRunning(MusicId))) return t;
			if (!onlySpotify)
			{
				NowPlayingTrack? m = await ReadMusic(ct);
				if (m != null && m.Playing) return m;
			}
			return t;
		}
		if (!onlySpotify && IsRunning(MusicId)) return await ReadMusic(ct);
		return null;
	}

	private async Task<NowPlayingTrack?> ReadSpotify(CancellationToken ct)
	{
		string? r = await RunScript(SpotifyScript, "Spotify", ct);
		if (r == null || r.Trim() == "stopped") return null;
		string[] p = r.Replace("\r", "").Split('\n');
		if (p.Length < 4) return null;
		bool playing = p[0].Trim() == "playing";
		string key = "spotify|" + p[1] + "|" + p[2] + "|" + p[3];
		if (key != lastKey)
		{
			lastKey = key;
			lastColor = null;
			string url = p.Length > 4 ? p[4].Trim() : "";
			if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					byte[] img = await http.GetByteArrayAsync(url, ct);
					lastColor = CoverDecoder.Dominant(img);
				}
				catch (Exception ex) when (!ct.IsCancellationRequested)
				{
					AppLog.Write("Spotify kapağı alınamadı: " + ex.Message);
				}
			}
		}
		return new NowPlayingTrack(key, p[1], p[2], "Spotify", playing, lastColor);
	}

	private async Task<NowPlayingTrack?> ReadMusic(CancellationToken ct)
	{
		// Önce yalnız bilgi; parça değiştiyse kapakla birlikte yeniden sorgu.
		string? r = await RunScript(MusicScript(null), "Müzik", ct);
		if (r == null || r.Trim() == "stopped") return null;
		string[] p = r.Replace("\r", "").Split('\n');
		if (p.Length < 4) return null;
		bool playing = p[0].Trim() == "playing";
		string key = "music|" + p[1] + "|" + p[2] + "|" + p[3];
		if (key != lastKey)
		{
			lastKey = key;
			lastColor = null;
			try
			{
				File.Delete(artFile);
				string? withArt = await RunScript(MusicScript(artFile), "Müzik", ct);
				if (withArt != null && withArt.TrimEnd().EndsWith("file") && File.Exists(artFile))
				{
					lastColor = CoverDecoder.Dominant(await File.ReadAllBytesAsync(artFile, ct));
				}
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				AppLog.Write("Müzik kapağı alınamadı: " + ex.Message);
			}
		}
		return new NowPlayingTrack(key, p[1], p[2], L.En ? "Music" : "Müzik", playing, lastColor);
	}

	/// <summary>osascript'i çalıştırır; izin reddi (-1743) PermissionException olur.</summary>
	internal static async Task<string?> RunScript(string script, string appName, CancellationToken ct)
	{
		ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/osascript")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("osascript başlatılamadı.");
		await proc.StandardInput.WriteAsync(script);
		proc.StandardInput.Close();
		Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
		Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
		using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
		{
			// İzin penceresi açıkken kullanıcı yanıtlayana kadar bekleyebilir.
			timeout.CancelAfter(TimeSpan.FromSeconds(60));
			try
			{
				await proc.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				try { proc.Kill(); } catch { }
				if (ct.IsCancellationRequested) throw;
				throw new TimeoutException($"{appName} yanıt vermedi.");
			}
		}
		string output = await outTask;
		string err = await errTask;
		if (proc.ExitCode != 0)
		{
			if (err.Contains("-1743")) throw Privacy.AutomationDenied(appName);
			if (err.Contains("-600") || err.Contains("-1728")) return null; // uygulama kapalı / parça yok
			throw new InvalidOperationException($"{appName}: {err.Trim()}");
		}
		return output.TrimEnd('\n');
	}
}

/// <summary>Kapak görselini (JPEG/PNG) SkiaSharp ile 48×48'e küçültüp baskın rengi bulur.</summary>
internal static class CoverDecoder
{
	public static MColor? Dominant(byte[] image)
	{
		using SkiaSharp.SKBitmap? src = SkiaSharp.SKBitmap.Decode(image);
		if (src == null) return null;
		using SkiaSharp.SKBitmap small = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(48, 48, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
		if (!src.ScalePixels(small, SkiaSharp.SKFilterQuality.Medium)) return null;
		return CoverColor.Dominant(small.GetPixelSpan(), 48, 48, small.RowBytes, bgra: false);
	}
}

internal sealed class DemoNowPlaying : INowPlaying
{
	public Task<NowPlayingTrack?> ReadAsync(bool onlySpotify, CancellationToken ct) =>
		Task.FromResult<NowPlayingTrack?>(new NowPlayingTrack("demo", "Blinding Lights", "The Weeknd", "Spotify", true, MColor.FromRgb(0xE0, 0x31, 0x2B)));
}
