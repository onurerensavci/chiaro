using HueStudio;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace ChiaroMac.Tests;

[Collection("Loc")]
public class LocTests
{
	[Fact]
	public void MacStringsTrEnParity()
	{
		Assert.Empty(MacStrings.Tr.Keys.Except(MacStrings.En.Keys));
		Assert.Empty(MacStrings.En.Keys.Except(MacStrings.Tr.Keys));
	}

	[Fact]
	public void SharedStringsTrEnParity()
	{
		Assert.Empty(Strings.Tr.Keys.Except(Strings.En.Keys));
	}

	[Fact]
	public void FormatPlaceholdersMatch()
	{
		static string P(string s) => string.Join(",", Regex.Matches(s, @"\{\d+[^}]*\}").Select(m => m.Value).OrderBy(x => x));
		foreach (var kv in MacStrings.Tr)
			Assert.True(P(kv.Value) == P(MacStrings.En[kv.Key]), kv.Key);
	}

	[Fact]
	public void SystemLanguageDetection()
	{
		Assert.Equal("tr", L.Resolve("tr"));
		Assert.Equal("en", L.Resolve("en"));
		// Linux'ta (CI) Objc yok; kültür bilgisi kullanılır.
		if (!OperatingSystem.IsMacOS())
		{
			Assert.Equal("tr", L.SystemLanguage(CultureInfo.GetCultureInfo("tr-TR")));
			Assert.Equal("en", L.SystemLanguage(CultureInfo.GetCultureInfo("de-DE")));
		}
	}

	[Fact]
	public void BleMessagesTranslateToEnglish()
	{
		try
		{
			L.Apply("en");
			string[] tr =
			{
				HueGattTransport.Describe(GattException.FromNSError("CBErrorDomain", 14, "")),
				HueGattTransport.Describe(GattException.FromNSError("CBErrorDomain", 15, "")),
				HueGattTransport.Describe(new GattException(GattFailure.Att, "", 5)),
				HueGattTransport.Describe(new GattException(GattFailure.Unauthorized, "")),
				"Bluetooth izni verilmedi. Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth'tan Chiaro'ya izin ver.",
				"Bluetooth kapalı. Denetim Merkezi'nden aç.",
			};
			foreach (string s in tr)
			{
				string en = L.Message("Komut: " + s);
				Assert.DoesNotMatch("[ğüşıöçĞÜŞİÖÇ]", en);
			}
			Assert.Equal(MacStrings.En["mac_upd_install"], L.T("mac_upd_install"));
		}
		finally
		{
			L.Apply("tr");
		}
	}

	[Fact]
	public void LogScrubsHome()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home) || home == "/") return;
		Assert.DoesNotContain(home, AppLog.Scrub($"x {home}/Library/y"));
	}
}
