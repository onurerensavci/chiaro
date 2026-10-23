using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>Mac test 3 düzeltmelerinin sınamaları (tek kopya, oturum açılışı yolu, SHA yedeği).</summary>
public class Test3Tests
{
	[Fact]
	public void SingleInstance_second_acquire_fails_until_released()
	{
		string dir = Path.Combine(Path.GetTempPath(), "chiaro-lock-" + Guid.NewGuid().ToString("N"));
		try
		{
			using (SingleInstance? first = SingleInstance.TryAcquire(dir))
			{
				Assert.NotNull(first);
				Assert.Null(SingleInstance.TryAcquire(dir));
			}
			using SingleInstance? again = SingleInstance.TryAcquire(dir);
			Assert.NotNull(again);
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	[Fact]
	public void LaunchAgent_has_bundle_association_and_aqua_session()
	{
		string xml = LoginItem.Plist("/Applications/Chiaro.app");
		var doc = System.Xml.Linq.XDocument.Parse(xml);
		string[] keys = doc.Descendants("key").Select(k => k.Value).ToArray();
		Assert.Contains("AssociatedBundleIdentifiers", keys);
		Assert.Contains("LimitLoadToSessionType", keys);
		Assert.Contains("RunAtLoad", keys);
	}

	[Theory]
	[InlineData("/Applications/Chiaro.app")]
	[InlineData("/Users/test/Apps & Tools/Chiaro.app")]
	public void RegisteredBundle_roundtrips_plist(string bundle)
	{
		Assert.Equal(bundle, LoginItem.RegisteredBundle(LoginItem.Plist(bundle)));
	}

	[Fact]
	public void IsInApplications()
	{
		Assert.True(LoginItem.IsInApplications("/Applications/Chiaro.app", "/Users/t"));
		Assert.True(LoginItem.IsInApplications("/Users/t/Applications/Chiaro.app", "/Users/t"));
		Assert.False(LoginItem.IsInApplications("/Users/t/Downloads/Chiaro.app", "/Users/t"));
		Assert.False(LoginItem.IsInApplications("/private/var/folders/x/AppTranslocation/A/d/Chiaro.app", "/Users/t"));
		Assert.False(LoginItem.IsInApplications(null, "/Users/t"));
	}

	[Fact]
	public void Evaluate_uses_sha256_sidecar_when_digest_missing()
	{
		string json = "{\"tag_name\":\"v3.5\",\"assets\":[" +
			"{\"name\":\"Chiaro_v3.5_mac.zip\",\"size\":1,\"browser_download_url\":\"https://github.com/x/Chiaro_v3.5_mac.zip\"}," +
			"{\"name\":\"Chiaro_v3.5_mac.zip.sha256\",\"size\":1,\"browser_download_url\":\"https://github.com/x/Chiaro_v3.5_mac.zip.sha256\"}]}";
		var (u, status) = MacUpdater.Evaluate(json, new Version(3, 4, 0));
		Assert.Equal("mac_upd_available", status);
		Assert.Null(u!.Sha256);
		Assert.Equal("https://github.com/x/Chiaro_v3.5_mac.zip.sha256", u.ShaUrl);
	}

	[Fact]
	public void ParseSha_accepts_sha256sum_format()
	{
		string h = new string('a', 32) + new string('B', 32);
		Assert.Equal(h.ToLowerInvariant(), MacUpdater.ParseSha(h + "  Chiaro_v3.5_mac.zip\n"));
		Assert.Equal(h.ToLowerInvariant(), MacUpdater.ParseSha(h));
		Assert.Null(MacUpdater.ParseSha("not a hash"));
		Assert.Null(MacUpdater.ParseSha(""));
	}
}
