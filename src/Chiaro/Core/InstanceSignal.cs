#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HueStudio;

/// <summary>
/// İkinci açılış: Chiaro zaten çalışıyorsa (tek kopya kilidi doluysa) yeni kopya, adlandırılmış bir olayla (named event)
/// ilk kopyaya "pencereni göster" der ve sessizce kapanır. İlk kopya olayı bir iş parçacığı havuzu beklemesiyle dinler
/// ve ana pencereyi (mini çubuk açıksa onu) öne getirir. İlk kopya yanıt veremese de yeni kopya ileti göstermeden kapanır.
/// </summary>
internal sealed class InstanceSignal : IDisposable
{
	internal const string ShowEventName = App.InstanceLockName + "-show";

	private const int AsfwAny = -1;

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(int processId);

	private readonly EventWaitHandle handle;
	private readonly RegisteredWaitHandle registration;

	private InstanceSignal(EventWaitHandle h, Action onShow)
	{
		handle = h;
		registration = ThreadPool.RegisterWaitForSingleObject(h, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
	}

	/// <summary>İlk kopya: "göster" isteklerini dinler. Olay oluşturulamazsa null (uygulama yine çalışır).</summary>
	public static InstanceSignal? Listen(Action onShow, string name = ShowEventName)
	{
		try
		{
			return new InstanceSignal(new EventWaitHandle(false, EventResetMode.AutoReset, name), onShow);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>İkinci kopya: ilk kopyaya göster isteği gönderir. İlk kopya dinlemiyorsa (ör. eski sürüm) false.</summary>
	public static bool SignalFirst(string name = ShowEventName)
	{
		try
		{
			// Kullanıcı bu kopyayı başlattığı için öne geçme hakkı bizde: ilk kopyaya devret (SetForegroundWindow çalışsın).
			try { AllowSetForegroundWindow(AsfwAny); } catch (Exception) { }
			if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? h)) return false;
			using (h)
			{
				return h.Set();
			}
		}
		catch (Exception)
		{
			return false;
		}
	}

	public void Dispose()
	{
		registration.Unregister(null);
		handle.Dispose();
	}
}
