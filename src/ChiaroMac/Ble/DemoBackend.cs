#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>
/// Benzetimli Hue Play (yalnızca Linux önizlemesi / testler için; macOS sürümünde CHIARO_DEMO=1 verilmedikçe kullanılmaz).
/// Gerçek ampul gibi davranır: 0002..0005 okunur/yazılır, değer aralığı denetlenir.
/// </summary>
public sealed class DemoBackend : IGattBackend
{
	public const string DemoId = "D3M0B0B0-0000-4000-8000-00000000C1A0";

	public string Name => "Demo";

	public DemoPeripheral Bulb { get; } = new DemoPeripheral();

	private int failConnects = int.TryParse(Environment.GetEnvironmentVariable("CHIARO_DEMO_FAIL_CONNECTS"), out int n) ? Math.Max(0, n) : 0;

	public DemoBackend()
	{
		if (Environment.GetEnvironmentVariable("CHIARO_DEMO_POWER_OFF") == "1") Bulb.SetPowerForDemo(false);
	}

	public Task<string> AdapterStatusAsync(CancellationToken ct) => Task.FromResult("Bluetooth LE adaptörü hazır");

	public async Task ScanAsync(Action<Advert> found, TimeSpan duration, CancellationToken ct)
	{
		await Task.Delay(300, ct);
		found(new Advert(DemoId, "Hue Play", new[] { Protocol.AdvertisedService }, Array.Empty<Guid>(), -52));
		await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1200, duration.TotalMilliseconds)), ct);
	}

	public async Task<IGattPeripheral> ConnectAsync(string id, Action<string> log, CancellationToken ct)
	{
		await Task.Delay(250, ct);
		if (id != DemoId) throw new GattException(GattFailure.UnknownDevice, "Çevre birimi bulunamadı");
		// Önizleme/test: ilk N bağlantı başarısız (Bluetooth oturum açılışında henüz hazır değil gibi).
		if (failConnects > 0)
		{
			failConnects--;
			throw new GattException(GattFailure.ConnectionFailed, "Bluetooth henüz hazır değil (demo)");
		}
		Bulb.Connect();
		return Bulb;
	}

	public void Dispose()
	{
	}
}

public sealed class DemoPeripheral : IGattPeripheral
{
	private readonly Dictionary<Guid, byte[]> values = new Dictionary<Guid, byte[]>
	{
		[Protocol.Characteristic(2)] = new byte[] { 1 },
		[Protocol.Characteristic(3)] = new byte[] { 183 },
		[Protocol.Characteristic(4)] = new byte[] { 0x4D, 0x01 },
		[Protocol.Characteristic(5)] = new byte[] { 0, 0, 0, 0 },
		[BleUuid.Parse("2A24")] = Encoding.UTF8.GetBytes("LCT024"),
		[BleUuid.Parse("2A28")] = Encoding.UTF8.GetBytes("1.104.2"),
	};

	public List<(int Code, byte[] Data)> Writes { get; } = new List<(int, byte[])>();

	/// <summary>Testler için: bu kod yazılınca verilecek ATT hatası.</summary>
	public Dictionary<int, int> AttErrors { get; } = new Dictionary<int, int>();

	public string Id => DemoBackend.DemoId;
	public string? Name => "Hue Play";
	public bool IsConnected { get; private set; }

	public event Action<string?>? Disconnected;

	public void Connect() => IsConnected = true;

	/// <summary>Önizleme/test: başlangıçta ampul kapalı.</summary>
	public void SetPowerForDemo(bool on) => values[Protocol.Characteristic(2)] = new byte[] { (byte)(on ? 1 : 0) };

	public void Drop()
	{
		IsConnected = false;
		Disconnected?.Invoke("demo");
	}

	public Task<IReadOnlyList<Guid>> DiscoverServicesAsync(IReadOnlyList<Guid>? filter, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<Guid>>(new[] { HueGattTransport.HueService, HueGattTransport.DeviceInfoService });

	public Task<IReadOnlyList<GattChar>> DiscoverCharacteristicsAsync(Guid service, CancellationToken ct)
	{
		IReadOnlyList<GattChar> list = service == HueGattTransport.HueService
			? new[] { 1, 2, 3, 4, 5 }.Select(c => new GattChar(Protocol.Characteristic(c), CharProps.Read | CharProps.Write | (c == 2 ? CharProps.Notify : CharProps.None))).ToList()
			: new[] { new GattChar(BleUuid.Parse("2A24"), CharProps.Read), new GattChar(BleUuid.Parse("2A28"), CharProps.Read) };
		return Task.FromResult(list);
	}

	public async Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct)
	{
		await Task.Delay(15, ct);
		if (!IsConnected) throw new GattException(GattFailure.NotConnected, "Bağlı değil.");
		return values.TryGetValue(characteristic, out byte[]? v) ? v.ToArray() : Array.Empty<byte>();
	}

	public async Task WriteAsync(Guid service, Guid characteristic, byte[] data, CancellationToken ct)
	{
		await Task.Delay(20, ct);
		if (!IsConnected) throw new GattException(GattFailure.NotConnected, "Bağlı değil.");
		int code = Convert.ToInt32(characteristic.ToString("D").Substring(9, 4));
		if (AttErrors.TryGetValue(code, out int att)) throw new GattException(GattFailure.Att, "ATT", att, "CBATTErrorDomain", att);
		bool valid = code switch
		{
			2 => data.Length == 1 && data[0] <= 1,
			3 => data.Length == 1 && data[0] >= 1 && data[0] <= 254,
			4 => data.Length == 2,
			5 => data.Length == 4,
			_ => true,
		};
		if (!valid) throw new GattException(GattFailure.Att, "ATT 0D", 0x0D, "CBATTErrorDomain", 0x0D);
		values[characteristic] = data.ToArray();
		lock (Writes) Writes.Add((code, data.ToArray()));
	}

	public void Dispose() => IsConnected = false;
}
