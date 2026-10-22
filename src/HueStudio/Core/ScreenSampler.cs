#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HueStudio;

/// <summary>Ekran izleme modu için seçilebilir bölge ve hesaplama yöntemiyle renk örnekleyici.</summary>
internal sealed class ScreenSampler : IDisposable
{
	private Bitmap? bitmap;
	private Graphics? graphics;

	public static readonly string[] AreaNames = { "Tüm ekran", "Orta", "Üst kenar", "Alt kenar", "Sol kenar", "Sağ kenar" };
	public static readonly string[] MethodNames = { "Ortalama", "Canlı" };

	public static string[] MonitorNames()
	{
		Screen[] screens = Screen.AllScreens;
		string[] names = new string[screens.Length];
		for (int i = 0; i < screens.Length; i++)
		{
			Screen s = screens[i];
			names[i] = $"Ekran {i + 1}{(s.Primary ? " (birincil)" : "")} • {s.Bounds.Width}×{s.Bounds.Height}";
		}
		return names;
	}

	public static Rectangle Region(Rectangle b, int area)
	{
		return area switch
		{
			1 => new Rectangle(b.X + b.Width / 4, b.Y + b.Height / 4, Math.Max(1, b.Width / 2), Math.Max(1, b.Height / 2)),
			2 => new Rectangle(b.X, b.Y, b.Width, Math.Max(1, b.Height / 4)),
			3 => new Rectangle(b.X, b.Y + b.Height - Math.Max(1, b.Height / 4), b.Width, Math.Max(1, b.Height / 4)),
			4 => new Rectangle(b.X, b.Y, Math.Max(1, b.Width / 4), b.Height),
			5 => new Rectangle(b.X + b.Width - Math.Max(1, b.Width / 4), b.Y, Math.Max(1, b.Width / 4), b.Height),
			_ => b,
		};
	}

	/// <returns>Renk ve 0..1 arası ortalama parlaklık.</returns>
	public (byte R, byte G, byte B, double Luma) Read(int monitor, int area, int method, bool ignoreDark)
	{
		Screen[] screens = Screen.AllScreens;
		Screen screen = (monitor >= 0 && monitor < screens.Length) ? screens[monitor] : (Screen.PrimaryScreen ?? throw new InvalidOperationException("Ekran bulunamadı."));
		Rectangle region = Region(screen.Bounds, area);
		if (bitmap == null || bitmap.Size != region.Size)
		{
			Dispose();
			bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
			graphics = Graphics.FromImage(bitmap);
		}
		graphics!.CopyFromScreen(region.Location, Point.Empty, region.Size);
		return Analyze(bitmap, method, ignoreDark);
	}

	internal static (byte R, byte G, byte B, double Luma) Analyze(Bitmap bmp, int method, bool ignoreDark)
	{
		BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			int stepY = Math.Max(1, bmp.Height / 36);
			int stepX = Math.Max(1, bmp.Width / 64);
			byte[] row = new byte[data.Stride];
			double sr = 0, sg = 0, sb = 0, sw = 0, luma = 0;
			double ar = 0, ag = 0, ab = 0;
			long all = 0;
			for (int y = 0; y < bmp.Height; y += stepY)
			{
				Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
				for (int x = 0; x < bmp.Width; x += stepX)
				{
					int o = x * 4;
					int b = row[o], g = row[o + 1], r = row[o + 2];
					ar += r; ag += g; ab += b; all++;
					int max = Math.Max(r, Math.Max(g, b));
					int min = Math.Min(r, Math.Min(g, b));
					luma += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
					if (ignoreDark && max < 28)
					{
						continue;
					}
					double w = 1.0;
					if (method == 1)
					{
						double sat = max == 0 ? 0 : (max - min) / (double)max;
						w = 0.03 + sat * sat * (max / 255.0);
					}
					sr += r * w; sg += g * w; sb += b * w; sw += w;
				}
			}
			if (all == 0)
			{
				return (0, 0, 0, 0);
			}
			if (sw <= 1e-6)
			{
				return ((byte)(ar / all), (byte)(ag / all), (byte)(ab / all), luma / all);
			}
			return ((byte)Math.Clamp(sr / sw, 0, 255), (byte)Math.Clamp(sg / sw, 0, 255), (byte)Math.Clamp(sb / sw, 0, 255), luma / all);
		}
		finally
		{
			bmp.UnlockBits(data);
		}
	}

	public void Dispose()
	{
		graphics?.Dispose();
		bitmap?.Dispose();
		graphics = null;
		bitmap = null;
	}
}
