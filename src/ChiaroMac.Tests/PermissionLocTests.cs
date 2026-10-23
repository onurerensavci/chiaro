using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

[Collection("Loc")]
public class PermissionLocTests
{
	[Theory]
	[InlineData("mac_perm_screen")]
	[InlineData("mac_perm_mic")]
	[InlineData("mac_perm_auto_spotify")]
	[InlineData("mac_perm_auto_music")]
	[InlineData("mac_no_sck")]
	[InlineData("mac_no_bundle")]
	[InlineData("mac_translocated")]
	public void Native_turkish_messages_translate_to_english(string key)
	{
		try
		{
			L.Apply("en");
			Assert.Equal(MacStrings.En[key], L.Message(MacStrings.Tr[key]));
			L.Apply("tr");
			Assert.Equal(MacStrings.Tr[key], L.Message(MacStrings.Tr[key]));
		}
		finally
		{
			L.Apply("tr");
		}
	}

	[Fact]
	public void New_mode_keys_exist_in_both_languages()
	{
		foreach (string k in new[] { "mac_perm_screen_btn", "mac_perm_mic_btn", "mac_perm_auto_btn", "mac_audio_sources", "autostart", "mac_upd_available", "mac_mini_back" })
		{
			Assert.True(MacStrings.Tr.ContainsKey(k), k);
			Assert.True(MacStrings.En.ContainsKey(k), k);
		}
		Assert.Equal(2, MacStrings.Tr["mac_audio_sources"].Split(',').Length);
		Assert.Equal(2, MacStrings.En["mac_audio_sources"].Split(',').Length);
		Assert.Contains("{0}", MacStrings.En["mac_upd_available"]);
	}
}
