#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HueStudio.MacInterop;
using static HueStudio.MacInterop.Objc;

namespace HueStudio;

/// <summary>
/// macOS CoreBluetooth arka ucu. CBCentralManager + kendi kaydettiğimiz Objective-C temsilci sınıfı
/// (ChiaroBleDelegate: CBCentralManagerDelegate + CBPeripheralDelegate) ile çalışır.
/// Tüm CoreBluetooth çağrıları tek bir seri GCD kuyruğunda yapılır; geri çağrılar da aynı kuyrukta gelir.
/// </summary>
public sealed class CoreBluetoothBackend : IGattBackend
{
	private const nint StateUnknown = 0, StateResetting = 1, StateUnsupported = 2, StateUnauthorized = 3, StatePoweredOff = 4, StatePoweredOn = 5;
	private const nint PeripheralConnected = 2;

	private static CoreBluetoothBackend? instance;
	private static IntPtr delegateClass;

	private readonly Action<string> log;
	private DispatchQueue? queue;
	private IntPtr central;
	private IntPtr delegateObject;
	private IntPtr coreBluetooth;
	private IntPtr keyLocalName, keyServiceUuids, keyServiceData, keyAllowDuplicates;
	private nint state = StateUnknown;
	private TaskCompletionSource stateKnown = NewTcs();
	private Action<Advert>? scanSink;
	private readonly Dictionary<string, IntPtr> peripherals = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<IntPtr, MacPeripheral> active = new Dictionary<IntPtr, MacPeripheral>();
	/// <summary>
	/// cancelPeripheralConnection: çağrılmış, didDisconnectPeripheral yankısı henüz gelmemiş çevre birimleri. Yankı gelmeden aynı
	/// ampule yeniden bağlanılırsa, eski bağlantının kapanma bildirimi yeni bağlantıyı düşürmesin diye beklenir/yutulur.
	/// </summary>
	private readonly Dictionary<IntPtr, TaskCompletionSource> cancelling = new Dictionary<IntPtr, TaskCompletionSource>();

	public string Name => "CoreBluetooth";

	public CoreBluetoothBackend(Action<string> logger)
	{
		log = logger;
	}

	private static TaskCompletionSource NewTcs() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	internal DispatchQueue Queue => queue!;

	// ------------------------------------------------------------------ kurulum

	private void EnsureCentral()
	{
		if (central != IntPtr.Zero) return;
		LoadFrameworks();
		coreBluetooth = NativeLibrary.Load("/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth");
		queue = new DispatchQueue("io.github.onurerensavci.chiaro.ble");
		instance = this;
		queue.Run(() =>
		{
			keyLocalName = ExportedString(coreBluetooth, "CBAdvertisementDataLocalNameKey", "kCBAdvDataLocalName");
			keyServiceUuids = ExportedString(coreBluetooth, "CBAdvertisementDataServiceUUIDsKey", "kCBAdvDataServiceUUIDs");
			keyServiceData = ExportedString(coreBluetooth, "CBAdvertisementDataServiceDataKey", "kCBAdvDataServiceData");
			keyAllowDuplicates = ExportedString(coreBluetooth, "CBCentralManagerScanOptionAllowDuplicatesKey", "kCBScanOptionAllowDuplicates");
			IntPtr cls = DelegateClass();
			delegateObject = Send(Send(cls, Sel("alloc")), Sel("init"));
			log("CoreBluetooth: CBCentralManager oluşturuluyor (ilk seferde macOS Bluetooth izni sorar).");
			central = Send(Send(Cls("CBCentralManager"), Sel("alloc")), Sel("initWithDelegate:queue:options:"), delegateObject, queue!.Handle, IntPtr.Zero);
			if (central == IntPtr.Zero) throw new InvalidOperationException("CBCentralManager oluşturulamadı.");
			try
			{
				nint auth = SendNInt(Cls("CBManager"), Sel("authorization"));
				log($"CoreBluetooth: yetki durumu={AuthName(auth)} ({auth})");
			}
			catch (Exception ex)
			{
				log("CoreBluetooth: yetki okunamadı: " + ex.Message);
			}
		});
	}

	private static string AuthName(nint a) => a switch { 0 => "belirlenmedi", 1 => "kısıtlı", 2 => "reddedildi", 3 => "izin verildi", _ => "?" };

