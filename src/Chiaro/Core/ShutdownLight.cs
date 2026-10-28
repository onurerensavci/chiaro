#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>Uygulamanın neden kapandığı. Işık yalnızca bilgisayar kapanırken / yeniden başlarken / oturum kapanırken kapatılır.</summary>
public enum ExitReason
{
	/// <summary>Tepsi/menü çubuğundan Çıkış, ⌘Q, pencere kapatma.</summary>
	AppQuit,
	/// <summary>Uygulama içi güncelleme sonrası yeniden başlatma.</summary>
	UpdateRestart,
	/// <summary>Oturum kapatma (Windows: oturumu kapat; macOS: çıkış yap).</summary>
	Logoff,
	/// <summary>Bilgisayarı kapatma ya da yeniden başlatma.</summary>
	SystemShutdown,
}

/// <summary>
/// "Bilgisayar kapanınca ışığı kapat": sistem kapanırken bağlı ampule tek bir kapat komutu gönderir.
/// Kapanma penceresi kısa olduğundan yanıt en çok <see cref="WriteTimeout"/> beklenir, yeniden denenmez.
/// </summary>
public static class ShutdownLight
{
	public static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(1500);

	public static bool IsSystemEnd(ExitReason reason) => reason is ExitReason.Logoff or ExitReason.SystemShutdown;

	/// <summary>Kapat komutu gönderilmeyecekse nedeni; gönderilecekse null. Başlangıç seçeneklerinden bağımsızdır.</summary>
	public static string? SkipReason(bool optionOn, ExitReason reason, bool connected, bool supportsPower)
	{
		if (!optionOn) return "seçenek kapalı";
		if (!IsSystemEnd(reason)) return reason == ExitReason.UpdateRestart ? "güncelleme için yeniden başlatma" : "normal çıkış";
		if (!connected) return "ampul bağlı değil";
		if (!supportsPower) return "ampul güç komutunu desteklemiyor";
		return null;
	}

	/// <summary>
	/// Kapat yazımını arka planda başlatır ve en çok <paramref name="timeout"/> bekler (çağıran iş parçacığını kısa süre bloklar;
	/// kapanma işleyicisinde zaman uyumlu çalışmak için). Sonuç günlük için kısa bir metindir.
	/// </summary>
	public static string SendBlocking(Func<CancellationToken, Task> write, TimeSpan timeout)
	{
		CancellationTokenSource cts = new CancellationTokenSource(timeout);
		Task t;
		try
		{
			t = Task.Run(() => write(cts.Token));
		}
		catch (Exception ex)
		{
			return "gönderilemedi: " + ex.Message;
		}
		try
		{
			if (!t.Wait(timeout)) return $"kapat komutu gönderildi, yanıt beklenmedi ({timeout.TotalMilliseconds:F0} ms)";
			return "ışık kapatıldı";
		}
		catch (AggregateException ex)
		{
			Exception e = ex.InnerException ?? ex;
			return e is OperationCanceledException ? $"kapat komutu gönderildi, yanıt beklenmedi ({timeout.TotalMilliseconds:F0} ms)" : "gönderilemedi: " + e.Message;
		}
		finally
		{
			if (t.IsCompleted) cts.Dispose();
		}
	}
}
