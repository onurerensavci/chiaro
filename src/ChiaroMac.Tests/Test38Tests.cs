using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>3.8: ikinci açılış iletisiz; çalışan kopya penceresini öne getirir.</summary>
[Collection("Loc")]
public class Test38Tests
{
	private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

	private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

	// Unix soket yolu ~104 bayt sınırlı: kısa geçici klasör.
	private static string Dir() => Directory.CreateDirectory(Path.Combine("/tmp", "c38-" + Guid.NewGuid().ToString("N")[..8])).FullName;

	[Fact]
	public void Second_launch_asks_first_to_come_to_front()
	{
		string dir = Dir();
		try
		{
			using SingleInstance? first = SingleInstance.TryAcquire(dir);
			Assert.NotNull(first);
			Assert.Null(SingleInstance.TryAcquire(dir)); // tek kopya korunur
			int shown = 0;
			using ShowListener? l = SingleInstance.Listen(() => { Interlocked.Increment(ref shown); return Task.CompletedTask; }, dir);
			Assert.NotNull(l);
			Assert.True(SingleInstance.SignalFirst(dir));
			Assert.True(SingleInstance.SignalFirst(dir)); // tekrar tekrar çalışır
			Assert.Equal(2, shown);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void No_listener_or_stale_socket_returns_false_quickly()
	{
		string dir = Dir();
		try
		{
			Assert.False(SingleInstance.SignalFirst(dir));
			File.WriteAllText(SingleInstance.SocketPath(dir), ""); // çökmüş kopyadan kalan dosya
			var sw = System.Diagnostics.Stopwatch.StartNew();
			Assert.False(SingleInstance.SignalFirst(dir));
			Assert.True(sw.ElapsedMilliseconds < 2500);
			// Yeni ilk kopya eski dosyanın üstüne dinleyebilir.
			using ShowListener? l = SingleInstance.Listen(() => Task.CompletedTask, dir);
			Assert.NotNull(l);
			Assert.True(SingleInstance.SignalFirst(dir));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void Stuck_first_instance_still_lets_second_exit_quietly()
	{
		string dir = Dir();
		try
		{
			TaskCompletionSource never = new TaskCompletionSource();
			using ShowListener? l = SingleInstance.Listen(() => never.Task, dir);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			Assert.False(SingleInstance.SignalFirst(dir, 3000));
			Assert.True(sw.ElapsedMilliseconds < 3500, sw.ElapsedMilliseconds.ToString());
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void Listener_disposal_removes_socket()
	{
		string dir = Dir();
		try
		{
			ShowListener? l = SingleInstance.Listen(() => Task.CompletedTask, dir);
			Assert.True(File.Exists(SingleInstance.SocketPath(dir)));
			l!.Dispose();
			Assert.False(File.Exists(SingleInstance.SocketPath(dir)));
			Assert.False(SingleInstance.SignalFirst(dir));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void Old_dialog_is_gone_on_both_platforms()
	{
		Assert.False(Strings.Tr.ContainsKey("already_running"));
		Assert.False(Strings.En.ContainsKey("already_running"));
		Assert.DoesNotContain(Strings.Tr.Values, v => v.Contains("simgeden çıkıp yeniden aç"));
		Assert.DoesNotContain(Strings.En.Values, v => v.Contains("Exit from the tray icon and open it again"));
		string win = Src("Chiaro", "App.xaml.cs");
		Assert.DoesNotContain("already_running", win.Replace("ContainsKey(\"already_running\")", ""));
		Assert.Contains("InstanceSignal.SignalFirst()", win);
		Assert.Contains("showSignal = InstanceSignal.Listen(", win);
		Assert.Contains("BringToFrontFromSecondLaunch", Src("Chiaro", "MainWindow.xaml.cs"));
		string program = Src("ChiaroMac", "Program.cs");
		int at = program.IndexOf("if (single == null)", StringComparison.Ordinal);
		string block = program.Substring(at, program.IndexOf("return 0;", at, StringComparison.Ordinal) - at);
		Assert.Contains("SingleInstance.SignalFirst()", block);
		Assert.DoesNotContain("Dialog", block);
		Assert.Contains("SingleInstance.Listen(", Src("ChiaroMac", "App.axaml.cs"));
		Assert.Contains("BringToFrontFromSecondLaunch", Src("ChiaroMac", "MainWindow.axaml.cs"));
	}
}