	private static string StateName(nint s) => s switch
	{
		StateUnknown => "Unknown", StateResetting => "Resetting", StateUnsupported => "Unsupported",
		StateUnauthorized => "Unauthorized", StatePoweredOff => "PoweredOff", StatePoweredOn => "PoweredOn", _ => s.ToString(),
	};

	internal static unsafe IntPtr DelegateClass()
	{
		if (delegateClass != IntPtr.Zero) return delegateClass;
		IntPtr existing = objc_getClass("ChiaroBleDelegate");
		if (existing != IntPtr.Zero) return delegateClass = existing;
		IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "ChiaroBleDelegate", 0);
		if (cls == IntPtr.Zero) throw new InvalidOperationException("Temsilci sınıfı oluşturulamadı.");
		Add(cls, "centralManagerDidUpdateState:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&DidUpdateState, "v@:@");
		Add(cls, "centralManager:didDiscoverPeripheral:advertisementData:RSSI:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscover, "v@:@@@@");
		Add(cls, "centralManager:didConnectPeripheral:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidConnect, "v@:@@");
		Add(cls, "centralManager:didFailToConnectPeripheral:error:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFailToConnect, "v@:@@@");
		Add(cls, "centralManager:didDisconnectPeripheral:error:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDisconnect, "v@:@@@");
		Add(cls, "peripheral:didDiscoverServices:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscoverServices, "v@:@@");
		Add(cls, "peripheral:didDiscoverCharacteristicsForService:error:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscoverCharacteristics, "v@:@@@");
		Add(cls, "peripheral:didUpdateValueForCharacteristic:error:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidUpdateValue, "v@:@@@");
		Add(cls, "peripheral:didWriteValueForCharacteristic:error:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidWriteValue, "v@:@@@");
		foreach (string p in new[] { "CBCentralManagerDelegate", "CBPeripheralDelegate" })
		{
			IntPtr proto = objc_getProtocol(p);
			if (proto != IntPtr.Zero) class_addProtocol(cls, proto);
		}
		objc_registerClassPair(cls);
		return delegateClass = cls;

		static void Add(IntPtr c, string sel, IntPtr imp, string types)
		{
			if (!class_addMethod(c, Sel(sel), imp, types)) throw new InvalidOperationException("Yöntem eklenemedi: " + sel);
		}
	}

	// ------------------------------------------------------------------ IGattBackend

	public async Task<string> AdapterStatusAsync(CancellationToken ct)
	{
		EnsureCentral();
		if (state is StateUnknown or StateResetting)
		{
			// İlk açılışta kullanıcı izin penceresini yanıtlayana kadar bekleriz.
			Task wait = stateKnown.Task;
			Task done = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(30), ct));
			ct.ThrowIfCancellationRequested();
			if (done != wait)
			{
				throw new GattException(GattFailure.Other, "Bluetooth durumu alınamadı. macOS izin penceresini yanıtla ya da Bluetooth'u kapatıp aç.");
			}
		}
		return state switch
		{
			StatePoweredOn => "Bluetooth LE adaptörü hazır",
			StatePoweredOff => throw new GattException(GattFailure.PoweredOff, "Bluetooth kapalı."),
			StateUnauthorized => throw new GattException(GattFailure.Unauthorized, "Bluetooth izni yok."),
			StateUnsupported => throw new GattException(GattFailure.Unsupported, "Bluetooth LE desteklenmiyor."),
			_ => throw new GattException(GattFailure.Other, "Bluetooth hazır değil (" + StateName(state) + ")."),
		};
	}

	public async Task ScanAsync(Action<Advert> found, TimeSpan duration, CancellationToken ct)
	{
		await AdapterStatusAsync(ct);
		// Sistemin zaten bağlı tuttuğu Hue ampulleri (ör. başka bir uygulama bağladıysa) reklam yayınlamayabilir.
		List<Advert> known = Queue.Run(() =>
		{
			List<Advert> list = new List<Advert>();
			IntPtr services = NSArray(new[] { CBUUID(HueGattTransport.HueService), CBUUID(Protocol.AdvertisedService) });
			foreach (IntPtr p in Items(Send(central, Sel("retrieveConnectedPeripheralsWithServices:"), services)))
			{
				string id = Remember(p);
				list.Add(new Advert(id, ToStr(Send(p, Sel("name"))), Array.Empty<Guid>(), Array.Empty<Guid>(), null, Known: true));
			}
			return list;
		});
		log($"CoreBluetooth: sistemde bağlı Hue sayısı={known.Count}");
		foreach (Advert a in known) found(a);

		scanSink = found;
		Queue.Run(() =>
		{
			IntPtr options = Dictionary(Bool(true), keyAllowDuplicates);
			SendVoid(central, Sel("scanForPeripheralsWithServices:options:"), IntPtr.Zero, options);
		});
		log("CoreBluetooth: tarama başladı");
		try
		{
			await Task.Delay(duration, ct);
		}
		finally
		{
			scanSink = null;
			try { Queue.Run(() => SendVoid(central, Sel("stopScan"))); } catch (Exception ex) { log("stopScan: " + ex.Message); }
			log("CoreBluetooth: tarama bitti");
		}
	}

	public async Task<IGattPeripheral> ConnectAsync(string id, Action<string> connectLog, CancellationToken ct)
	{
		await AdapterStatusAsync(ct);
		IntPtr p = Queue.Run(() => Lookup(id));
		if (p == IntPtr.Zero)
		{
			connectLog("CoreBluetooth: kayıtlı kimlik bulunamadı; kısa tarama yapılıyor");
			using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task scan = ScanAsync(a => { if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) stop.Cancel(); }, TimeSpan.FromSeconds(8), stop.Token);
			try { await scan; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
			p = Queue.Run(() => Lookup(id));
			if (p == IntPtr.Zero) throw new GattException(GattFailure.UnknownDevice, "Çevre birimi bulunamadı: …" + HueGattTransport.Tail(id));
		}
		// Aynı ampulün önceki bağlantısı az önce kapatıldıysa (Yenile / yeniden bağlan) kapanma yankısını bekle.
		Task? closing = Queue.Run(() => cancelling.TryGetValue(p, out TaskCompletionSource? t) ? t.Task : null);
		if (closing != null)
		{
			Task done = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(3), ct));
			ct.ThrowIfCancellationRequested();
			if (done != closing)
			{
				connectLog("CoreBluetooth: önceki bağlantının kapanışı 3 sn içinde bildirilmedi; devam ediliyor");
				Queue.Run(() => cancelling.Remove(p));
			}
		}
		MacPeripheral mp = new MacPeripheral(this, p, id);
		Queue.Run(() =>
		{
			active[p] = mp;
			mp.Name = ToStr(Send(p, Sel("name")));
			SendVoid(p, Sel("setDelegate:"), delegateObject);
			nint st = SendNInt(p, Sel("state"));
			connectLog($"CoreBluetooth: connectPeripheral (durum={st})");
			SendVoid(central, Sel("connectPeripheral:options:"), p, IntPtr.Zero);
		});
		try
		{
			// CoreBluetooth bağlantı isteği kendiliğinden zaman aşımına uğramaz.
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(TimeSpan.FromSeconds(20));
			try
			{
				await mp.Connected.Task.WaitAsync(limit.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				throw new GattException(GattFailure.NotConnected, "Bağlantı 20 sn içinde kurulamadı.");
			}
			connectLog("CoreBluetooth: bağlandı");
			return mp;
		}
		catch
		{
			mp.Dispose();
			throw;
		}
	}

	/// <summary>Kuyrukta çağrılır.</summary>
	private IntPtr Lookup(string id)
	{
		if (peripherals.TryGetValue(id, out IntPtr p)) return p;
		IntPtr uuid = Send(Send(Cls("NSUUID"), Sel("alloc")), Sel("initWithUUIDString:"), NSString(id));
		if (uuid == IntPtr.Zero) return IntPtr.Zero;
		try
		{
			List<IntPtr> list = Items(Send(central, Sel("retrievePeripheralsWithIdentifiers:"), NSArray(new[] { uuid })));
			if (list.Count == 0) return IntPtr.Zero;
			Remember(list[0]);
			return list[0];
		}
		finally
		{
			Release(uuid);
		}
	}

	/// <summary>Kuyrukta çağrılır. CBPeripheral'ı tutar (retain) ve kimliğini döndürür.</summary>
	private string Remember(IntPtr p)
	{
		string id = ToStr(Send(Send(p, Sel("identifier")), Sel("UUIDString"))) ?? "";
		if (id.Length > 0 && !peripherals.ContainsKey(id))
		{
			peripherals[id] = Retain(p);
		}
		return id;
	}

	internal static IntPtr CBUUID(Guid uuid) => Send(Cls("CBUUID"), Sel("UUIDWithString:"), NSString(BleUuid.ToCoreBluetooth(uuid)));

	internal static Guid FromCBUUID(IntPtr cbuuid) => BleUuid.Parse(ToStr(Send(cbuuid, Sel("UUIDString"))) ?? "0000");

	internal void CancelConnection(IntPtr p, MacPeripheral owner)
	{
		if (central == IntPtr.Zero) return;
		Queue.Run(() =>
		{
			// Yalnızca bu bağlantıya aitse kaldır (aynı ampule yeni bir bağlantı kaydedilmiş olabilir).
			if (active.TryGetValue(p, out MacPeripheral? cur) && ReferenceEquals(cur, owner)) active.Remove(p);
			else if (active.ContainsKey(p)) return;
			// 0 = bağlı değil: kapanma bildirimi gelmez. Diğer durumlarda didDisconnectPeripheral yankısı beklenir.
			if (SendNInt(p, Sel("state")) != 0 && !cancelling.ContainsKey(p)) cancelling[p] = NewTcs();
			SendVoid(central, Sel("cancelPeripheralConnection:"), p);
		});
	}

	/// <summary>Kuyrukta çağrılır. Bu kapanma, bizim iptal ettiğimiz bir bağlantının yankısıysa true.</summary>
	private bool TakeCancelEcho(IntPtr p)
	{
		if (!cancelling.Remove(p, out TaskCompletionSource? t)) return false;
		t.TrySetResult();
		return true;
	}

	public void Dispose()
	{
		// Merkez yönetici uygulama boyunca yaşar; bağlantılar MacPeripheral.Dispose ile kapanır.
		if (queue == null) return;
		List<MacPeripheral> list;
		try { list = queue.Run(() => active.Values.ToList()); } catch { return; }
		foreach (MacPeripheral mp in list) mp.Dispose();
	}

	// ------------------------------------------------------------------ geri çağrılar (GCD kuyruğunda)

	private static void Guard(string name, Action a)
	{
		DispatchQueue.Enter();
		IntPtr pool = objc_autoreleasePoolPush();
		try
		{
			a();
		}
		catch (Exception ex)
		{
			try { instance?.log($"CoreBluetooth geri çağrı hatası ({name}): {ex}"); } catch { }
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
			DispatchQueue.Exit();
		}
	}

	[UnmanagedCallersOnly]
	private static void DidUpdateState(IntPtr self, IntPtr cmd, IntPtr centralManager) => Guard("state", () =>
	{
		CoreBluetoothBackend? b = instance;
		if (b == null) return;
		nint s = SendNInt(centralManager, Sel("state"));
		b.state = s;
		b.log("CoreBluetooth: durum=" + StateName(s));
		if (s is not (StateUnknown or StateResetting)) b.stateKnown.TrySetResult();
		else if (b.stateKnown.Task.IsCompleted) b.stateKnown = NewTcs();
		if (s != StatePoweredOn)
		{
			foreach (MacPeripheral mp in b.active.Values.ToList()) mp.OnDisconnected("Bluetooth " + StateName(s));
			// Bluetooth kapandı/sıfırlandı: bekleyen iptal yankıları artık gelmez.
			foreach (IntPtr p in b.cancelling.Keys.ToList()) b.TakeCancelEcho(p);
		}
	});

	[UnmanagedCallersOnly]
	private static void DidDiscover(IntPtr self, IntPtr cmd, IntPtr centralManager, IntPtr peripheral, IntPtr adv, IntPtr rssi) => Guard("discover", () =>
	{
		CoreBluetoothBackend? b = instance;
		Action<Advert>? sink = b?.scanSink;
		if (b == null || sink == null) return;
		string id = b.Remember(peripheral);
		string? name = ToStr(Get(adv, b.keyLocalName)) ?? ToStr(Send(peripheral, Sel("name")));
		List<Guid> uuids = Items(Get(adv, b.keyServiceUuids)).Select(FromCBUUID).ToList();
		List<Guid> dataUuids = new List<Guid>();
		IntPtr serviceData = Get(adv, b.keyServiceData);
		if (serviceData != IntPtr.Zero) dataUuids.AddRange(Items(Send(serviceData, Sel("allKeys"))).Select(FromCBUUID));
		short? r = rssi == IntPtr.Zero ? null : (short)Math.Clamp(SendInt(rssi, Sel("intValue")), short.MinValue, short.MaxValue);
		if (r == 127) r = null; // 127 = kullanılamıyor
		Advert a = new Advert(id, name, uuids, dataUuids, r);
		ThreadPool.UnsafeQueueUserWorkItem(_ => { try { sink(a); } catch { } }, null);
	});

	private static MacPeripheral? Find(IntPtr peripheral) =>
		instance != null && instance.active.TryGetValue(peripheral, out MacPeripheral? mp) ? mp : null;

	[UnmanagedCallersOnly]
	private static void DidConnect(IntPtr self, IntPtr cmd, IntPtr centralManager, IntPtr peripheral) => Guard("connect", () =>
	{
		Find(peripheral)?.Connected.TrySetResult();
	});

	[UnmanagedCallersOnly]
	private static void DidFailToConnect(IntPtr self, IntPtr cmd, IntPtr centralManager, IntPtr peripheral, IntPtr error) => Guard("failConnect", () =>
	{
		var e = Error(error);
		instance?.log($"CoreBluetooth: bağlantı başarısız: {e.Domain} {e.Code} {e.Description}");
		if (instance != null && instance.TakeCancelEcho(peripheral)) return;
		Find(peripheral)?.Connected.TrySetException(GattException.FromNSError(e.Domain ?? "CBErrorDomain", e.Domain == null ? 10 : e.Code, e.Description));
	});

	[UnmanagedCallersOnly]
	private static void DidDisconnect(IntPtr self, IntPtr cmd, IntPtr centralManager, IntPtr peripheral, IntPtr error) => Guard("disconnect", () =>
	{
		var e = Error(error);
		if (instance != null && instance.TakeCancelEcho(peripheral))
		{
			instance.log("CoreBluetooth: bağlantı kapatıldı (istekle)");
			return;
		}
		instance?.log($"CoreBluetooth: bağlantı koptu: {e.Domain} {e.Code} {e.Description}");
		MacPeripheral? mp = Find(peripheral);
		if (mp == null) return;
		mp.Connected.TrySetException(e.Domain != null ? GattException.FromNSError(e.Domain, e.Code, e.Description) : new GattException(GattFailure.NotConnected, "Bağlantı koptu."));
		mp.OnDisconnected(e.Description, e.Domain != null ? GattException.FromNSError(e.Domain, e.Code, e.Description) : null);
	});

	[UnmanagedCallersOnly]
	private static void DidDiscoverServices(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr error) => Guard("services", () =>
	{
		Find(peripheral)?.OnServices(error);
	});

	[UnmanagedCallersOnly]
	private static void DidDiscoverCharacteristics(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr service, IntPtr error) => Guard("characteristics", () =>
	{
		Find(peripheral)?.OnCharacteristics(service, error);
	});

	[UnmanagedCallersOnly]
	private static void DidUpdateValue(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr characteristic, IntPtr error) => Guard("read", () =>
	{
		Find(peripheral)?.OnRead(characteristic, error);
	});

	[UnmanagedCallersOnly]
	private static void DidWriteValue(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr characteristic, IntPtr error) => Guard("write", () =>
	{
		Find(peripheral)?.OnWrite(characteristic, error);
	});
}

/// <summary>Bağlı bir CBPeripheral. Bekleyen her işlem için tek TaskCompletionSource tutulur.</summary>
internal sealed class MacPeripheral : IGattPeripheral
{
	private readonly CoreBluetoothBackend owner;
	private readonly IntPtr peripheral;
	private readonly Dictionary<Guid, IntPtr> services = new Dictionary<Guid, IntPtr>();
	private readonly Dictionary<(Guid, Guid), IntPtr> chars = new Dictionary<(Guid, Guid), IntPtr>();
	private TaskCompletionSource<bool>? servicesTcs;
	private readonly Dictionary<Guid, TaskCompletionSource<bool>> charsTcs = new Dictionary<Guid, TaskCompletionSource<bool>>();
	private readonly Dictionary<Guid, TaskCompletionSource<byte[]>> readTcs = new Dictionary<Guid, TaskCompletionSource<byte[]>>();
	private readonly Dictionary<Guid, TaskCompletionSource<bool>> writeTcs = new Dictionary<Guid, TaskCompletionSource<bool>>();
	private bool disposed;
	private bool lost;

	public TaskCompletionSource Connected { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	public string Id { get; }
	public string? Name { get; set; }

	public bool IsConnected => !lost && !disposed && Connected.Task.IsCompletedSuccessfully;

	public event Action<string?>? Disconnected;

	public MacPeripheral(CoreBluetoothBackend owner, IntPtr peripheral, string id)
	{
		this.owner = owner;
		this.peripheral = peripheral;
		Id = id;
	}

	private static TaskCompletionSource<T> New<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

	private void EnsureConnected()
	{
		if (!IsConnected || Objc.SendNInt(peripheral, Objc.Sel("state")) != 2)
			throw new GattException(GattFailure.NotConnected, "Bağlı değil.");
	}

	private static async Task<T> Await<T>(TaskCompletionSource<T> tcs, int seconds, string what, CancellationToken ct)
	{
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
		limit.CancelAfter(TimeSpan.FromSeconds(seconds));
		try
		{
			return await tcs.Task.WaitAsync(limit.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new GattException(GattFailure.NotConnected, what + ": ampul " + seconds + " sn içinde yanıt vermedi.");
		}
	}

	public async Task<IReadOnlyList<Guid>> DiscoverServicesAsync(IReadOnlyList<Guid>? filter, CancellationToken ct)
	{
		TaskCompletionSource<bool> tcs = New<bool>();
		owner.Queue.Run(() =>
		{
			EnsureConnected();
			servicesTcs = tcs;
			IntPtr arr = filter == null ? IntPtr.Zero : Objc.NSArray(filter.Select(CoreBluetoothBackend.CBUUID).ToList());
			Objc.SendVoid(peripheral, Objc.Sel("discoverServices:"), arr);
		});
		await Await(tcs, 20, "Servis keşfi", ct);
		return owner.Queue.Run(() => services.Keys.ToList());
	}

	public async Task<IReadOnlyList<GattChar>> DiscoverCharacteristicsAsync(Guid service, CancellationToken ct)
	{
		TaskCompletionSource<bool> tcs = New<bool>();
		owner.Queue.Run(() =>
		{
			EnsureConnected();
			if (!services.TryGetValue(service, out IntPtr s)) throw new GattException(GattFailure.Unsupported, "Servis yok: " + service);
			charsTcs[service] = tcs;
			Objc.SendVoid(peripheral, Objc.Sel("discoverCharacteristics:forService:"), IntPtr.Zero, s);
		});
		await Await(tcs, 20, "Kontrol keşfi", ct);
		return owner.Queue.Run(() =>
		{
			List<GattChar> list = new List<GattChar>();
			foreach (var kv in chars.Where(k => k.Key.Item1 == service))
			{
				CharProps props = (CharProps)(int)Objc.SendNUIntRet(kv.Value, Objc.Sel("properties"));
				list.Add(new GattChar(kv.Key.Item2, props));
			}
			return list;
		});
	}

	public async Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct)
	{
		TaskCompletionSource<byte[]> tcs = New<byte[]>();
		owner.Queue.Run(() =>
		{
			EnsureConnected();
			if (!chars.TryGetValue((service, characteristic), out IntPtr c)) throw new GattException(GattFailure.Unsupported, "Kontrol yok: " + characteristic);
			readTcs[characteristic] = tcs;
			Objc.SendVoid(peripheral, Objc.Sel("readValueForCharacteristic:"), c);
		});
		// İlk şifreli okumada macOS eşleştirme penceresi açabilir: kullanıcıya zaman tanı.
		return await Await(tcs, 45, "Okuma", ct);
	}

	public async Task WriteAsync(Guid service, Guid characteristic, byte[] data, CancellationToken ct)
	{
		TaskCompletionSource<bool> tcs = New<bool>();
		owner.Queue.Run(() =>
		{
			EnsureConnected();
			if (!chars.TryGetValue((service, characteristic), out IntPtr c)) throw new GattException(GattFailure.Unsupported, "Kontrol yok: " + characteristic);
			writeTcs[characteristic] = tcs;
			// CBCharacteristicWriteWithResponse = 0 (Windows'taki WriteWithResponse ile aynı)
			Objc.SendVoidWrite(peripheral, Objc.Sel("writeValue:forCharacteristic:type:"), Objc.NSData(data), c, 0);
		});
		await Await(tcs, 15, "Yazma", ct);
	}

	// ---- kuyruk tarafı

	private static GattException? Fail(IntPtr error)
	{
		if (error == IntPtr.Zero) return null;
		var e = Objc.Error(error);
		return GattException.FromNSError(e.Domain, e.Code, e.Description);
	}

	internal void OnServices(IntPtr error)
	{
		TaskCompletionSource<bool>? tcs = servicesTcs;
		servicesTcs = null;
		if (Fail(error) is GattException ex) { tcs?.TrySetException(ex); return; }
		foreach (IntPtr s in Objc.Items(Objc.Send(peripheral, Objc.Sel("services"))))
		{
			services[CoreBluetoothBackend.FromCBUUID(Objc.Send(s, Objc.Sel("UUID")))] = s;
		}
		tcs?.TrySetResult(true);
	}

	internal void OnCharacteristics(IntPtr service, IntPtr error)
	{
		Guid sid = CoreBluetoothBackend.FromCBUUID(Objc.Send(service, Objc.Sel("UUID")));
		charsTcs.Remove(sid, out TaskCompletionSource<bool>? tcs);
		if (Fail(error) is GattException ex) { tcs?.TrySetException(ex); return; }
		foreach (IntPtr c in Objc.Items(Objc.Send(service, Objc.Sel("characteristics"))))
		{
			chars[(sid, CoreBluetoothBackend.FromCBUUID(Objc.Send(c, Objc.Sel("UUID"))))] = c;
		}
		tcs?.TrySetResult(true);
	}

	internal void OnRead(IntPtr characteristic, IntPtr error)
	{
		Guid cid = CoreBluetoothBackend.FromCBUUID(Objc.Send(characteristic, Objc.Sel("UUID")));
		if (!readTcs.Remove(cid, out TaskCompletionSource<byte[]>? tcs)) return; // bildirim; kullanılmıyor
		if (Fail(error) is GattException ex) { tcs.TrySetException(ex); return; }
		tcs.TrySetResult(Objc.Bytes(Objc.Send(characteristic, Objc.Sel("value"))));
	}

	internal void OnWrite(IntPtr characteristic, IntPtr error)
	{
		Guid cid = CoreBluetoothBackend.FromCBUUID(Objc.Send(characteristic, Objc.Sel("UUID")));
		if (!writeTcs.Remove(cid, out TaskCompletionSource<bool>? tcs)) return;
		if (Fail(error) is GattException ex) { tcs.TrySetException(ex); return; }
		tcs.TrySetResult(true);
	}

	internal void OnDisconnected(string? reason, GattException? error = null)
	{
		if (lost) return;
		lost = true;
		GattException ex = error ?? new GattException(GattFailure.NotConnected, "Bağlantı koptu." + (reason != null ? " " + reason : ""));
		servicesTcs?.TrySetException(ex);
		foreach (var t in charsTcs.Values) t.TrySetException(ex);
		foreach (var t in readTcs.Values) t.TrySetException(ex);
		foreach (var t in writeTcs.Values) t.TrySetException(ex);
		charsTcs.Clear();
		readTcs.Clear();
		writeTcs.Clear();
		services.Clear();
		chars.Clear();
		Action<string?>? h = Disconnected;
		if (h != null) ThreadPool.UnsafeQueueUserWorkItem(_ => { try { h(reason); } catch { } }, null);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		Disconnected = null;
		try
		{
			owner.Queue.Run(() =>
			{
				OnDisconnected("kapatıldı");
			});
			owner.CancelConnection(peripheral, this);
		}
		catch
		{
		}
	}
}
