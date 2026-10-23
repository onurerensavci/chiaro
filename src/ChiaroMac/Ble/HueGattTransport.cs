#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>
/// Hue Bluetooth mantığı (Windows BleController'ın platformdan bağımsız karşılığı):
/// servis 932c32bd-0000-…, kontroller 0002 güç / 0003 parlaklık / 0004 beyaz tonu (mired) / 0005 renk (xy),
/// hepsi yanıtlı yazma (write with response). Kodlama Windows'la ortak Protocol.cs'den gelir.
/// </summary>
public sealed class HueGattTransport : IBulbTransport
{
	public static readonly Guid HueService = Protocol.Characteristic(0);
	public static readonly Guid DeviceInfoService = new Guid("0000180a-0000-1000-8000-00805f9b34fb");

	private readonly IGattBackend backend;
	private readonly Action<string> log;
	private IGattPeripheral? device;
	private readonly Dictionary<Guid, GattChar> characteristics = new Dictionary<Guid, GattChar>();
	private readonly HashSet<int> rejected = new HashSet<int>();
	private readonly SemaphoreSlim io = new SemaphoreSlim(1, 1);

	public bool Ready { get; private set; }

	public string Metadata { get; private set; } = "Model / yazılım bilgisi alınamadı";

	public string BackendName => backend.Name;

	public event Action<bool>? ConnectionChanged;

	public HueGattTransport(IGattBackend backend, Action<string> logger)
	{
		this.backend = backend;
		log = logger;
	}

	public bool Supports(int code)
	{
		return Ready && !rejected.Contains(code) && characteristics.TryGetValue(Protocol.Characteristic(code), out GattChar? c)
			&& c.Props.HasFlag(CharProps.Write);
	}

	public async Task<string> AdapterStatus(CancellationToken ct)
	{
		try
		{
			return await backend.AdapterStatusAsync(ct);
		}
		catch (GattException ex)
		{
			throw new InvalidOperationException(AdapterMessage(ex));
		}
	}

	private static string AdapterMessage(GattException ex) => ex.Failure switch
	{
		GattFailure.Unauthorized => "Bluetooth izni verilmedi. Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth'tan Chiaro'ya izin ver.",
		GattFailure.PoweredOff => "Bluetooth kapalı. Denetim Merkezi'nden aç.",
		GattFailure.Unsupported => "Bu Mac Bluetooth LE desteklemiyor.",
		_ => ex.Message,
	};

	/// <summary>Windows'taki gibi: FE0F servisi/servis verisi, Hue servis UUID'si ya da adında "Hue" geçen cihazlar.</summary>
	public static bool IsHueCandidate(Advert a)
	{
		bool flag = a.ServiceUuids.Contains(Protocol.AdvertisedService) || a.ServiceUuids.Contains(HueService)
			|| a.ServiceDataUuids.Contains(Protocol.AdvertisedService);
		bool named = a.Name != null && a.Name.Contains("Hue", StringComparison.OrdinalIgnoreCase);
		return flag || named || a.Known;
	}

	public async Task ScanAsync(Action<Lamp> found, CancellationToken ct)
	{
		log(await AdapterStatus(ct));
		HashSet<string> seen = new HashSet<string>();
		await backend.ScanAsync(a =>
		{
			try
			{
				if (!IsHueCandidate(a)) return;
				string name = string.IsNullOrWhiteSpace(a.Name) ? "Hue adayı" : a.Name!;
				lock (seen)
				{
					if (seen.Add(a.Id)) log($"Bulundu: {name} (…{Tail(a.Id)}) RSSI={a.Rssi?.ToString() ?? "—"} sistemde bağlı={a.Known}");
				}
				found(new Lamp(a.Id, name, a.Known, a.Rssi));
			}
			catch (Exception ex)
			{
				log("Reklam okunamadı: " + ex.Message);
			}
		}, TimeSpan.FromSeconds(10), ct);
	}

	internal static string Tail(string id) => id.Length > 4 ? id[^4..] : id;

