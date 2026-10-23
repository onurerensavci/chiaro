using System.Diagnostics;
using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>Mac test 4: eşlenmiş yazma yanıtları ve kendi kendini güncelleme (Linux'ta sınanabilen kısımlar).</summary>
public class Test4Tests
{
	// ---------------------------------------------------------------- eşlenmiş yanıtlar

	[Fact]
	public void Late_reply_to_timed_out_write_does_not_complete_next_write()
	{
		PendingOps<bool> ops = new PendingOps<bool>();
		Guid c = Guid.NewGuid();
		TaskCompletionSource<bool> first = ops.Add(c);   // zaman aşımına uğrayıp bırakılan yazma
		TaskCompletionSource<bool> second = ops.Add(c);  // sonraki yazma
		// İlk yazmanın geç yanıtı gelir: kuyruğun başı (ilk yazma) tamamlanır, ikinci beklemeye devam eder.
		Assert.True(ops.TakeOldest(c, out TaskCompletionSource<bool> owner));
		owner.TrySetResult(true);
		Assert.Same(first, owner);
		Assert.False(second.Task.IsCompleted);
		// İkinci yazmanın kendi yanıtı.
		Assert.True(ops.TakeOldest(c, out TaskCompletionSource<bool> owner2));
		Assert.Same(second, owner2);
		Assert.Equal(0, ops.Count(c));
		// İstenmemiş yanıt (ör. bildirim) hiçbir şeyi tamamlamaz.
		Assert.False(ops.TakeOldest(c, out _));
	}

	[Fact]
	public void Pending_ops_are_per_characteristic_and_fail_on_disconnect()
	{
		PendingOps<byte[]> ops = new PendingOps<byte[]>();
		Guid a = Guid.NewGuid(), b = Guid.NewGuid();
		var ta = ops.Add(a);
		var tb = ops.Add(b);
		Assert.True(ops.TakeOldest(b, out var got));
		Assert.Same(tb, got);
		ops.FailAll(new InvalidOperationException("koptu"));
		Assert.True(ta.Task.IsFaulted);
		Assert.Equal(0, ops.Count(a));
	}

	// ---------------------------------------------------------------- kendi kendini güncelleme

	private static string Temp(string name)
	{
		string d = Path.Combine(Path.GetTempPath(), "chiaro-update-test-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(d);
		return d;
	}

	private static string FakeBundle(string parent, string name = "Chiaro.app", string id = SelfUpdate.BundleId, string version = "3.5.0", bool exec = true, string marker = "new")
	{
		string app = Path.Combine(parent, name);
		string macos = Path.Combine(app, "Contents", "MacOS");
		Directory.CreateDirectory(macos);
		File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"),
			$"<?xml version=\"1.0\"?><plist><dict><key>CFBundleExecutable</key><string>Chiaro</string><key>CFBundleIdentifier</key><string>{id}</string><key>CFBundleShortVersionString</key><string>{version}</string></dict></plist>");
		string exe = Path.Combine(macos, "Chiaro");
		File.WriteAllText(exe, "#!/bin/sh\n");
		File.SetUnixFileMode(exe, exec ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
		File.WriteAllText(Path.Combine(app, "Contents", "marker.txt"), marker);
		return app;
	}

	private static int Run(string file, string workDir, params string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo(file) { WorkingDirectory = workDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string a in args) psi.ArgumentList.Add(a);
		using Process p = Process.Start(psi)!;
		p.WaitForExit();
		return p.ExitCode;
	}

	[Fact]
	public void Verify_accepts_valid_bundle_and_rejects_wrong_ones()
	{
		string d = Temp("verify");
		try
		{
			SelfUpdate.VerifyBundle(FakeBundle(d, "Good.app"), new Version(3, 5));
			Assert.Throws<InvalidOperationException>(() => SelfUpdate.VerifyBundle(FakeBundle(d, "Other.app", id: "com.example.other"), new Version(3, 5)));
			Assert.Throws<InvalidOperationException>(() => SelfUpdate.VerifyBundle(FakeBundle(d, "NoExec.app", exec: false), new Version(3, 5)));
			Assert.Throws<InvalidOperationException>(() => SelfUpdate.VerifyBundle(FakeBundle(d, "Old.app", version: "3.4.0"), new Version(3, 5)));
			Assert.Throws<InvalidOperationException>(() => SelfUpdate.VerifyBundle(Path.Combine(d, "Missing.app"), new Version(3, 5)));
		}
		finally { Directory.Delete(d, true); }
	}

	[Fact]
	public void Extract_keeps_symlinks_and_exec_bits_and_finds_single_app()
	{
		string d = Temp("zip");
		try
		{
			string src = Path.Combine(d, "src");
			string app = FakeBundle(src);
			File.CreateSymbolicLink(Path.Combine(app, "Contents", "MacOS", "link.dylib"), "Chiaro");
			string zip = Path.Combine(d, "Chiaro_v3.5_mac.zip");
			Assert.Equal(0, Run("zip", src, "-qry", "-X", zip, "Chiaro.app"));
			string outDir = Path.Combine(d, "x");
			SelfUpdate.Extract(zip, outDir);
			string found = SelfUpdate.FindAndVerifyBundle(outDir, new Version(3, 5));
			Assert.Equal(Path.Combine(outDir, "Chiaro.app"), found);
			FileInfo link = new FileInfo(Path.Combine(found, "Contents", "MacOS", "link.dylib"));
			Assert.NotNull(link.LinkTarget);
			Assert.Equal("Chiaro", link.LinkTarget);
			Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(found, "Contents", "MacOS", "Chiaro")) & UnixFileMode.UserExecute);
		}
		finally { Directory.Delete(d, true); }
	}

	[Fact]
	public void Cannot_update_reasons()
	{
		Assert.Equal("mac_upd_self_no_bundle", SelfUpdate.CannotUpdateReason(null));
		Assert.Equal("mac_upd_self_translocated", SelfUpdate.CannotUpdateReason("/private/var/folders/xy/T/AppTranslocation/ABCD/d/Chiaro.app"));
		string d = Temp("ro");
		try
		{
			string app = FakeBundle(d, marker: "old");
			Assert.Null(SelfUpdate.CannotUpdateReason(app));
			File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			if (Environment.UserName != "root") Assert.Equal("mac_upd_self_readonly", SelfUpdate.CannotUpdateReason(app));
		}
		finally
		{
			File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			Directory.Delete(d, true);
		}
	}

	private sealed class HelperFixture : IDisposable
	{
		public readonly string Dir, Work, Old, Log, Opener, Marker, Status, Zip, Fallback;

		public HelperFixture(string name)
		{
			Dir = Temp(name);
			Work = Path.Combine(Dir, "chiaro-update-work");
			Directory.CreateDirectory(Work);
			Old = FakeBundle(Path.Combine(Dir, "Applications"), marker: "old");
			Log = Path.Combine(Dir, "update.log");
			Marker = Path.Combine(Dir, "opened.txt");
			Opener = Path.Combine(Dir, "open.sh");
			Status = Path.Combine(Dir, "update-status");
			Zip = Path.Combine(Work, "Chiaro_v3.5_mac.zip");
			File.WriteAllText(Zip, "zip");
			Directory.CreateDirectory(Path.Combine(Dir, "Downloads"));
			Fallback = Path.Combine(Dir, "Downloads", "Chiaro_v3.5_mac.zip");
			File.WriteAllText(Opener, $"#!/bin/sh\necho \"$1\" > '{Marker}'\n");
			File.SetUnixFileMode(Opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		public int RunHelper(string staged, int pid)
		{
			using Process p = SelfUpdate.StartHelper(Work, pid, Old, staged, Log, Opener, Status, Zip, Fallback);
			Assert.True(p.WaitForExit(30000), "yardımcı bitmedi");
			return p.ExitCode;
		}

		public string OldMarker => File.ReadAllText(Path.Combine(Old, "Contents", "marker.txt"));

		public void Dispose()
		{
			try { Directory.Delete(Dir, true); } catch { }
		}
	}

	[Fact]
	public void Helper_waits_for_pid_swaps_bundle_and_relaunches()
	{
		using HelperFixture f = new HelperFixture("swap");
		string newApp = FakeBundle(Path.Combine(f.Dir, "x"), marker: "new");
		string staged = SelfUpdate.Stage(newApp, f.Old);
		Assert.Equal(f.Old + SelfUpdate.StagingSuffix, staged);
		using Process app = Process.Start(new ProcessStartInfo("sleep", "1.5") { UseShellExecute = false })!;
		Stopwatch sw = Stopwatch.StartNew();
		Assert.Equal(0, f.RunHelper(staged, app.Id));
		Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1), "uygulamanın kapanmasını beklemedi");
		Assert.Equal("new", f.OldMarker);
		Assert.False(Directory.Exists(staged));
		Assert.False(Directory.Exists(f.Old + SelfUpdate.BackupSuffix));
		Assert.Equal(f.Old, File.ReadAllText(f.Marker).Trim());
		Assert.False(Directory.Exists(f.Work), "geçici çalışma klasörü silinmeli");
		Assert.Equal(0, SelfUpdate.TakeStatus(f.Status));
		Assert.Null(SelfUpdate.TakeStatus(f.Status));
		Assert.False(File.Exists(f.Fallback), "başarılı güncellemede zip İndirilenler'e taşınmaz");
	}

	[Fact]
	public void Helper_restores_backup_when_new_bundle_is_broken()
	{
		using HelperFixture f = new HelperFixture("restore");
		string staged = f.Old + SelfUpdate.StagingSuffix;
		Directory.CreateDirectory(Path.Combine(staged, "Contents")); // Contents/MacOS yok: bozuk paket
		Assert.Equal(5, f.RunHelper(staged, 999999));
		Assert.Equal("old", f.OldMarker);
		Assert.Equal(5, SelfUpdate.TakeStatus(f.Status));
		Assert.True(File.Exists(f.Fallback), "başarısızlıkta doğrulanmış zip İndirilenler'e taşınmalı");
		Assert.False(Directory.Exists(staged));
		Assert.False(Directory.Exists(f.Old + SelfUpdate.BackupSuffix));
		Assert.Equal(f.Old, File.ReadAllText(f.Marker).Trim());
	}

	[Fact]
	public void Helper_without_staged_bundle_keeps_old_app_and_relaunches()
	{
		using HelperFixture f = new HelperFixture("missing");
		Assert.Equal(3, f.RunHelper(f.Old + SelfUpdate.StagingSuffix, 999999));
		Assert.Equal("old", f.OldMarker);
		Assert.Equal(f.Old, File.ReadAllText(f.Marker).Trim());
	}

	[Fact]
	public void Cleanup_removes_leftover_staging_and_backup()
	{
		string d = Temp("cleanup");
		try
		{
			string app = FakeBundle(d, marker: "old");
			Directory.CreateDirectory(app + SelfUpdate.StagingSuffix);
			Directory.CreateDirectory(app + SelfUpdate.BackupSuffix);
			SelfUpdate.CleanupLeftovers(app);
			Assert.False(Directory.Exists(app + SelfUpdate.StagingSuffix));
			Assert.False(Directory.Exists(app + SelfUpdate.BackupSuffix));
			Assert.True(Directory.Exists(app));
		}
		finally { Directory.Delete(d, true); }
	}

	[Fact]
	public void Plist_string_reads_values()
	{
		const string xml = "<dict><key>CFBundleIdentifier</key>\n\t<string>io.github.onurerensavci.chiaro</string><key>A</key><string>x &amp; y</string></dict>";
		Assert.Equal(SelfUpdate.BundleId, SelfUpdate.PlistString(xml, "CFBundleIdentifier"));
		Assert.Equal("x & y", SelfUpdate.PlistString(xml, "A"));
		Assert.Null(SelfUpdate.PlistString(xml, "Missing"));
	}
}
