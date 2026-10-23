#nullable enable annotations
#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace HueStudio;

public sealed class BleController : IDisposable
{
	private BluetoothLEDevice? device;

	private GattSession? session;

	private readonly List<GattDeviceService> services = new List<GattDeviceService>();

	private readonly Dictionary<Guid, GattCharacteristic> characteristics = new Dictionary<Guid, GattCharacteristic>();

	private readonly HashSet<int> rejected = new HashSet<int>();

	private readonly Action<string> log;

	public bool Ready { get; private set; }

	public string Metadata { get; private set; } = "Model / yazılım bilgisi alınamadı";

	public event Action<bool>? ConnectionChanged;

	public BleController(Action<string> logger)
	{
		log = logger;
	}

	public bool Supports(int code)
	{
		if (Ready && !rejected.Contains(code) && characteristics.TryGetValue(Protocol.Characteristic(code), out GattCharacteristic value))
		{
			return value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
		}
		return false;
	}

	public static async Task<string> AdapterStatus(CancellationToken ct)
	{
		BluetoothAdapter bluetoothAdapter = (await BluetoothAdapter.GetDefaultAsync().AsTask(ct)) ?? throw new InvalidOperationException("Bluetooth adaptörü yok.");
		if (!bluetoothAdapter.IsLowEnergySupported)
		{
			throw new InvalidOperationException("Adaptör Bluetooth LE desteklemiyor.");
		}
		Radio radio = await bluetoothAdapter.GetRadioAsync().AsTask(ct);
		if ((object)radio != null && radio.State != RadioState.On)
		{
			throw new InvalidOperationException("Bluetooth kapalı. Ayarlardan aç.");
		}
		return "Bluetooth LE adaptörü hazır";
	}

