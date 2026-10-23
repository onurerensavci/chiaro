#nullable enable
using System;
using Microsoft.Win32;

namespace HueStudio;

/// <summary>HKCU\...\Run kaydı: Windows açılınca tepside, pencere gizli başlar.</summary>
public static class AutoStart
{
	public const string TrayArgument = "--tray";
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "Chiaro";
	/// <summary>Eski değer adları: eski sürümler ve 3.3 deneme adı.</summary>
	internal static readonly string[] LegacyValueNames = { "HueStudio", "Sati" };
	internal const string LegacyValueName = "HueStudio";

	public static bool IsTrayLaunch(string[] args)
	{
		foreach (string a in args)
		{
			if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static string Command
	{
		get
		{
			string exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
			return $"\"{exe}\" {TrayArgument}";
		}
	}

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
			return key?.GetValue(ValueName) is string s && s.Length > 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Eski "HueStudio" / "Sati" değerleri varsa siler; başlangıç açıktıysa aynı --tray bağımsız değişkeniyle "Chiaro" yazar.
	/// Taşıma yapıldıysa true döner.
	/// </summary>
	public static bool MigrateLegacy(string runKey = RunKey)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
			bool enabled = false, found = false;
			foreach (string name in LegacyValueNames)
			{
				if (key.GetValue(name) is not string old) continue;
				found = true;
				enabled |= old.Trim().Length > 0;
				key.DeleteValue(name, throwOnMissingValue: false);
			}
			if (!found) return false;
			if (enabled)
			{
				string exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
				key.SetValue(ValueName, $"\"{exe}\" {TrayArgument}", RegistryValueKind.String);
			}
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Duman testi: geçici bir anahtarda taşıma.</summary>
	internal static string MigrationCheck()
	{
		const string test = @"Software\ChiaroSmokeTest\Run";
		try
		{
			using (RegistryKey k = Registry.CurrentUser.CreateSubKey(test, writable: true))
			{
				k.SetValue(LegacyValueName, "\"C:\\Apps\\HueStudio_v3.2.exe\" --tray");
				k.SetValue("Sati", "\"C:\\Apps\\Sati.exe\" --tray");
			}
			bool moved = MigrateLegacy(test);
			using RegistryKey r = Registry.CurrentUser.OpenSubKey(test)!;
			string? now = r.GetValue(ValueName) as string;
			bool ok = moved && r.GetValue(LegacyValueName) == null && r.GetValue("Sati") == null && now != null && now.EndsWith(" " + TrayArgument) && !MigrateLegacy(test);
			if (!ok) throw new InvalidOperationException($"Run key migration failed: moved={moved} value={now}");
			return $"\nStartup migration: Run values HueStudio + Sati removed, Chiaro written with {TrayArgument}, second run no-op: PASS.";
		}
		finally
		{
			Registry.CurrentUser.DeleteSubKeyTree(@"Software\ChiaroSmokeTest", throwOnMissingSubKey: false);
		}
	}

	/// <summary>Kaydı ayara göre ekler/siler. Exe taşındıysa yolu günceller.</summary>
	public static bool Apply(bool enabled)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
			if (enabled)
			{
				string cmd = Command;
				if (!string.Equals(key.GetValue(ValueName) as string, cmd, StringComparison.OrdinalIgnoreCase))
				{
					key.SetValue(ValueName, cmd, RegistryValueKind.String);
				}
			}
			else if (key.GetValue(ValueName) != null)
			{
				key.DeleteValue(ValueName, throwOnMissingValue: false);
			}
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
