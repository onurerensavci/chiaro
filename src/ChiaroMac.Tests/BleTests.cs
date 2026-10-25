using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

public class BleUuidTests
{
	[Theory]
	[InlineData("FE0F", "0000fe0f-0000-1000-8000-00805f9b34fb")]
	[InlineData("180A", "0000180a-0000-1000-8000-00805f9b34fb")]
	[InlineData("2a24", "00002a24-0000-1000-8000-00805f9b34fb")]
	[InlineData("932C32BD-0002-47A2-835A-A8D455B859DD", "932c32bd-0002-47a2-835a-a8d455b859dd")]
	public void Parse(string cb, string guid) => Assert.Equal(new Guid(guid), BleUuid.Parse(cb));

	[Fact]
	public void ToCoreBluetoothRoundTrip()
	{
		Assert.Equal("FE0F", BleUuid.ToCoreBluetooth(Protocol.AdvertisedService));
		Assert.Equal("180A", BleUuid.ToCoreBluetooth(HueGattTransport.DeviceInfoService));
		Assert.Equal("932C32BD-0005-47A2-835A-A8D455B859DD", BleUuid.ToCoreBluetooth(Protocol.Characteristic(5)));
		foreach (int c in new[] { 0, 1, 2, 3, 4, 5 })
			Assert.Equal(Protocol.Characteristic(c), BleUuid.Parse(BleUuid.ToCoreBluetooth(Protocol.Characteristic(c))));
	}
}

public class GattErrorTests
{
	[Theory]
	[InlineData("CBATTErrorDomain", 5, GattFailure.Att)]
	[InlineData("CBATTErrorDomain", 15, GattFailure.Att)]
	[InlineData("CBErrorDomain", 14, GattFailure.PeerRemovedPairing)]
	[InlineData("CBErrorDomain", 15, GattFailure.EncryptionTimedOut)]
	[InlineData("CBErrorDomain", 6, GattFailure.NotConnected)]
	[InlineData("CBErrorDomain", 7, GattFailure.NotConnected)]
	[InlineData("CBErrorDomain", 10, GattFailure.ConnectionFailed)]
	[InlineData("CBErrorDomain", 12, GattFailure.UnknownDevice)]
	[InlineData("NSCocoaErrorDomain", 1, GattFailure.Other)]
	public void FromNSError(string domain, long code, GattFailure expected)
	{
		var e = GattException.FromNSError(domain, code, "x");
		Assert.Equal(expected, e.Failure);
		if (expected == GattFailure.Att) Assert.Equal((int)code, e.AttCode);
	}

	[Theory]
	[InlineData(5, "Eşleştirme yetersiz")]
	[InlineData(8, "Eşleştirme yetersiz")]
	[InlineData(15, "Eşleştirme yetersiz")]
	[InlineData(3, "desteklemiyor")]
	[InlineData(6, "desteklemiyor")]
	[InlineData(13, "kabul etmedi")]
	[InlineData(19, "kabul etmedi")]
	[InlineData(1, "reddetti")]
	public void DescribeAttMatchesWindows(int att, string fragment)
	{
		string s = HueGattTransport.Describe(new GattException(GattFailure.Att, "", att, "CBATTErrorDomain", att));
		Assert.Contains(fragment, s);
		Assert.EndsWith($"(ATT {att:X2})", s);
	}

	[Fact]
	public void DescribeMacSpecific()
	{
		Assert.Contains("unut", HueGattTransport.Describe(GattException.FromNSError("CBErrorDomain", 14, "")));
		Assert.Contains("Şifreleme", HueGattTransport.Describe(GattException.FromNSError("CBErrorDomain", 15, "")));
		Assert.Contains("CBErrorDomain 15", HueGattTransport.Describe(GattException.FromNSError("CBErrorDomain", 15, "")));
	}
}

// Test36Tests CHIARO_DEMO_FAIL_CONNECTS ortam değişkenini kısa süre ayarlıyor: aynı koleksiyonda (sırayla) çalışsın.
[Collection("Loc")]
public class TransportTests
{
	private static (HueGattTransport t, DemoBackend b, List<string> log) Make()
	{
		var b = new DemoBackend();
		var log = new List<string>();
		return (new HueGattTransport(b, s => { lock (log) log.Add(s); }), b, log);
	}

	private static Lamp Demo => new Lamp(DemoBackend.DemoId, "Hue Play", false, -50);

