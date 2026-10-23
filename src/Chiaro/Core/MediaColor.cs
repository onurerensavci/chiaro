#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace HueStudio;

/// <summary>
/// Windows medya oturumlarından (GlobalSystemMediaTransportControls) çalan parçayı ve
/// albüm kapağının baskın canlı rengini okur. API anahtarı gerekmez; Spotify masaüstü
/// ve SMTC destekleyen diğer oynatıcılarla çalışır.
/// </summary>
public sealed class MediaColorReader
{
	public sealed record Track(string Key, string Title, string Artist, string App, bool Playing, System.Windows.Media.Color? Color);

	private GlobalSystemMediaTransportControlsSessionManager? manager;
	private string lastKey = "";
	private System.Windows.Media.Color? lastColor;

	public static bool IsSpotify(string appId) => appId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

	public async Task<Track?> ReadAsync(bool onlySpotify, CancellationToken ct)
	{
		manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
		GlobalSystemMediaTransportControlsSession? session = null;
		var sessions = manager.GetSessions();
		if (onlySpotify)
		{
			session = sessions.FirstOrDefault(s => IsSpotify(s.SourceAppUserModelId ?? ""));
		}
		else
		{
			session = manager.GetCurrentSession();
			session ??= sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
				?? sessions.FirstOrDefault();
		}
		if (session == null) return null;
		string app = session.SourceAppUserModelId ?? "";
		bool playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
		GlobalSystemMediaTransportControlsSessionMediaProperties props = await session.TryGetMediaPropertiesAsync().AsTask(ct);
		string title = props?.Title ?? "";
		string artist = props?.Artist ?? "";
		string key = app + "|" + title + "|" + artist + "|" + props?.AlbumTitle;
		if (key != lastKey)
		{
			lastKey = key;
			lastColor = null;
			if (props?.Thumbnail != null)
			{
				try
				{
					using IRandomAccessStreamWithContentType ras = await props.Thumbnail.OpenReadAsync().AsTask(ct);
					using Stream stream = ras.AsStreamForRead();
					using MemoryStream ms = new MemoryStream();
					await stream.CopyToAsync(ms, ct);
					ms.Position = 0;
					using Bitmap bmp = new Bitmap(ms);
					lastColor = Dominant(bmp);
				}
				catch (Exception) when (!ct.IsCancellationRequested)
				{
					lastColor = null;
				}
			}
		}
		return new Track(key, title, artist, app, playing, lastColor);
	}

	/// <summary>Kapak görselinin baskın canlı rengi (doygun tonlar öncelikli, ton histogramı).</summary>
	public static System.Windows.Media.Color Dominant(Bitmap source)
	{
		const int size = 48;
		using Bitmap small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
		using (Graphics g = Graphics.FromImage(small))
		{
			g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
			g.DrawImage(source, 0, 0, size, size);
		}
		double[] weight = new double[24];
		double[] sr = new double[24], sg = new double[24], sb = new double[24];
		double ar = 0, ag = 0, ab = 0;
		int n = 0;
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color p = small.GetPixel(x, y);
				ar += p.R; ag += p.G; ab += p.B; n++;
				var (h, s, v) = ColorMath.ToHsv(System.Windows.Media.Color.FromRgb(p.R, p.G, p.B));
				if (s < 0.22 || v < 0.18) continue;
				double w = s * s * v;
				int bin = (int)(h / 15.0) % 24;
				weight[bin] += w; sr[bin] += p.R * w; sg[bin] += p.G * w; sb[bin] += p.B * w;
			}
		}
		int best = -1;
		double bestScore = 0;
		for (int i = 0; i < 24; i++)
		{
			// Komşu kutuları da say: yakın tonlar tek renk gibi davransın.
			double score = weight[i] + 0.5 * (weight[(i + 23) % 24] + weight[(i + 1) % 24]);
			if (score > bestScore) { bestScore = score; best = i; }
		}
		if (best < 0 || weight[best] <= 0.5)
		{
			// Neredeyse renksiz kapak: ortalama ton, sıcak beyaza yakın.
			System.Windows.Media.Color avg = System.Windows.Media.Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
			return ColorMath.Lerp(avg, ColorMath.FromKelvin(3000), 0.5);
		}
		System.Windows.Media.Color c = System.Windows.Media.Color.FromRgb(
			(byte)Math.Clamp(sr[best] / weight[best], 0, 255),
			(byte)Math.Clamp(sg[best] / weight[best], 0, 255),
			(byte)Math.Clamp(sb[best] / weight[best], 0, 255));
		var (hh, ss, _) = ColorMath.ToHsv(c);
		return ColorMath.FromHsv(hh, Math.Clamp(ss * 1.1, 0, 1), 1.0);
	}

	public static System.Windows.Media.Color Boost(System.Windows.Media.Color c, double percent)
	{
		var (h, s, v) = ColorMath.ToHsv(c);
		return ColorMath.FromHsv(h, Math.Min(1.0, s * (1.0 + percent / 100.0)), v);
	}
}
