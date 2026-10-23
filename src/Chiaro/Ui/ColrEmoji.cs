#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace HueStudio;

/// <summary>
/// Küçük, bağımsız renkli emoji çizici. Windows'un Segoe UI Emoji yazı tipini
/// (C:\Windows\Fonts\seguiemj.ttf) doğrudan okur: cmap (biçim 12), COLR v0 katmanları,
/// CPAL paleti ve glyf ana hatları → WPF vektör çizimi. WPF'nin yazı tipi yığınına
/// bağlı değildir. Tek kod noktalı emojiler (VS16 yok sayılır); birleşik dizilerde ilk emoji.
/// </summary>
internal static class ColrEmoji
{
	private static bool loaded;
	private static byte[] d = Array.Empty<byte>();
	private static readonly Dictionary<ushort, (int First, int Count)> baseGlyphs = new();
	private static (ushort Gid, ushort Palette)[] layers = Array.Empty<(ushort, ushort)>();
	private static Color[] palette = Array.Empty<Color>();
	private static readonly List<(uint Start, uint End, uint Glyph)> cmap12 = new();
	private static readonly Dictionary<ushort, Geometry?> outlines = new();
	private static double upm = 2048, ascent = 1900, descent = 500;
	private static int glyf = -1, loca = -1, hmtx = -1, numHMetrics, locFormat;
	internal static string Error = "";

	public static bool Available
	{
		get { Load(); return baseGlyphs.Count > 0 && glyf >= 0; }
	}