	[Fact]
	public void HueCandidateFilter()
	{
		var none = Array.Empty<Guid>();
		Assert.True(HueGattTransport.IsHueCandidate(new Advert("a", null, new[] { Protocol.AdvertisedService }, none, -40)));
		Assert.True(HueGattTransport.IsHueCandidate(new Advert("a", null, none, new[] { Protocol.AdvertisedService }, -40)));
		Assert.True(HueGattTransport.IsHueCandidate(new Advert("a", "Hue Play", none, none, -40)));
		Assert.True(HueGattTransport.IsHueCandidate(new Advert("a", null, none, none, null, Known: true)));
		Assert.False(HueGattTransport.IsHueCandidate(new Advert("a", "AirPods", none, none, -40)));
	}

	[Fact]
	public async Task ScanFindsDemoBulb()
	{
		var (t, _, _) = Make();
		var found = new List<Lamp>();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await t.ScanAsync(l => { lock (found) found.Add(l); }, cts.Token);
		Assert.Contains(found, l => l.Id == DemoBackend.DemoId);
	}

	[Fact]
	public async Task ConnectReadWrite()
	{
		var (t, b, log) = Make();
		var stages = new List<string>();
		await t.ConnectAsync(Demo, stages.Add, CancellationToken.None);
		Assert.True(t.Ready);
		Assert.NotEmpty(stages);
		Assert.Contains("LCT024", t.Metadata);
		foreach (int c in new[] { 2, 3, 4, 5 }) Assert.True(t.Supports(c), $"Supports {c}");

		Assert.Equal(new byte[] { 1 }, await t.Read(2, default));
		await t.Write(3, Protocol.Brightness(50), default);
		await t.Write(4, Protocol.Temperature(2700), default);
		await t.Write(5, Protocol.Rgb(255, 0, 0), default);
		await t.Write(2, Protocol.Power(false), default);
		Assert.Equal(new[] { 3, 4, 5, 2 }, b.Bulb.Writes.Select(w => w.Code).ToArray());
		Assert.Equal("7F", Convert.ToHexString(b.Bulb.Writes[0].Data));
		Assert.Equal(new byte[] { 0x7F }, await t.Read(3, default));
		Assert.Contains(log, l => l.StartsWith("Yazma 0003: 7F"));

		t.Disconnect();
		Assert.False(t.Ready);
		Assert.False(t.Supports(3));
	}

	[Fact]
	public async Task UnsupportedAttMarksCharacteristicRejected()
	{
		var (t, b, _) = Make();
		await t.ConnectAsync(Demo, _ => { }, default);
		b.Bulb.AttErrors[5] = 6;
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Write(5, Protocol.Rgb(1, 2, 3), default));
		Assert.Contains("desteklemiyor", ex.Message);
		Assert.False(t.Supports(5));
		Assert.True(t.Supports(3));
	}

	[Fact]
	public async Task PowerIsNeverRejectedAndPairingMessage()
	{
		var (t, b, _) = Make();
		await t.ConnectAsync(Demo, _ => { }, default);
		b.Bulb.AttErrors[2] = 5;
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Write(2, Protocol.Power(true), default));
		Assert.Contains("Eşleştirme yetersiz", ex.Message);
		Assert.True(t.Supports(2));
	}

	[Fact]
	public async Task InvalidValueRejectedByBulb()
	{
		var (t, _, _) = Make();
		await t.ConnectAsync(Demo, _ => { }, default);
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Write(3, new byte[] { 0 }, default));
		Assert.Contains("kabul etmedi", ex.Message);
	}

	[Fact]
	public async Task DropRaisesConnectionChanged()
	{
		var (t, b, _) = Make();
		await t.ConnectAsync(Demo, _ => { }, default);
		bool? state = null;
		t.ConnectionChanged += s => state = s;
		b.Bulb.Drop();
		await Task.Delay(50);
		Assert.False(state);
		Assert.False(t.Ready);
	}

	[Fact]
	public void LampJsonRoundTrip()
	{
		var l = new Lamp("5C2A1F0E-1111-2222-3333-444455556666", "Hue Play", true, -61);
		var back = System.Text.Json.JsonSerializer.Deserialize<Lamp>(System.Text.Json.JsonSerializer.Serialize(l));
		Assert.Equal(l, back);
		Assert.Equal("6666", l.Tail);
	}
}
