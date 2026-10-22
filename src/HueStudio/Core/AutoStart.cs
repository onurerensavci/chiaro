#nullable enable
using System;
using Microsoft.Win32;

namespace HueStudio;

/// <summary>HKCU\...\Run kaydı: Windows açılınca tepside, pencere gizli başlar.</summary>
public static class AutoStart
{
	public const string TrayArgument = "--tray";
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "HueStudio";

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