	public async Task ConnectAsync(Lamp lamp, Action<string> progress, CancellationToken ct)
	{
		Disconnect();
		string stage = "Bluetooth adaptörü kontrolü";
		try
		{
			log($"Seçim: {lamp.Display} (…{Tail(lamp.Id)}); arka uç={backend.Name}");
			Stage("Bluetooth adaptörü kontrolü");
			await AdapterStatus(ct);
			Stage("Ampule bağlanma");
			device = await backend.ConnectAsync(lamp.Id, log, ct);
			device.Disconnected += OnDisconnected;
			Stage("Hue servislerini okuma");
			IReadOnlyList<Guid> services = await device.DiscoverServicesAsync(new[] { HueService, DeviceInfoService }, ct);
			log("Servisler: " + string.Join(", ", services));
			if (!services.Contains(HueService))
			{
				throw new InvalidOperationException("Bu cihaz Hue Bluetooth kontrolünü desteklemiyor.");
			}
			Stage("Hue kontrollerini okuma");
			foreach (GattChar c in await device.DiscoverCharacteristicsAsync(HueService, ct))
			{
				characteristics[c.Uuid] = c;
				log($"GATT {c.Uuid}: {c.Props}");
			}
			if (!characteristics.TryGetValue(Protocol.Characteristic(2), out GattChar? power) || !power.Props.HasFlag(CharProps.Write))
			{
				throw new InvalidOperationException("Ampul aç/kapat komutunu desteklemiyor.");
			}
			if (!power.Props.HasFlag(CharProps.Read))
			{
				throw new InvalidOperationException("Güç durumu okunamadı.");
			}
			// İlk şifreli okuma: macOS gerekirse burada eşleştirme ister.
			Stage("Ampulün güç durumunu okuma — macOS eşleştirme sorarsa onayla");
			byte[]? state = await ReadCore(2, ct);
			if (state == null || state.Length != 1 || state[0] > 1)
			{
				throw new InvalidOperationException("Ampul yazılımı desteklenmiyor.");
			}
			Stage("Model ve yazılım bilgisini okuma");
			if (services.Contains(DeviceInfoService)) await ReadMetadata(ct);
			Ready = true;
			log("GATT okuma erişimi doğrulandı; yazma erişimi ilk komutta sınanacak.");
		}
		catch (OperationCanceledException)
		{
			log($"İptal / zaman aşımı; aşama: {stage}");
			Disconnect();
			throw;
		}
		catch (Exception ex)
		{
			log($"Başarısız aşama: {stage}; {ex.GetType().Name}: {ex.Message}");
			Disconnect();
			string text = ex switch
			{
				InvalidOperationException => ex.Message,
				GattException g => Describe(g),
				UnauthorizedAccessException => "Bluetooth erişimi reddedildi.",
				_ => "Bağlanılamadı (" + ex.GetType().Name + ").",
			};
			throw new InvalidOperationException(StageLabel(stage) + ": " + text, ex);
		}

		void Stage(string text)
		{
			stage = text;
			log("Bağlantı aşaması: " + text);
			progress(StageLabel(text) + "…");
		}
	}

	private static string StageLabel(string stage)
	{
		int i = stage.IndexOf(" — ", StringComparison.Ordinal);
		return i > 0 ? stage[..i] : stage;
	}

