#nullable enable
using System;
using System.IO;
using System.Text;

namespace HueStudio;

/// <summary>
/// Hata ayıklama kaydı: macOS'ta ~/Library/Logs/Chiaro/chiaro.log (Linux'ta ~/.cache/Chiaro/chiaro.log).
/// Bluetooth adımları, hatalar ve sürüm bilgisi yazılır. Kullanıcı adı / ev klasörü yolu "~" ile gizlenir.
/// 1 MB'ı aşınca chiaro.log.1'e döner.
/// </summary>
public static class AppLog
{
	private static readonly object Gate = new object();
	private static string? path;

	public static event Action<string>? Written;

	public static string Folder
	{
		get
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string? overrideDir = Environment.GetEnvironmentVariable("CHIARO_LOG_DIR");
			if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;
			return OperatingSystem.IsMacOS()
				? Path.Combine(home, "Library", "Logs", "Chiaro")
				: Path.Combine(home, ".cache", "Chiaro");
		}
	}

	public static string FilePath => path ??= Path.Combine(Folder, "chiaro.log");

	public static string Scrub(string text)
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrEmpty(home) && home.Length > 1) text = text.Replace(home, "~");
		string user = Environment.UserName;
		if (!string.IsNullOrEmpty(user) && user.Length > 2) text = text.Replace("/" + user + "/", "/~/");
		return text;
	}

	public static void Write(string message)
	{
		string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Scrub(message)}";
		lock (Gate)
		{
			try
			{
				Directory.CreateDirectory(Folder);
				FileInfo fi = new FileInfo(FilePath);
				if (fi.Exists && fi.Length > 1_000_000)
				{
					File.Move(FilePath, FilePath + ".1", overwrite: true);
				}
				File.AppendAllText(FilePath, line + "\n", Encoding.UTF8);
			}
			catch
			{
			}
		}
		try { Written?.Invoke(line); } catch { }
	}
}