	public async Task ScanAsync(Action<Lamp> found, CancellationToken ct)
	{
		Action<string> action = log;
		action(await AdapterStatus(ct));
		BluetoothLEAdvertisementWatcher watcher = new BluetoothLEAdvertisementWatcher
		{
			ScanningMode = BluetoothLEScanningMode.Active
		};
		Exception scanError = null;
		TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		watcher.Received += (BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs e) =>
		{
			try
			{
				bool flag = e.Advertisement.ServiceUuids.Contains(Protocol.AdvertisedService) || e.Advertisement.ServiceUuids.Contains(Protocol.Characteristic(0));
				foreach (BluetoothLEAdvertisementDataSection item in e.Advertisement.DataSections.Where((BluetoothLEAdvertisementDataSection s) => s.DataType == 22))
				{
					flag |= Protocol.HasFe0fServiceData(Bytes(item.Data));
				}
				string localName = e.Advertisement.LocalName;
				bool flag2 = localName.Contains("Hue", StringComparison.OrdinalIgnoreCase);
				if (flag | flag2)
				{
					found(new Lamp(e.BluetoothAddress, e.BluetoothAddressType, string.IsNullOrWhiteSpace(localName) ? "Hue adayı" : localName, null, Paired: false, e.RawSignalStrengthInDBm));
				}
			}
			catch (Exception ex)
			{
				log("Reklam okunamadı: " + ex.Message);
			}
		};
		watcher.Stopped += (BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementWatcherStoppedEventArgs e) =>
		{
			if (e.Error != BluetoothError.Success)
			{
				scanError = new InvalidOperationException($"Tarama durdu ({e.Error}).");
			}
			stopped.TrySetResult();
		};
		try
		{
			watcher.Start();
			foreach (DeviceInformation item2 in (await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(pairingState: true), new string[] { "System.Devices.Aep.DeviceAddress" }, DeviceInformationKind.AssociationEndpoint).AsTask(ct)).Where((DeviceInformation d) => d.Name.Contains("Hue", StringComparison.OrdinalIgnoreCase)))
			{
				ulong result = 0uL;
				if (item2.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var value))
				{
					ulong.TryParse(value?.ToString()?.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, null, out result);
				}
				found(new Lamp(result, BluetoothAddressType.Unspecified, item2.Name, item2.Id, Paired: true, null));
			}
			await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(12L), ct), stopped.Task);
			ct.ThrowIfCancellationRequested();
			if (scanError != null)
			{
				throw scanError;
			}
		}
		finally
		{
			if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
			{
				watcher.Stop();
			}
		}
	}

	public async Task ConnectAsync(Lamp lamp, Action<string> progress, CancellationToken ct)
	{
		Disconnect();
		string stage = "Bluetooth adaptörü kontrolü";
		try
		{
			log($"Seçim: {lamp.Display}; adres türü={lamp.AddressType}; Windows kaydı var={lamp.DeviceId != null}");
			await AdapterStatus(ct);
			Stage("Bluetooth adresiyle cihaz açma");
			Func<ulong, CancellationToken, Task<BluetoothLEDevice>> openTyped = null;
			BluetoothAddressType addressType = lamp.AddressType;
			if ((uint)addressType <= 1u)
			{
				openTyped = async (ulong address, CancellationToken token) => await BluetoothLEDevice.FromBluetoothAddressAsync(address, lamp.AddressType).AsTask(token);
			}
			device = await DeviceConnection.OpenByAddressAsync(lamp.Address, async (ulong address, CancellationToken token) => await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token), openTyped, log, ct);
			Stage("Windows eşleştirme kaydı kontrolü");
			log($"Cihaz: {lamp.Display}; adres türü: {lamp.AddressType}; eşleşmiş: {device.DeviceInformation.Pairing.IsPaired}");
			if (!device.DeviceInformation.Pairing.IsPaired)
			{
				Stage("Eşleştirme — Windows onay penceresi açılırsa onayla");
				DeviceInformationCustomPairing pairing = device.DeviceInformation.Pairing.Custom;
				pairing.PairingRequested += Accept;
				try
				{
					DevicePairingResult devicePairingResult = await pairing.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption).AsTask(ct);
					log("Eşleştirme: " + devicePairingResult.Status);
					DevicePairingResultStatus status = devicePairingResult.Status;
					if (status != DevicePairingResultStatus.Paired && status != DevicePairingResultStatus.AlreadyPaired)
					{
						throw new InvalidOperationException($"Eşleştirilemedi ({devicePairingResult.Status}). Ampulü görünür yap; bkz. Yardım.");
					}
				}
				finally
				{
					pairing.PairingRequested -= Accept;
				}
			}
			Stage("GATT oturumu açma");
			session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
			if ((object)session != null && session.CanMaintainConnection)
			{
				session.MaintainConnection = true;
			}
			Stage("Hue servislerini okuma");
			GattDeviceServicesResult gattDeviceServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
			services.AddRange(gattDeviceServicesResult.Services);
			Check(gattDeviceServicesResult.Status, gattDeviceServicesResult.ProtocolError, "Servis keşfi");
			GattDeviceService? gattDeviceService = services.FirstOrDefault((GattDeviceService s) => s.Uuid == Protocol.Characteristic(0)) ?? throw new InvalidOperationException("Bu cihaz Hue Bluetooth kontrolünü desteklemiyor.");
			Stage("Hue kontrollerini okuma");
			GattCharacteristicsResult gattCharacteristicsResult = await gattDeviceService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
			Check(gattCharacteristicsResult.Status, gattCharacteristicsResult.ProtocolError, "Kontrol keşfi");
			foreach (GattCharacteristic characteristic in gattCharacteristicsResult.Characteristics)
			{
				characteristics[characteristic.Uuid] = characteristic;
				log($"GATT {characteristic.Uuid}: {characteristic.CharacteristicProperties}");
			}
			if (!characteristics.TryGetValue(Protocol.Characteristic(2), out GattCharacteristic value) || !value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
			{
				throw new InvalidOperationException("Ampul aç/kapat komutunu desteklemiyor.");
			}
			if (!value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
			{
				throw new InvalidOperationException("Güç durumu okunamadı.");
			}
			Stage("Ampulün güç durumunu okuma");
			byte[] array = await Read(2, ct);
			if (array == null || array.Length != 1 || array[0] > 1)
			{
				throw new InvalidOperationException("Ampul yazılımı desteklenmiyor.");
			}
			Stage("Model ve yazılım bilgisini okuma");
			await ReadMetadata(ct);
			Ready = true;
			device.ConnectionStatusChanged += StatusChanged;
			log("GATT okuma erişimi doğrulandı; yazma erişimi ilk komutta sınanacak.");
		}
		catch (OperationCanceledException)
		{
			Disconnect();
			throw;
		}
		catch (Exception ex2)
		{
			log($"Başarısız aşama: {stage}; HRESULT=0x{ex2.HResult:X8}; {ex2}");
			Disconnect();
			string text;
			if (ex2 is InvalidOperationException)
			{
				text = ex2.Message;
			}
			else
			{
				text = ((ex2 is UnauthorizedAccessException) ? "Bluetooth erişimi reddedildi." : $"Bağlanılamadı (0x{ex2.HResult:X8}).");
			}
			throw new InvalidOperationException(stage + ": " + text, ex2);
		}
		static void Accept(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
		{
			if (args.PairingKind == DevicePairingKinds.ConfirmOnly)
			{
				args.Accept();
			}
		}
		void Stage(string text2)
		{
			stage = text2;
			log("Bağlantı aşaması: " + text2);
			progress(text2 + "…");
		}
	}

	private async Task ReadMetadata(CancellationToken ct)
	{
		GattDeviceService gattDeviceService = services.FirstOrDefault((GattDeviceService s) => s.Uuid == new Guid("0000180a-0000-1000-8000-00805f9b34fb"));
		if ((object)gattDeviceService == null)
		{
			return;
		}
		GattCharacteristicsResult result;
		try
		{
			result = await gattDeviceService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
			if (result.Status == GattCommunicationStatus.Success)
			{
				Metadata = "Model: " + await ReadText("2a24") + "  •  Yazılım: " + await ReadText("2a28");
				log(Metadata);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			log("Model bilgisi: " + ex2.Message);
		}
		async Task<string> ReadText(string id)
		{
			GattCharacteristic gattCharacteristic = result.Characteristics.FirstOrDefault((GattCharacteristic x) => x.Uuid == new Guid("0000" + id + "-0000-1000-8000-00805f9b34fb"));
			if ((object)gattCharacteristic == null)
			{
				return "bilinmiyor";
			}
			GattReadResult gattReadResult = await gattCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
			return (gattReadResult.Status == GattCommunicationStatus.Success) ? Encoding.UTF8.GetString(Bytes(gattReadResult.Value)).TrimEnd('\0') : "okunamadı";
		}
	}

	public async Task<byte[]?> Read(int code, CancellationToken ct)
	{
		if (!characteristics.TryGetValue(Protocol.Characteristic(code), out GattCharacteristic value) || !value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
		{
			return null;
		}
		GattReadResult gattReadResult = await value.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
		Check(gattReadResult.Status, gattReadResult.ProtocolError, "Durum okuma");
		return Bytes(gattReadResult.Value);
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
		catch (Exception ex2)
		{
			log($"İsteğe bağlı {code:0000} okuma: {ex2.Message}");
			return null;
		}
	}

	public async Task Write(int code, byte[] data, CancellationToken ct)
	{
		if (!Supports(code))
		{
			throw new InvalidOperationException("Bu ampulde bu kontrol yok.");
		}
		using DataWriter writer = new DataWriter();
		writer.WriteBytes(data);
		GattWriteResult gattWriteResult = await characteristics[Protocol.Characteristic(code)].WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct);
		log($"Yazma {code:0000}: {Convert.ToHexString(data)} → {gattWriteResult.Status}, ATT={gattWriteResult.ProtocolError:X2}");
		bool flag = code != 2 && gattWriteResult.Status == GattCommunicationStatus.ProtocolError;
		if (flag)
		{
			bool flag2;
			switch (gattWriteResult.ProtocolError)
			{
			case 3:
			case 6:
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		if (flag)
		{
			rejected.Add(code);
		}
		Check(gattWriteResult.Status, gattWriteResult.ProtocolError, "Komut");
	}

	private void StatusChanged(BluetoothLEDevice sender, object args)
	{
		if ((object)sender == device)
		{
			ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);
		}
	}

	private static byte[] Bytes(IBuffer buffer)
	{
		using DataReader dataReader = DataReader.FromBuffer(buffer);
		byte[] array = new byte[buffer.Length];
		dataReader.ReadBytes(array);
		return array;
	}

	private static void Check(GattCommunicationStatus status, byte? att, string action)
	{
		if (status == GattCommunicationStatus.Success)
		{
			return;
		}
		string text;
		switch (status)
		{
		case GattCommunicationStatus.AccessDenied:
			text = "Erişim reddedildi. Eşleştirmeyi kontrol et.";
			break;
		case GattCommunicationStatus.Unreachable:
			text = "Ampule ulaşılamıyor. Açık ve yakında mı?";
			break;
		default:
		{
			bool flag;
			switch (att)
			{
			case 5:
			case 8:
			case 15:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				text = "Eşleştirme yetersiz. Eski kaydı Windows’tan kaldırıp yeniden eşleştir.";
				break;
			}
			switch (att)
			{
			case 3:
			case 6:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				text = "Bu model desteklemiyor.";
				break;
			}
			switch (att)
			{
			case 13:
			case 19:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			text = ((!flag) ? "Ampul reddetti. Yeniden bağlan." : "Ampul bu değeri kabul etmedi.");
			break;
		}
		}
		string value = text;
		throw new InvalidOperationException($"{action}: {value} ({status}, ATT {att?.ToString("X2") ?? "—"})");
	}

	public void Disconnect()
	{
		Ready = false;
		if ((object)device != null)
		{
			device.ConnectionStatusChanged -= StatusChanged;
		}
		characteristics.Clear();
		rejected.Clear();
		foreach (GattDeviceService service in services)
		{
			service.Dispose();
		}
		services.Clear();
		if ((object)session != null)
		{
			session.MaintainConnection = false;
			session.Dispose();
			session = null;
		}
		device?.Dispose();
		device = null;
		Metadata = "Model / yazılım bilgisi alınamadı";
	}

	public void Dispose()
	{
		Disconnect();
	}
}
