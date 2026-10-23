#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HueStudio;

/// <summary>
/// Uygulamanın kullandığı ampul arayüzü — Windows'taki BleController ile aynı yüzey
/// (Ready, Supports, ScanAsync, ConnectAsync, Read, ReadOptional, Write, Disconnect).
/// </summary>
public interface IBulbTransport : IDisposable
{
	bool Ready { get; }
	string Metadata { get; }
	event Action<bool>? ConnectionChanged;
	bool Supports(int code);
	Task<string> AdapterStatus(CancellationToken ct);
	Task ScanAsync(Action<Lamp> found, CancellationToken ct);
	Task ConnectAsync(Lamp lamp, Action<string> progress, CancellationToken ct);
	Task<byte[]?> Read(int code, CancellationToken ct);
	Task<byte[]?> ReadOptional(int code, CancellationToken ct);
	Task Write(int code, byte[] data, CancellationToken ct);
	void Disconnect();
}

[Flags]
public enum CharProps
{
	None = 0,
	Broadcast = 0x01,
	Read = 0x02,
	WriteWithoutResponse = 0x04,
	Write = 0x08,
	Notify = 0x10,
	Indicate = 0x20,
}

/// <summary>Bir reklam (advertisement) kaydı. Id: CoreBluetooth çevre birimi kimliği (NSUUID).</summary>
public sealed record Advert(string Id, string? Name, IReadOnlyList<Guid> ServiceUuids, IReadOnlyList<Guid> ServiceDataUuids, short? Rssi, bool Known = false);

public sealed record GattChar(Guid Uuid, CharProps Props);

/// <summary>Düşük seviye BLE arka ucu (macOS: CoreBluetooth; Linux: yalnızca demo).</summary>
public interface IGattBackend : IDisposable
{
	string Name { get; }
	Task<string> AdapterStatusAsync(CancellationToken ct);
	/// <summary>Tarar; sistemin zaten bağlı tuttuğu Hue ampullerini de (Known=true) bildirir.</summary>
	Task ScanAsync(Action<Advert> found, TimeSpan duration, CancellationToken ct);
	Task<IGattPeripheral> ConnectAsync(string id, Action<string> log, CancellationToken ct);
}

public interface IGattPeripheral : IDisposable
{
	string Id { get; }
	string? Name { get; }
	bool IsConnected { get; }
	/// <summary>Bağlantı koptuğunda (hata iletisiyle).</summary>
	event Action<string?>? Disconnected;
	Task<IReadOnlyList<Guid>> DiscoverServicesAsync(IReadOnlyList<Guid>? filter, CancellationToken ct);
	Task<IReadOnlyList<GattChar>> DiscoverCharacteristicsAsync(Guid service, CancellationToken ct);
	Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct);
	Task WriteAsync(Guid service, Guid characteristic, byte[] data, CancellationToken ct);
}

public enum GattFailure
{
	Other,
	/// <summary>ATT hata kodu (AttCode dolu).</summary>
	Att,
	NotConnected,
	Unauthorized,
	PoweredOff,
	Unsupported,
	/// <summary>CBErrorPeerRemovedPairingInformation (14): ampul eşleştirmeyi unutmuş.</summary>
	PeerRemovedPairing,
	/// <summary>CBErrorEncryptionTimedOut (15)</summary>
	EncryptionTimedOut,
	ConnectionFailed,
	UnknownDevice,
}

public sealed class GattException : Exception
{
	public GattFailure Failure { get; }
	public int? AttCode { get; }
	public string? Domain { get; }
	public long Code { get; }

	public GattException(GattFailure failure, string message, int? att = null, string? domain = null, long code = 0)
		: base(message)
	{
		Failure = failure;
		AttCode = att;
		Domain = domain;
		Code = code;
	}

	/// <summary>NSError (alan adı + kod) → sınıflandırılmış hata.</summary>
	public static GattException FromNSError(string? domain, long code, string? description)
	{
		string d = domain ?? "";
		string text = $"{d} {code}: {description}";
		if (d == "CBATTErrorDomain")
		{
			return new GattException(GattFailure.Att, text, (int)code, d, code);
		}
		if (d == "CBErrorDomain")
		{
			GattFailure f = code switch
			{
				3 or 7 or 6 => GattFailure.NotConnected,      // notConnected, peripheralDisconnected, connectionTimeout
				10 or 11 => GattFailure.ConnectionFailed,     // connectionFailed, connectionLimitReached
				12 => GattFailure.UnknownDevice,
				13 => GattFailure.Unsupported,                // operationNotSupported
				14 => GattFailure.PeerRemovedPairing,
				15 => GattFailure.EncryptionTimedOut,
				_ => GattFailure.Other,
			};
			return new GattException(f, text, null, d, code);
		}
		return new GattException(GattFailure.Other, text, null, d, code);
	}
}

/// <summary>Bluetooth UUID dönüşümleri (CoreBluetooth kısa "FE0F" biçimi ↔ 128-bit Guid).</summary>
public static class BleUuid
{
	private const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

	public static Guid Parse(string text)
	{
		string t = text.Trim();
		if (t.Length == 4) return new Guid("0000" + t + BaseSuffix);
		if (t.Length == 8) return new Guid(t + BaseSuffix);
		return Guid.Parse(t);
	}

	/// <summary>CBUUID UUIDWithString: için: Bluetooth taban UUID'sinde 16-bit kısa biçim, değilse tam biçim.</summary>
	public static string ToCoreBluetooth(Guid uuid)
	{
		string s = uuid.ToString("D");
		if (s.StartsWith("0000") && s.EndsWith(BaseSuffix, StringComparison.OrdinalIgnoreCase)) return s.Substring(4, 4).ToUpperInvariant();
		return s.ToUpperInvariant();
	}
}
