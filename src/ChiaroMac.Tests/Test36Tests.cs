using HueStudio;
using System.Text.RegularExpressions;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>3.6: "Genel" → "Ayarlar / Settings" ve "Bilgisayar açılınca ışığı aç".</summary>
[Collection("Loc")]
public class Test36Tests
{
	private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

	[Fact]
	public void Settings_card_is_renamed_in_both_languages()
	{
		Assert.Equal("Ayarlar", Strings.Tr["settings_title"]);
		Assert.Equal("Settings", Strings.En["settings_title"]);
		Assert.False(Strings.Tr.ContainsKey("general"));
		Assert.False(Strings.En.ContainsKey("general"));
		Assert.DoesNotContain("Genel", Strings.Tr.Values);
		Assert.DoesNotContain("General", Strings.En.Values);
		string axaml = File.ReadAllText(Path.Combine(Repo(), "ChiaroMac", "MainWindow.axaml"));
		Assert.Contains("ui:Tr.Key=\"settings_title\"", axaml);
		Assert.DoesNotContain("ui:Tr.Key=\"general\"", axaml);
	}

	[Fact]
	public void Light_on_startup_labels_and_mac_tips()
	{
		Assert.Equal("Bilgisayar açılınca ışığı aç", Strings.Tr["light_on_startup"]);
		Assert.Equal("Turn on light at startup", Strings.En["light_on_startup"]);
		Assert.Contains("Oturum açılışında başlat", MacStrings.Tr["light_on_startup_needs"]);
		Assert.Contains("Open at login", MacStrings.En["light_on_startup_needs"]);
		Assert.Contains("Windows ile başlat", Strings.Tr["light_on_startup_needs"]);
		Assert.Equal(Strings.Tr.Count, Strings.En.Count);
	}

	[Fact]
	public void Checkbox_sits_right_under_login_option()
	{
		string axaml = File.ReadAllText(Path.Combine(Repo(), "ChiaroMac", "MainWindow.axaml"));
		string[] boxes = Regex.Matches(axaml, "<CheckBox x:Name=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
		int i = Array.IndexOf(boxes, "AutostartBox");
		Assert.True(i >= 0 && boxes[i + 1] == "LightOnStartupBox", string.Join(",", boxes));
		Assert.Contains("ToolTip.ShowOnDisabled=\"True\"", axaml);
	}

	[Fact]
	public void Only_login_launch_with_option_and_login_item_triggers()
	{
		Assert.True(StartupLight.ShouldRun(autostartLaunch: true, optionOn: true, startupEnabled: true));
		Assert.False(StartupLight.ShouldRun(false, true, true));   // elle açılış
		Assert.False(StartupLight.ShouldRun(true, false, true));   // seçenek kapalı
		Assert.False(StartupLight.ShouldRun(true, true, false));   // oturum açılışı kapalı
		// LaunchAgent --background ile açar; güncelleme yardımcısı bu bayrağı vermez.
		string plist = LoginItem.Plist("/Applications/Chiaro.app");
		Assert.Contains("<string>" + LoginItem.BackgroundArgument + "</string>", plist);
		string selfUpdate = File.ReadAllText(Path.Combine(Repo(), "ChiaroMac", "Mac", "SelfUpdate.cs"));
		Assert.DoesNotContain("BackgroundArgument", selfUpdate);
		Assert.DoesNotContain("--background", selfUpdate);
	}

	[Fact]
	public void Backoff_stays_within_about_60_seconds()
	{
		TimeSpan t = TimeSpan.Zero;
		List<double> waits = new();
		for (int attempt = 0; StartupLight.NextDelay(attempt, t) is TimeSpan d; attempt++)
		{
			waits.Add(d.TotalSeconds);
			t += d;
		}
		Assert.True(waits.Count >= 4, string.Join(",", waits));
		Assert.True(waits[0] < waits[1] && waits[1] < waits[2]);
		Assert.True(t <= StartupLight.Window);
		Assert.Null(StartupLight.NextDelay(0, StartupLight.Window));
	}

	[Fact]
	public void Power_on_skipped_over_running_mode_or_user_action()
	{
		Assert.Null(StartupLight.SkipReason(false, "", false));
		Assert.Null(StartupLight.SkipReason(false, "", null));
		Assert.NotNull(StartupLight.SkipReason(false, "Ekran", false));
		Assert.NotNull(StartupLight.SkipReason(false, "", true));
		Assert.NotNull(StartupLight.SkipReason(true, "", false));
	}

	[Fact]
	public void Setting_defaults_off_and_persists()
	{
		string src = File.ReadAllText(Path.Combine(Repo(), "Chiaro", "Core", "StudioSettings.cs"));
		Assert.Matches(@"public bool LightOnStartup \{ get; set; \}\s*\n", src);
	}

	[Fact]
	public async Task Demo_backend_can_fail_first_connects()
	{
		Environment.SetEnvironmentVariable("CHIARO_DEMO_FAIL_CONNECTS", "2");
		try
		{
			DemoBackend b = new DemoBackend();
			await Assert.ThrowsAsync<GattException>(() => b.ConnectAsync(DemoBackend.DemoId, _ => { }, CancellationToken.None));
			await Assert.ThrowsAsync<GattException>(() => b.ConnectAsync(DemoBackend.DemoId, _ => { }, CancellationToken.None));
			Assert.NotNull(await b.ConnectAsync(DemoBackend.DemoId, _ => { }, CancellationToken.None));
		}
		finally
		{
			Environment.SetEnvironmentVariable("CHIARO_DEMO_FAIL_CONNECTS", null);
		}
	}

	[Fact]
	public void Test_tag_absent_in_test_project()
	{
		Assert.Null(BuildTag.Test);
		Assert.Equal("", BuildTag.Suffix);
	}
}
