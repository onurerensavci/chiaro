using System;
using System.IO;
using HueStudio;
using Xunit;
using MColor = System.Windows.Media.Color;

namespace ChiaroMac.Tests;

public class AmbientTests
{
	private static byte[] Solid(int w, int h, byte r, byte g, byte b)
	{
		byte[] px = new byte[w * h * 4];
		for (int i = 0; i < w * h; i++)
		{
			px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
		}
		return px;
	}

	[Theory]
	[InlineData(0, 0, 0, 1920, 1080)]
	[InlineData(1, 480, 270, 960, 540)]
	[InlineData(2, 0, 0, 1920, 270)]
	[InlineData(3, 0, 810, 1920, 270)]
	[InlineData(4, 0, 0, 480, 1080)]
	[InlineData(5, 1440, 0, 480, 1080)]
	public void Region_matches_windows(int area, double x, double y, double w, double h)
	{
		Assert.Equal((x, y, w, h), ScreenMath.Region(0, 0, 1920, 1080, area));
	}

	[Fact]
	public void OutputSize_keeps_aspect_and_limit()
	{
		var (w, h) = ScreenMath.OutputSize(3024, 1964);
		Assert.Equal(128, w);
		Assert.InRange(h, 82, 84);
		Assert.Equal((4, 128), ScreenMath.OutputSize(10, 5000));
	}

	[Fact]
	public void Analyze_solid_color()
	{
		byte[] px = Solid(64, 36, 200, 100, 50);
		var c = ScreenMath.Analyze(px, 64, 36, 64 * 4, 0, false);
		Assert.Equal((byte)200, c.R);
		Assert.Equal((byte)100, c.G);
		Assert.Equal((byte)50, c.B);
		Assert.InRange(c.Luma, 0.44, 0.48);
	}

	[Fact]
	public void Analyze_ignore_dark_and_vivid()
	{
		// Üst yarı siyah, alt yarı canlı mavi
		int w = 64, h = 36;
		byte[] px = Solid(w, h, 0, 0, 0);
		for (int y = h / 2; y < h; y++)
			for (int x = 0; x < w; x++)
			{
				int o = (y * w + x) * 4;
				px[o] = 255; px[o + 1] = 40; px[o + 2] = 10;
			}
		var avg = ScreenMath.Analyze(px, w, h, w * 4, 0, false);
		var dark = ScreenMath.Analyze(px, w, h, w * 4, 0, true);
		Assert.InRange(avg.B, 120, 135);
		Assert.Equal((byte)255, dark.B);
		var vivid = ScreenMath.Analyze(px, w, h, w * 4, 1, false);
		Assert.True(vivid.B > 240);
		// Her şey karanlıksa ortalamaya düşer (siyah ekran → siyah)
		var black = ScreenMath.Analyze(Solid(8, 8, 5, 5, 5), 8, 8, 32, 0, true);
		Assert.Equal((byte)5, black.R);
	}

