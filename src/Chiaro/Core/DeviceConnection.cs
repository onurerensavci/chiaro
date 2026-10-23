#nullable enable annotations
#nullable disable warnings
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

public static class DeviceConnection
{
	public static async Task<T> OpenByAddressAsync<T>(ulong address, Func<ulong, CancellationToken, Task<T?>> openCached, Func<ulong, CancellationToken, Task<T?>>? openTyped, Action<string> log, CancellationToken ct) where T : class
	{
		if (address == 0L || address > 281474976710655L)
		{
			throw new InvalidOperationException("Adres alınamadı. Yeniden tara.");
		}
		ct.ThrowIfCancellationRequested();
		try
		{
			log($"Açılış: FromBluetoothAddressAsync({address:X12}); Windows önbelleği");
			T val = await openCached(address, ct);
			if (val != null)
			{
				return val;
			}
			log("Adresle açılış: Windows cihaz döndürmedi.");
		}
		catch (Exception ex) when (ex.HResult == -2147024809 && openTyped != null && !ct.IsCancellationRequested)
		{
			log("Adresle açılış parametresi reddedildi: " + ex.Message + "; reklamın adres türü deneniyor.");
		}
		ct.ThrowIfCancellationRequested();
		if (openTyped != null)
		{
			log($"Açılış: aynı adres {address:X12}, reklamdan alınan adres türüyle");
			T val2 = await openTyped(address, ct);
			if (val2 != null)
			{
				return val2;
			}
		}
		throw new InvalidOperationException("Ampul açılamadı. Yeniden tara ve bağlan.");
	}
}
