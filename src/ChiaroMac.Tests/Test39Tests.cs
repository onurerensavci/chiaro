using HueStudio;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>3.9: "Bilgisayar kapanınca ışığı kapat / Turn off light when computer shuts down".</summary>
[Collection("Loc")]
public class Test39Tests
{
	private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

	private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

	private static string Method(string src, string signature)
	{
		int at = src.IndexOf(signature, StringComparison.Ordinal);
		Assert.True(at >= 0, signature);
		int open = src.IndexOf('{', at), depth = 0, i = open;
		for (; i < src.Length; i++)
		{
			if (src[i] == '{') depth++;
			else if (src[i] == '}' && --depth == 0) break;
		}
		return src.Substring(at, i - at + 1);
	}

	[Fact]
	public void Labels_and_tips()
	{
		Assert.Equal("Bilgisayar kapanınca ışığı kapat", Strings.Tr["light_off_on_shutdown"]);
		Assert.Equal("Turn off light when computer shuts down", Strings.En["light_off_on_shutdown"]);
		Assert.Equal("Bilgisayar kapanırken veya yeniden başlarken ampule kapat komutu gönderir", Strings.Tr["light_off_on_shutdown_tip"]);
		Assert.Contains("shuts down or restarts", Strings.En["light_off_on_shutdown_tip"]);
		Assert.StartsWith("Mac kapanırken", MacStrings.Tr["light_off_on_shutdown_tip"]);
		Assert.Contains("Mac", MacStrings.En["light_off_on_shutdown_tip"]);
		Assert.Equal(Strings.Tr.Count, Strings.En.Count);
		Assert.Equal(MacStrings.Tr.Count, MacStrings.En.Count);
	}

	[Fact]
	public void Checkbox_after_restore_last_mode_and_independent_of_startup()
	{
		foreach (string[] f in new[] { new[] { "ChiaroMac", "MainWindow.axaml" }, new[] { "Chiaro", "MainWindow.xaml" } })
		{
			string[] boxes = Regex.Matches(Src(f), "<CheckBox x:Name=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
			int i = Array.IndexOf(boxes, "RestoreLastModeBox");
			Assert.True(i >= 0 && boxes[i + 1] == "LightOffOnShutdownBox", string.Join(",", boxes));
		}
		// Başlangıç kapalıyken soluklaştırılmaz.
		Assert.DoesNotContain("LightOffOnShutdownBox.IsEnabled", Src("ChiaroMac", "MainWindow.Mac.cs"));
		Assert.DoesNotContain("LightOffOnShutdownBox.IsEnabled", Src("Chiaro", "MainWindow.xaml.cs"));
	}

	[Fact]
	public void Default_off_and_persisted()
	{
		Assert.False(new StudioSettings().LightOffOnShutdown);
		Assert.False(JsonSerializer.Deserialize<StudioSettings>("{\"AutoStart\":true,\"RestoreLastMode\":true}")!.LightOffOnShutdown);
		Assert.True(JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(new StudioSettings { LightOffOnShutdown = true }))!.LightOffOnShutdown);
	}

	[Theory]
	[InlineData(true, ExitReason.SystemShutdown, true, null)]
	[InlineData(true, ExitReason.Logoff, true, null)]
	[InlineData(false, ExitReason.SystemShutdown, true, "seçenek kapalı")]
	[InlineData(true, ExitReason.SystemShutdown, false, "ampul bağlı değil")]
	[InlineData(true, ExitReason.AppQuit, true, "normal çıkış")]
	[InlineData(true, ExitReason.UpdateRestart, true, "güncelleme için yeniden başlatma")]
	public void Decision(bool on, ExitReason reason, bool connected, string? skip)
	{
		Assert.Equal(skip, ShutdownLight.SkipReason(on, reason, connected, connected));
	}