	[Fact]
	public void Analyze_respects_stride_padding()
	{
		int w = 10, h = 4, stride = 64;
		byte[] px = new byte[stride * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
			{
				int o = y * stride + x * 4;
				px[o] = 0; px[o + 1] = 0; px[o + 2] = 255;
			}
		for (int y = 0; y < h; y++)
			for (int o = y * stride + w * 4; o < (y + 1) * stride; o++) px[o] = 255;
		var c = ScreenMath.Analyze(px, w, h, stride, 0, false);
		Assert.Equal((255, 0, 0), (c.R, c.G, c.B));
	}

	[Fact]
	public void AudioMath_peak_float_and_int16()
	{
		float[] f = { 0.1f, -0.75f, 0.3f };
		byte[] fb = new byte[f.Length * 4];
		Buffer.BlockCopy(f, 0, fb, 0, fb.Length);
		Assert.Equal(0.75f, AudioMath.Peak(fb, true, 32), 3);
		short[] s = { 1000, -16384, 200 };
		byte[] sb = new byte[s.Length * 2];
		Buffer.BlockCopy(s, 0, sb, 0, sb.Length);
		Assert.Equal(0.5f, AudioMath.Peak(sb, false, 16), 2);
		Assert.Equal(0f, AudioMath.Peak(Array.Empty<byte>(), true, 32));
	}

	[Fact]
	public void AudioMath_level_range()
	{
		Assert.Equal(5, AudioMath.Level(0, 5, 5, 100));
		Assert.Equal(100, AudioMath.Level(1, 5, 5, 100));
		Assert.Equal(100, AudioMath.Level(0.5, 5, 5, 100));
		double mid = AudioMath.Level(0.05, 5, 5, 100);
		Assert.InRange(mid, 50, 75);
	}

	[Fact]
	public void CoverColor_dominant_prefers_saturated()
	{
		int w = 48, h = 48;
		byte[] px = Solid(w, h, 240, 240, 240); // çoğu beyaz
		for (int i = 0; i < w * h / 3; i++)
		{
			px[i * 4] = 40; px[i * 4 + 1] = 40; px[i * 4 + 2] = 220; // BGRA: kırmızı
		}
		MColor c = CoverColor.Dominant(px, w, h, w * 4, bgra: true);
		Assert.True(c.R > 180 && c.G < 120 && c.B < 120, $"{c.R},{c.G},{c.B}");
	}

	[Fact]
	public void CoverColor_grey_falls_back_to_warm()
	{
		byte[] px = Solid(16, 16, 128, 128, 128);
		MColor c = CoverColor.Dominant(px, 16, 16, 64, bgra: true);
		Assert.True(c.R >= c.B, $"{c.R},{c.G},{c.B}");
	}

	[Fact]
	public void CoverColor_boost_increases_saturation()
	{
		MColor baseC = MColor.FromRgb(200, 120, 100);
		MColor boosted = CoverColor.Boost(baseC, 60);
		var (_, s0, _) = ColorMath.ToHsv(baseC);
		var (_, s1, _) = ColorMath.ToHsv(boosted);
		Assert.True(s1 > s0);
		Assert.Equal(baseC, CoverColor.Boost(baseC, 0));
	}
}

public class MacSystemTests
{
	private static string Release(string tag, params string[] assets) =>
		"{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/onurerensavci/chiaro/releases/tag/" + tag + "\",\"body\":\"notlar\",\"assets\":[" +
		string.Join(",", Array.ConvertAll(assets, a => "{\"name\":\"" + a + "\",\"size\":123,\"browser_download_url\":\"https://github.com/x/" + a + "\",\"digest\":\"sha256:ABCDEF\"}")) + "]}";

	[Fact]
	public void Current_version_from_program()
	{
		Assert.Equal(new Version(3, 9, 0), MacUpdater.Current);
	}

	[Fact]
	public void Release_3_9_is_current_and_3_10_is_newer()
	{
		Version cur = MacUpdater.Current;
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.7", "Chiaro_v3.7.exe", "Chiaro_v3.7_mac.zip"), cur).Status);
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.8", "Chiaro_v3.8.exe", "Chiaro_v3.8_mac.zip"), cur).Status);
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.9", "Chiaro_v3.9.exe", "Chiaro_v3.9_mac.zip"), cur).Status);
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.9.0", "Chiaro_v3.9.0_mac.zip"), cur).Status);
		var (u, status) = MacUpdater.Evaluate(Release("v3.10", "Chiaro_v3.10.exe", "Chiaro_v3.10.exe.sha256", "Chiaro_v3.10_mac.zip", "Chiaro_v3.10_mac.zip.sha256"), cur);
		Assert.Equal("mac_upd_available", status);
		Assert.Equal("Chiaro_v3.10_mac.zip", u!.AssetName);
		Assert.Equal("mac_upd_available", MacUpdater.Evaluate(Release("v3.9.1", "Chiaro_v3.9.1_mac.zip"), cur).Status);
		Assert.Equal("mac_upd_available", MacUpdater.Evaluate(Release("v4.0", "Chiaro_v4.0_mac.zip"), cur).Status);
		Assert.True(MacUpdater.Compare(new Version(3, 10), new Version(3, 9, 0)) > 0);
		Assert.Equal(0, MacUpdater.Compare(new Version(3, 9), new Version(3, 9, 0, 0)));
	}