	private void OnDisconnected(string? reason)
	{
		log("Bağlantı koptu" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
		bool was = Ready;
		Ready = false;
		if (was) ConnectionChanged?.Invoke(false);
	}

	private async Task ReadMetadata(CancellationToken ct)
	{
		try
		{
			IReadOnlyList<GattChar> chars = await device!.DiscoverCharacteristicsAsync(DeviceInfoService, ct);
			async Task<string> Text(string id)
			{
				Guid u = BleUuid.Parse(id);
				if (!chars.Any(c => c.Uuid == u && c.Props.HasFlag(CharProps.Read))) return "bilinmiyor";
				try
				{
					byte[] b = await device!.ReadAsync(DeviceInfoService, u, ct);
					return Encoding.UTF8.GetString(b).TrimEnd('\0');
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					return "okunamadı";
				}
			}
			Metadata = "Model: " + await Text("2A24") + "  •  Yazılım: " + await Text("2A28");
			log(Metadata);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			log("Model bilgisi: " + ex.Message);
		}
	}

	public async Task<byte[]?> Read(int code, CancellationToken ct)
	{
		try
		{
			return await ReadCore(code, ct);
		}
		catch (GattException g)
		{
			throw new InvalidOperationException("Durum okuma: " + Describe(g), g);
		}
	}

	private async Task<byte[]?> ReadCore(int code, CancellationToken ct)
	{
		if (device == null || !characteristics.TryGetValue(Protocol.Characteristic(code), out GattChar? c) || !c.Props.HasFlag(CharProps.Read))
		{
			return null;
		}
		await io.WaitAsync(ct);
		try
		{
			byte[] v = await device.ReadAsync(HueService, c.Uuid, ct);
			log($"Okuma {code:0000}: {Convert.ToHexString(v)}");
			return v;
		}
		finally
		{
			io.Release();
		}
	}

	public async Task<byte[]?> ReadOptional(int code, CancellationToken ct)
	{
		try
		{
			return await Read(code, ct);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			log($"İsteğe bağlı {code:0000} okuma: {ex.Message}");
			return null;
		}
	}

	public async Task Write(int code, byte[] data, CancellationToken ct)
	{
		if (!Supports(code) || device == null)
		{
			throw new InvalidOperationException("Bu ampulde bu kontrol yok.");
		}
		await io.WaitAsync(ct);
		try
		{
			await device.WriteAsync(HueService, Protocol.Characteristic(code), data, ct);
			log($"Yazma {code:0000}: {Convert.ToHexString(data)} → Success");
		}
		catch (GattException g)
		{
			log($"Yazma {code:0000}: {Convert.ToHexString(data)} → {g.Failure}, ATT={g.AttCode?.ToString("X2") ?? "—"} ({g.Message})");
			if (code != 2 && g.Failure == GattFailure.Att && g.AttCode is 3 or 6)
			{
				rejected.Add(code);
			}
			throw new InvalidOperationException("Komut: " + Describe(g), g);
		}
		finally
		{
			io.Release();
		}
	}

	/// <summary>Windows'taki Check() eşlemesi + CoreBluetooth'a özgü durumlar.</summary>
	public static string Describe(GattException g)
	{
		string reason = g.Failure switch
		{
			GattFailure.Att => g.AttCode switch
			{
				5 or 8 or 15 => "Eşleştirme yetersiz. Ampulü Hue uygulamasında görünür yap, sonra yeniden bağlan.",
				3 or 6 => "Bu model desteklemiyor.",
				13 or 19 => "Ampul bu değeri kabul etmedi.",
				_ => "Ampul reddetti. Yeniden bağlan.",
			},
			GattFailure.PeerRemovedPairing => "Ampul eski eşleştirmeyi unutmuş. Sistem Ayarları → Bluetooth'ta ampulü unut, sonra yeniden bağlan.",
			GattFailure.EncryptionTimedOut => "Şifreleme zaman aşımı. Ampulü Hue uygulamasında görünür yap, sonra yeniden bağlan.",
			GattFailure.NotConnected => "Ampule ulaşılamıyor. Açık ve yakında mı?",
			GattFailure.ConnectionFailed => "Ampule ulaşılamıyor. Açık ve yakında mı?",
			GattFailure.UnknownDevice => "Ampul bu Mac'te bulunamadı. Yeniden tara.",
			GattFailure.Unauthorized => "Bluetooth izni verilmedi. Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth'tan Chiaro'ya izin ver.",
			GattFailure.PoweredOff => "Bluetooth kapalı. Denetim Merkezi'nden aç.",
			GattFailure.Unsupported => "Bu model desteklemiyor.",
			_ => "Ampul reddetti. Yeniden bağlan.",
		};
		string detail = g.AttCode.HasValue ? $"ATT {g.AttCode.Value:X2}" : g.Domain != null ? $"{g.Domain} {g.Code}" : g.Failure.ToString();
		return $"{reason} ({detail})";
	}

	public void Disconnect()
	{
		bool was = Ready;
		Ready = false;
		characteristics.Clear();
		rejected.Clear();
		if (device != null)
		{
			device.Disconnected -= OnDisconnected;
			try { device.Dispose(); } catch (Exception ex) { log("Bağlantı kapatma: " + ex.Message); }
			device = null;
		}
		Metadata = "Model / yazılım bilgisi alınamadı";
		if (was) log("Bağlantı kapatıldı.");
	}

	public void Dispose()
	{
		Disconnect();
		backend.Dispose();
	}
}