	[Fact]
	public async Task Simulated_shutdown_sends_one_power_off_to_connected_bulb()
	{
		var b = new DemoBackend();
		var log = new List<string>();
		var t = new HueGattTransport(b, s => { lock (log) log.Add(s); });
		await t.ConnectAsync(new Lamp(DemoBackend.DemoId, "Hue Play", false, -50), _ => { }, CancellationToken.None);
		Assert.Null(ShutdownLight.SkipReason(true, ExitReason.SystemShutdown, t.Ready, t.Supports(2)));
		string r = ShutdownLight.SendBlocking(ct => t.Write(2, Protocol.Power(on: false), ct), ShutdownLight.WriteTimeout);
		Assert.Equal("ışık kapatıldı", r);
		Assert.Equal(new[] { 2 }, b.Bulb.Writes.Select(w => w.Code).ToArray());
		Assert.Equal(new byte[] { 0 }, b.Bulb.Writes[0].Data);
		lock (log) Assert.Contains(log, l => l.StartsWith("Yazma 0002: 00"));
		t.Disconnect();
		// Bağlı değilken gönderilmez.
		Assert.Equal("ampul bağlı değil", ShutdownLight.SkipReason(true, ExitReason.SystemShutdown, t.Ready, false));
	}

	[Fact]
	public void Stuck_or_failing_write_does_not_hold_shutdown()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		Assert.StartsWith("kapat komutu gönderildi", ShutdownLight.SendBlocking(_ => new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(300)));
		Assert.True(sw.ElapsedMilliseconds < 1200, sw.ElapsedMilliseconds.ToString());
		int calls = 0;
		Assert.StartsWith("gönderilemedi", ShutdownLight.SendBlocking(_ => { calls++; throw new InvalidOperationException("x"); }, ShutdownLight.WriteTimeout));
		Assert.Equal(1, calls); // yeniden deneme yok
		Assert.True(ShutdownLight.WriteTimeout <= TimeSpan.FromSeconds(2));
	}

	[Fact]
	public void Only_system_end_paths_call_it()
	{
		// Windows: yalnız oturum sonu olayı (Shutdown -> SystemShutdown, aksi Logoff).
		string app = Src("Chiaro", "App.xaml.cs");
		Assert.Matches(@"SessionEnding \+= [^{]*\{[^}]*LightOffForSystemEnd\(args\.ReasonSessionEnding == ReasonSessionEnding\.Shutdown \? ExitReason\.SystemShutdown : ExitReason\.Logoff\)", app);
		string win = Src("Chiaro", "MainWindow.xaml.cs");
		Assert.DoesNotContain("LightOffForSystemEnd", Method(win, "internal void ExitApplication()"));
		Assert.DoesNotContain("LightOffForSystemEnd", Method(Src("Chiaro", "MainWindow.Update.cs"), "private void ExitForUpdate()"));
		Assert.Equal(1, Regex.Matches(app, "LightOffForSystemEnd\\(").Count);
		// Mac: yalnız NSWorkspaceWillPowerOffNotification (ve Linux demo betiği).
		Assert.Contains("NSWorkspaceWillPowerOffNotification", Src("ChiaroMac", "Mac", "PowerOff.cs"));
		Assert.Contains("PowerOffObserver.Register(() => LightOffForSystemEnd(ExitReason.SystemShutdown))", Src("ChiaroMac", "MainWindow.Mac.cs"));
		string mac = Src("ChiaroMac", "MainWindow.axaml.cs");
		Assert.DoesNotContain("LightOffForSystemEnd", Method(mac, "internal void ExitApplication()"));
		Assert.DoesNotContain("LightOffForSystemEnd", Method(mac, "internal void PrepareForExit()"));
		Assert.DoesNotContain("LightOffForSystemEnd", Src("ChiaroMac", "App.axaml.cs"));
		Assert.DoesNotContain("LightOffForSystemEnd", Src("ChiaroMac", "Mac", "SelfUpdate.cs"));
	}

	[Fact]
	public void Running_mode_is_stopped_first_but_last_mode_is_kept()
	{
		foreach (string[] f in new[] { new[] { "ChiaroMac", "MainWindow.Mac.cs" }, new[] { "Chiaro", "MainWindow.xaml.cs" } })
		{
			string m = Method(Src(f), "internal string LightOffForSystemEnd(ExitReason reason)");
			int keep = m.IndexOf("keepLastModeOnStop = true;", StringComparison.Ordinal);
			int cancel = m.IndexOf("modeCancellation.Cancel();", StringComparison.Ordinal);
			int write = m.IndexOf("Protocol.Power(on: false)", StringComparison.Ordinal);
			Assert.True(keep > 0 && cancel > keep && write > cancel, string.Join(",", f));
			Assert.Contains("ShutdownLight.WriteTimeout", m);
		}
	}
}
