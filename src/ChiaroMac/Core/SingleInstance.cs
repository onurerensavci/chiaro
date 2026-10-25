#nullable enable
using System;

namespace HueStudio;

/// <summary>
/// Tek kopya kilidi: aynı kullanıcıda iki Chiaro (ör. İndirilenler'deki ve Uygulamalar'daki kopya, ya da Terminal'den
/// başlatılan ikinci kopya) aynı ampule bağlanmaya çalışıp birbirini düşürmesin. Kilit, uygulama kapanınca (çökse bile)
/// işletim sistemince bırakılır (flock).
/// İkinci açılış: kilit doluysa yeni kopya, ilk kopyanın dinlediği yerel sokete (chiaro-mac.sock) "show" yazar; ilk kopya
/// penceresini (mini çubuk açıksa onu) öne getirip "ok" der, yeni kopya ileti göstermeden kapanır. İlk kopya yanıt
/// vermezse yeni kopya kısa bir beklemeden sonra yine sessizce kapanır.
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

	public static string SocketPath(string? folder = null) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LockPath(folder))!, "chiaro-mac.sock");

	/// <summary>İlk kopya: "göster" isteklerini dinler (kilit bizdeyken; eski soket dosyası silinir). Açılamazsa null.</summary>
	public static ShowListener? Listen(Func<System.Threading.Tasks.Task> onShow, string? folder = null)
	{
		string path = SocketPath(folder);
		try
		{
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
			System.Net.Sockets.Socket s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
			s.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
			s.Listen(4);
			return new ShowListener(s, path, onShow);
		}
		catch (Exception ex)
		{
			AppLog.Write("İkinci açılış dinleyicisi açılamadı: " + ex.Message);
			return null;
		}
	}

	/// <summary>İkinci kopya: ilk kopyadan penceresini öne getirmesini ister. Yanıt geldiyse true; dinleyen yoksa ya da yanıt vermezse false.</summary>
	public static bool SignalFirst(string? folder = null, int timeoutMs = 2000)
	{
		string path = SocketPath(folder);
		if (!System.IO.File.Exists(path)) return false;
		try
		{
			using System.Net.Sockets.Socket s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
			using System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(timeoutMs);
			s.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), cts.Token).AsTask().GetAwaiter().GetResult();
			s.Send(System.Text.Encoding.ASCII.GetBytes("show\n"));
			byte[] buf = new byte[16];
			int n = s.ReceiveAsync(buf, System.Net.Sockets.SocketFlags.None, cts.Token).AsTask().GetAwaiter().GetResult();
			return System.Text.Encoding.ASCII.GetString(buf, 0, n).StartsWith("ok", StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
		}
	}
}

/// <summary>İlk kopyanın "göster" dinleyicisi (arka plan iş parçacığı).</summary>
internal sealed class ShowListener : IDisposable
{
	private readonly System.Net.Sockets.Socket socket;
	private readonly string path;
	private readonly Func<System.Threading.Tasks.Task> onShow;
	private volatile bool disposed;

	internal ShowListener(System.Net.Sockets.Socket s, string p, Func<System.Threading.Tasks.Task> show)
	{
		socket = s;
		path = p;
		onShow = show;
		new System.Threading.Thread(Loop) { IsBackground = true, Name = "Chiaro ikinci açılış" }.Start();
	}

	private void Loop()
	{
		while (!disposed)
		{
			try
			{
				using System.Net.Sockets.Socket c = socket.Accept();
				c.ReceiveTimeout = 2000;
				c.SendTimeout = 2000;
				byte[] buf = new byte[16];
				int n = c.Receive(buf);
				if (!System.Text.Encoding.ASCII.GetString(buf, 0, n).StartsWith("show", StringComparison.Ordinal)) continue;
				// Arayüz takılıysa en çok ~1,5 sn bekle; yanıt yoksa ikinci kopya yine sessizce kapanır.
				bool done = onShow().Wait(1500);
				c.Send(System.Text.Encoding.ASCII.GetBytes(done ? "ok\n" : "busy\n"));
			}
			catch (Exception ex)
			{
				if (disposed) return;
				AppLog.Write("İkinci açılış dinleyicisi: " + ex.Message);
				System.Threading.Thread.Sleep(200);
			}
		}
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		try { socket.Dispose(); } catch (Exception) { }
		try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch (Exception) { }
	}
}