	private static void Load()
	{
		if (loaded) return;
		loaded = true;
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "seguiemj.ttf");
			if (!File.Exists(path)) { Error = "font not found"; return; }
			d = File.ReadAllBytes(path);
			int colr = Find("COLR"), cpal = Find("CPAL"), head = Find("head"), hhea = Find("hhea");
			glyf = Find("glyf"); loca = Find("loca"); hmtx = Find("hmtx");
			if (colr < 0 || cpal < 0 || head < 0 || hhea < 0 || glyf < 0 || loca < 0) { Error = "missing tables"; glyf = -1; return; }
			upm = U16(head + 18);
			locFormat = (short)U16(head + 50);
			ascent = (short)U16(hhea + 4);
			descent = -(short)U16(hhea + 6);
			numHMetrics = U16(hhea + 34);
			ReadCmap();
			int numBase = U16(colr + 2);
			int baseOff = colr + (int)U32(colr + 4);
			int layerOff = colr + (int)U32(colr + 8);
			int numLayers = U16(colr + 12);
			for (int i = 0; i < numBase; i++)
			{
				int p = baseOff + i * 6;
				baseGlyphs[(ushort)U16(p)] = (U16(p + 2), U16(p + 4));
			}
			layers = new (ushort, ushort)[numLayers];
			for (int i = 0; i < numLayers; i++)
			{
				int p = layerOff + i * 4;
				layers[i] = ((ushort)U16(p), (ushort)U16(p + 2));
			}
			int numEntries = U16(cpal + 2);
			int recOff = cpal + (int)U32(cpal + 8);
			int first = U16(cpal + 12);
			palette = new Color[numEntries];
			for (int i = 0; i < numEntries; i++)
			{
				int p = recOff + (first + i) * 4; // BGRA
				palette[i] = Color.FromArgb(d[p + 3], d[p + 2], d[p + 1], d[p]);
			}
		}
		catch (Exception ex)
		{
			Error = ex.GetType().Name + ": " + ex.Message;
			glyf = -1;
		}
	}

	private static int U16(int p) => (d[p] << 8) | d[p + 1];
	private static uint U32(int p) => (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
	private static short S16(int p) => (short)U16(p);

	private static int Find(string tag)
	{
		int n = U16(4);
		for (int i = 0; i < n; i++)
		{
			int p = 12 + i * 16;
			if (d[p] == tag[0] && d[p + 1] == tag[1] && d[p + 2] == tag[2] && d[p + 3] == tag[3]) return (int)U32(p + 8);
		}
		return -1;
	}

	private static void ReadCmap()
	{
		int cmap = Find("cmap");
		if (cmap < 0) return;
		int n = U16(cmap + 2);
		for (int i = 0; i < n; i++)
		{
			int off = cmap + (int)U32(cmap + 4 + i * 8 + 4);
			if (U16(off) != 12) continue;
			uint groups = U32(off + 12);
			for (uint g = 0; g < groups; g++)
			{
				int p = off + 16 + (int)g * 12;
				cmap12.Add((U32(p), U32(p + 4), U32(p + 8)));
			}
			return;
		}
	}

	private static bool MapChar(int cp, out ushort gid)
	{
		gid = 0;
		int lo = 0, hi = cmap12.Count - 1;
		while (lo <= hi)
		{
			int mid = (lo + hi) / 2;
			var g = cmap12[mid];
			if (cp < g.Start) hi = mid - 1;
			else if (cp > g.End) lo = mid + 1;
			else { gid = (ushort)(g.Glyph + (uint)cp - g.Start); return true; }
		}
		return false;
	}

	private static int GlyphOffset(int gid, out int length)
	{
		int a, b;
		if (locFormat == 0) { a = U16(loca + gid * 2) * 2; b = U16(loca + gid * 2 + 2) * 2; }
		else { a = (int)U32(loca + gid * 4); b = (int)U32(loca + gid * 4 + 4); }
		length = b - a;
		return glyf + a;
	}

	private static double Advance(int gid)
	{
		if (hmtx < 0 || numHMetrics == 0) return upm;
		int i = Math.Min(gid, numHMetrics - 1);
		return U16(hmtx + i * 4);
	}

	/// <summary>Yazı tipi birimlerinde (y yukarı) ana hat → em kutusunda WPF geometrisi.</summary>
	private static Geometry? Outline(ushort gid, int depth = 0)
	{
		if (outlines.TryGetValue(gid, out Geometry? cached)) return cached;
		Geometry? result = null;
		int off = GlyphOffset(gid, out int len);
		if (len > 0)
		{
			int contours = S16(off);
			if (contours >= 0) result = Simple(off, contours);
			else if (depth < 4) result = Composite(off, depth);
		}
		result?.Freeze();
		outlines[gid] = result;
		return result;
	}

	private static Point P(double x, double y) => new Point(x / upm, (ascent - y) / upm);

	private static Geometry Simple(int off, int contours)
	{
		int[] ends = new int[contours];
		for (int i = 0; i < contours; i++) ends[i] = U16(off + 10 + i * 2);
		int n = contours == 0 ? 0 : ends[^1] + 1;
		int p = off + 10 + contours * 2;
		int insLen = U16(p);
		p += 2 + insLen;
		byte[] flags = new byte[n];
		for (int i = 0; i < n;)
		{
			byte f = d[p++];
			flags[i++] = f;
			if ((f & 8) != 0)
			{
				int rep = d[p++];
				while (rep-- > 0 && i < n) flags[i++] = f;
			}
		}
		int[] xs = new int[n], ys = new int[n];
		int v = 0;
		for (int i = 0; i < n; i++)
		{
			byte f = flags[i];
			if ((f & 2) != 0) { int dx = d[p++]; v += (f & 16) != 0 ? dx : -dx; }
			else if ((f & 16) == 0) { v += S16(p); p += 2; }
			xs[i] = v;
		}
		v = 0;
		for (int i = 0; i < n; i++)
		{
			byte f = flags[i];
			if ((f & 4) != 0) { int dy = d[p++]; v += (f & 32) != 0 ? dy : -dy; }
			else if ((f & 32) == 0) { v += S16(p); p += 2; }
			ys[i] = v;
		}
		StreamGeometry geo = new StreamGeometry { FillRule = FillRule.Nonzero };
		using (StreamGeometryContext ctx = geo.Open())
		{
			int start = 0;
			for (int c = 0; c < contours; c++)
			{
				int end = ends[c];
				int count = end - start + 1;
				if (count < 2) { start = end + 1; continue; }
				bool On(int k) => (flags[start + ((k % count) + count) % count] & 1) != 0;
				(double X, double Y) Pt(int k) { int i = start + ((k % count) + count) % count; return (xs[i], ys[i]); }
				// Başlangıç: ilk eğri üstü nokta (yoksa iki kontrolün ortası).
				int s0 = -1;
				for (int k = 0; k < count; k++) if (On(k)) { s0 = k; break; }
				(double X, double Y) begin;
				if (s0 < 0) { var a = Pt(0); var b = Pt(1); begin = ((a.X + b.X) / 2, (a.Y + b.Y) / 2); s0 = 0; }
				else begin = Pt(s0);
				ctx.BeginFigure(P(begin.X, begin.Y), true, true);
				(double X, double Y)? ctrl = null;
				for (int k = 1; k <= count; k++)
				{
					int idx = s0 + k;
					var pt = Pt(idx);
					if (On(idx))
					{
						if (ctrl is { } cp) ctx.QuadraticBezierTo(P(cp.X, cp.Y), P(pt.X, pt.Y), true, true);
						else ctx.LineTo(P(pt.X, pt.Y), true, true);
						ctrl = null;
					}
					else
					{
						if (ctrl is { } cp)
						{
							var mid = ((cp.X + pt.X) / 2, (cp.Y + pt.Y) / 2);
							ctx.QuadraticBezierTo(P(cp.X, cp.Y), P(mid.Item1, mid.Item2), true, true);
						}
						ctrl = pt;
					}
				}
				if (ctrl is { } last) ctx.QuadraticBezierTo(P(last.X, last.Y), P(begin.X, begin.Y), true, true);
				start = end + 1;
			}
		}
		return geo;
	}

	private static Geometry? Composite(int off, int depth)
	{
		GeometryGroup group = new GeometryGroup { FillRule = FillRule.Nonzero };
		int p = off + 10;
		while (true)
		{
			int flags = U16(p);
			ushort gid = (ushort)U16(p + 2);
			p += 4;
			double dx, dy;
			if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
			else { dx = (sbyte)d[p]; dy = (sbyte)d[p + 1]; p += 2; }
			double sx = 1, sy = 1, s01 = 0, s10 = 0;
			if ((flags & 8) != 0) { sx = sy = S16(p) / 16384.0; p += 2; }
			else if ((flags & 0x40) != 0) { sx = S16(p) / 16384.0; sy = S16(p + 2) / 16384.0; p += 4; }
			else if ((flags & 0x80) != 0) { sx = S16(p) / 16384.0; s01 = S16(p + 2) / 16384.0; s10 = S16(p + 4) / 16384.0; sy = S16(p + 6) / 16384.0; p += 8; }
			Geometry? child = Outline(gid, depth + 1);
			if (child != null && (flags & 2) != 0)
			{
				// Bileşen em kutusunda; dönüşümü em kutusuna çevir (y aşağı).
				double a = ascent / upm;
				Matrix m = new Matrix(1, 0, 0, 1, 0, -a);           // taban çizgisine göre
				m.Append(new Matrix(sx, -s01, -s10, sy, 0, 0));
				m.Append(new Matrix(1, 0, 0, 1, dx / upm, a - dy / upm));
				Geometry c = child.Clone();
				c.Transform = new MatrixTransform(m);
				group.Children.Add(c);
			}
			if ((flags & 0x20) == 0) break;
		}
		return group.Children.Count == 0 ? null : group;
	}

	/// <summary>Renkli çizim (em kutusu) ya da null.</summary>
	public static Drawing? Render(string text)
	{
		Load();
		if (glyf < 0 || string.IsNullOrEmpty(text)) return null;
		try
		{
			TextElementEnumerator it = StringInfo.GetTextElementEnumerator(text);
			if (!it.MoveNext()) return null;
			int cp = char.ConvertToUtf32((string)it.Current, 0);
			if (!MapChar(cp, out ushort gid) || !baseGlyphs.TryGetValue(gid, out var range)) return null;
			DrawingGroup group = new DrawingGroup();
			double adv = Advance(gid) / upm;
			double height = (ascent + descent) / upm;
			group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, adv, height))));
			for (int i = range.First; i < range.First + range.Count && i < layers.Length; i++)
			{
				(ushort lg, ushort pi) = layers[i];
				Geometry? g = Outline(lg);
				if (g == null) continue;
				Brush b = pi == 0xFFFF || pi >= palette.Length ? Brushes.Black : new SolidColorBrush(palette[pi]);
				group.Children.Add(new GeometryDrawing(b, null, g));
			}
			if (group.Children.Count <= 1) return null;
			group.Freeze();
			return group;
		}
		catch (Exception ex)
		{
			Error = "render " + ex.Message;
			return null;
		}
	}
}
