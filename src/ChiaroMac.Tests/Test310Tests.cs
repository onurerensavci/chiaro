using Xunit;

namespace ChiaroMac.Tests;

/// <summary>3.10: yeni siyah-beyaz "liquid glass" simge — tüm simge dosyaları aynı tasarımdan, gerekli boyutlarda.</summary>
public class Test310Tests
{
	private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
	private static byte[] Bytes(params string[] parts) => File.ReadAllBytes(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

	private static (int W, int H) PngSize(byte[] d)
	{
		Assert.True(d.Length > 24 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G', "PNG");
		int Be(int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
		return (Be(16), Be(20));
	}

	[Fact]
	public void Windows_ico_has_all_sizes_and_app_ico_matches()
	{
		byte[] ico = Bytes("src", "Chiaro", "Assets", "Chiaro.ico");
		Assert.Equal(ico, Bytes("src", "Chiaro", "app.ico"));
		Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
		Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
		int n = BitConverter.ToUInt16(ico, 4);
		var sizes = Enumerable.Range(0, n).Select(i => ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i]).ToHashSet();
		foreach (int s in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }) Assert.Contains(s, sizes);
	}

	[Fact]
	public void Mac_icns_has_retina_sizes_up_to_1024()
	{
		byte[] d = Bytes("src", "ChiaroMac", "Packaging", "Chiaro.icns");
		Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(d, 0, 4));
		var types = new Dictionary<string, (int, int)>();
		for (int i = 8; i < d.Length;)
		{
			string t = System.Text.Encoding.ASCII.GetString(d, i, 4);
			int len = (d[i + 4] << 24) | (d[i + 5] << 16) | (d[i + 6] << 8) | d[i + 7];
			types[t] = PngSize(d[(i + 8)..(i + len)]);
			i += len;
		}
		Assert.Equal((16, 16), types["icp4"]);
		Assert.Equal((32, 32), types["ic11"]);
		Assert.Equal((64, 64), types["ic12"]);
		Assert.Equal((256, 256), types["ic13"]);
		Assert.Equal((512, 512), types["ic14"]);
		Assert.Equal((1024, 1024), types["ic10"]);
	}

	[Fact]
	public void App_logo_pngs_are_the_new_dark_icon()
	{
		Assert.Equal((128, 128), PngSize(Bytes("src", "Chiaro", "Assets", "logo", "logo-128.png")));
		Assert.Equal(Bytes("src", "Chiaro", "Assets", "logo", "logo-128.png"), Bytes("src", "ChiaroMac", "Assets", "logo-128.png"));
		Assert.Equal((256, 256), PngSize(Bytes("src", "ChiaroMac", "Assets", "chiaro-256.png")));
		Assert.Equal((44, 44), PngSize(Bytes("src", "ChiaroMac", "Assets", "tray-color.png")));
		Assert.Equal((512, 512), PngSize(Bytes("docs", "logo.png")));
		string svg = File.ReadAllText(Path.Combine(Repo(), "src", "Chiaro", "Assets", "logo", "logo.svg"));
		Assert.Contains("#FFFFFF", svg);
		Assert.DoesNotContain("#FF9A55", svg); // eski renkli parıltı yok
	}
}
