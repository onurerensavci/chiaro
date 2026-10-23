#nullable enable
using System;

namespace HueStudio;

/// <summary>
/// Tek kopya kilidi: aynı kullanıcıda iki Chiaro (ör. İndirilenler'deki ve Uygulamalar'daki kopya, ya da Terminal'den
/// başlatılan ikinci kopya) aynı ampule bağlanmaya çalışıp birbirini düşürmesin. Kilit, uygulama kapanınca (çökse bile)
/// işletim sistemince bırakılır (flock).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
	private readonly System.IO.FileStream? stream;

	private SingleInstance(System.IO.FileStream? s)
	{
		stream = s;
	}

	public static string LockPath(string? folder = null) =>
		System.IO.Path.Combine(folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "HueBluetooth"), "chiaro-mac.lock");

	/// <summary>Kilidi alır; başka bir kopya tutuyorsa null. Kilit dosyası açılamıyorsa (izin vb.) uygulamayı engellemez.</summary>
	public static SingleInstance? TryAcquire(string? folder = null)
	{
		string path = LockPath(folder);
		try
		{
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
			return new SingleInstance(fs);
		}
		catch (System.IO.IOException)
		{
			return null;
		}
		catch (Exception ex)
		{
			AppLog.Write("Tek kopya kilidi alınamadı (yok sayıldı): " + ex.Message);
			return new SingleInstance(null);
		}
	}

	public void Dispose() => stream?.Dispose();
}
