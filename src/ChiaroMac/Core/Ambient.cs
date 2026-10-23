#nullable enable
using System;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

/// <summary>Windows ScreenSampler / AmbientInput / MediaColorReader hesaplarının platformdan bağımsız kopyası.</summary>
public static class ScreenMath
{
	/// <summary>Windows ScreenSampler.Region ile aynı: 0 tüm, 1 orta, 2 üst, 3 alt, 4 sol, 5 sağ.</summary>
	public static (double X, double Y, double W, double H) Region(double x, double y, double w, double h, int area)
	{
		double qh = Math.Max(1, Math.Floor(h / 4)), qw = Math.Max(1, Math.Floor(w / 4));
		return area switch
		{
			1 => (x + Math.Floor(w / 4), y + Math.Floor(h / 4), Math.Max(1, Math.Floor(w / 2)), Math.Max(1, Math.Floor(h / 2))),
			2 => (x, y, w, qh),
			3 => (x, y + h - qh, w, qh),
			4 => (x, y, qw, h),
			5 => (x + w - qw, y, qw, h),
			_ => (x, y, w, h),
		};
	}

	/// <summary>Bölgeyi en uzun kenarı ~max piksel olacak şekilde küçültülmüş çıktı boyutu.</summary>
	public static (int W, int H) OutputSize(double w, double h, int max = 128)
	{
		double s = max / Math.Max(1, Math.Max(w, h));
		return (Math.Max(4, (int)Math.Round(w * s)), Math.Max(4, (int)Math.Round(h * s)));
	}

	/// <summary>Windows ScreenSampler.Analyze ile aynı algoritma (BGRA satırları).</summary>
	public static (byte R, byte G, byte B, double Luma) Analyze(ReadOnlySpan<byte> bgra, int width, int height, int stride, int method, bool ignoreDark)
	{
		int stepY = Math.Max(1, height / 36);
		int stepX = Math.Max(1, width / 64);
		double sr = 0, sg = 0, sb = 0, sw = 0, luma = 0;
		double ar = 0, ag = 0, ab = 0;
		long all = 0;
		for (int y = 0; y < height; y += stepY)
		{
			int row = y * stride;
			for (int x = 0; x < width; x += stepX)
			{
				int o = row + x * 4;
				if (o + 2 >= bgra.Length) break;
				int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
				ar += r; ag += g; ab += b; all++;
				int max = Math.Max(r, Math.Max(g, b));
				int min = Math.Min(r, Math.Min(g, b));
				luma += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
				if (ignoreDark && max < 28) continue;
				double w = 1.0;
				if (method == 1)
				{
					double sat = max == 0 ? 0 : (max - min) / (double)max;
					w = 0.03 + sat * sat * (max / 255.0);
				}
				sr += r * w; sg += g * w; sb += b * w; sw += w;
			}
		}
		if (all == 0) return (0, 0, 0, 0);
		if (sw <= 1e-6) return ((byte)(ar / all), (byte)(ag / all), (byte)(ab / all), luma / all);
		return ((byte)Math.Clamp(sr / sw, 0, 255), (byte)Math.Clamp(sg / sw, 0, 255), (byte)Math.Clamp(sb / sw, 0, 255), luma / all);
	}
}

public static class AudioMath
{
	/// <summary>Ses örneklerinin mutlak tepe değeri (0..1). Float32 ya da 16/32 bit tamsayı PCM.</summary>
	public static float Peak(ReadOnlySpan<byte> data, bool isFloat, int bits)
	{
		float peak = 0;
		if (isFloat && bits == 32)
		{
			ReadOnlySpan<float> f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
			for (int i = 0; i < f.Length; i++)
			{
				float a = Math.Abs(f[i]);
				if (a > peak && float.IsFinite(a)) peak = a;
			}
		}
		else if (bits == 16)
		{
			ReadOnlySpan<short> s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data);
			for (int i = 0; i < s.Length; i++) peak = Math.Max(peak, Math.Abs(s[i] / 32768f));
		}
		else if (bits == 32)
		{
			ReadOnlySpan<int> s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(data);
			for (int i = 0; i < s.Length; i++) peak = Math.Max(peak, Math.Abs(s[i] / 2147483648f));
		}
		return Math.Clamp(peak, 0f, 1f);
	}

	/// <summary>Windows AmbientInput.AudioLevel ile aynı.</summary>
	public static double Level(double peak, double gain, double min, double max) => min + (max - min) * Math.Sqrt(Math.Clamp(peak * gain, 0.0, 1.0));
}

public static class CoverColor
{
	/// <summary>Windows MediaColorReader.Dominant ile aynı (48×48 RGBA/BGRA örnek üzerinde ton histogramı).</summary>
	public static MColor Dominant(ReadOnlySpan<byte> pixels, int width, int height, int stride, bool bgra)
	{
		double[] weight = new double[24];
		double[] sr = new double[24], sg = new double[24], sb = new double[24];
		double ar = 0, ag = 0, ab = 0;
		int n = 0;
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int o = y * stride + x * 4;
				byte r = bgra ? pixels[o + 2] : pixels[o], g = pixels[o + 1], b = bgra ? pixels[o] : pixels[o + 2];
				ar += r; ag += g; ab += b; n++;
				var (h, s, v) = ColorMath.ToHsv(MColor.FromRgb(r, g, b));
				if (s < 0.22 || v < 0.18) continue;
				double w = s * s * v;
				int bin = (int)(h / 15.0) % 24;
				weight[bin] += w; sr[bin] += r * w; sg[bin] += g * w; sb[bin] += b * w;
			}
		}
		if (n == 0) return ColorMath.FromKelvin(3000);
		int best = -1;
		double bestScore = 0;
		for (int i = 0; i < 24; i++)
		{
			double score = weight[i] + 0.5 * (weight[(i + 23) % 24] + weight[(i + 1) % 24]);
			if (score > bestScore) { bestScore = score; best = i; }
		}
		if (best < 0 || weight[best] <= 0.5)
		{
			MColor avg = MColor.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
			return ColorMath.Lerp(avg, ColorMath.FromKelvin(3000), 0.5);
		}
		MColor c = MColor.FromRgb(
			(byte)Math.Clamp(sr[best] / weight[best], 0, 255),
			(byte)Math.Clamp(sg[best] / weight[best], 0, 255),
			(byte)Math.Clamp(sb[best] / weight[best], 0, 255));
		var (hh, ss, _) = ColorMath.ToHsv(c);
		return ColorMath.FromHsv(hh, Math.Clamp(ss * 1.1, 0, 1), 1.0);
	}

	public static MColor Boost(MColor c, double percent)
	{
		var (h, s, v) = ColorMath.ToHsv(c);
		return ColorMath.FromHsv(h, Math.Min(1.0, s * (1.0 + percent / 100.0)), v);
	}
}