	[Theory]
	[InlineData("v3.5", true)]
	[InlineData("3.5.1", true)]
	[InlineData("Chiaro v3.6", true)]
	[InlineData("latest", false)]
	[InlineData("", false)]
	public void TryParse_tags(string tag, bool ok)
	{
		Assert.Equal(ok, MacUpdater.TryParse(tag, out _));
	}

	[Fact]
	public void Evaluate_newer_with_mac_asset()
	{
		var (u, status) = MacUpdater.Evaluate(Release("v3.5", "Chiaro_v3.5.zip", "Chiaro_v3.5_mac.zip"), new Version(3, 4, 0));
		Assert.Equal("mac_upd_available", status);
		Assert.NotNull(u);
		Assert.Equal("3.5", u!.Version);
		Assert.Equal("Chiaro_v3.5_mac.zip", u.AssetName);
		Assert.Equal("abcdef", u.Sha256);
	}

	[Fact]
	public void Evaluate_newer_windows_only_disables_gracefully()
	{
		var (u, status) = MacUpdater.Evaluate(Release("v3.5", "Chiaro_v3.5.zip", "Chiaro_v3.5_setup.exe"), new Version(3, 4, 0));
		Assert.Null(u);
		Assert.Equal("mac_upd_no_mac", status);
	}

	[Fact]
	public void Evaluate_same_or_older_is_latest()
	{
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.4", "Chiaro_v3.4_mac.zip"), new Version(3, 4, 0)).Status);
		Assert.Equal("mac_upd_latest", MacUpdater.Evaluate(Release("v3.3.2", "Chiaro_v3.3.2_mac.zip"), new Version(3, 4, 0)).Status);
		Assert.Equal("mac_upd_none", MacUpdater.Evaluate("{\"message\":\"Not Found\"}", new Version(3, 4, 0)).Status);
	}

	[Fact]
	public void Evaluate_rejects_non_https_asset()
	{
		string json = Release("v3.5", "Chiaro_v3.5_mac.zip").Replace("https://github.com/x/", "http://evil/");
		Assert.Equal("mac_upd_no_mac", MacUpdater.Evaluate(json, new Version(3, 4, 0)).Status);
	}

	[Theory]
	[InlineData("Chiaro_v3.5_mac.zip", true)]
	[InlineData("Chiaro_3.5.1_mac.zip", true)]
	[InlineData("Chiaro_v3.5_mac_intel.zip", false)]
	[InlineData("Chiaro_v3.5.zip", false)]
	public void Asset_regex(string name, bool match)
	{
		Assert.Equal(match, MacUpdater.AssetRx.IsMatch(name));
	}

	[Fact]
	public void BundlePath_and_translocation()
	{
		Assert.Equal(Path.GetFullPath("/Applications/Chiaro.app"), LoginItem.BundlePath("/Applications/Chiaro.app/Contents/MacOS/Chiaro"));
		Assert.Null(LoginItem.BundlePath("/usr/local/bin/Chiaro"));
		Assert.True(LoginItem.IsTranslocated("/private/var/folders/x/AppTranslocation/ABC/d/Chiaro.app"));
		Assert.False(LoginItem.IsTranslocated("/Applications/Chiaro.app"));
	}

	[Fact]
	public void LaunchAgent_plist_is_valid_and_escaped()
	{
		string xml = LoginItem.Plist("/Users/test/Apps & Tools/Chiaro.app");
		var doc = System.Xml.Linq.XDocument.Parse(xml);
		string all = doc.ToString();
		Assert.Contains("io.github.onurerensavci.chiaro", all);
		Assert.Contains("/Users/test/Apps &amp; Tools/Chiaro.app", all);
		Assert.Contains("--background", all);
		Assert.Contains("/usr/bin/open", all);
		Assert.StartsWith("<?xml", xml);
	}

	[Fact]
	public void MacPrefs_roundtrip_defaults()
	{
		MacPrefs p = System.Text.Json.JsonSerializer.Deserialize<MacPrefs>("{}")!;
		Assert.True(p.AutoUpdateCheck);
		Assert.Equal(0, p.AudioSource);
	}
}
