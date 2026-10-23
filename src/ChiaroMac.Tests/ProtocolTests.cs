using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>
/// Altın değerler Windows sürümündeki Protocol.cs formülünden bağımsız olarak (Python ile) hesaplandı.
/// Mac sürümü aynı Protocol.cs dosyasını bağlantıyla derler; bu testler kodlamanın bozulmadığını ve
/// Mac katmanının ampule tam olarak Windows'la aynı baytları gönderdiğini doğrular.
/// </summary>
public class ProtocolTests
{
	[Theory]
	[InlineData(0, "03")]
	[InlineData(1, "03")]
	[InlineData(50, "7F")]
	[InlineData(72, "B7")]
	[InlineData(100, "FE")]
	[InlineData(150, "FE")]
	public void Brightness(double pct, string hex) => Assert.Equal(hex, Convert.ToHexString(Protocol.Brightness(pct)));

	[Theory]
	[InlineData(2000, "C601")]
	[InlineData(2200, "C601")]
	[InlineData(2700, "7201")]
	[InlineData(3003, "4D01")]
	[InlineData(4000, "FA00")]
	[InlineData(6500, "9A00")]
	[InlineData(9000, "9A00")]
	public void Temperature(double k, string hex) => Assert.Equal(hex, Convert.ToHexString(Protocol.Temperature(k)));

	[Theory]
	[InlineData(255, 0, 0, "DBA37954")]
	[InlineData(0, 255, 0, "CC4C9999")]
	[InlineData(0, 0, 255, "67265D0F")]
	[InlineData(255, 255, 255, "0E503954")]
	[InlineData(0, 0, 0, "0D503954")]
	[InlineData(255, 140, 26, "2C86E169")]
	public void Rgb(byte r, byte g, byte b, string hex) => Assert.Equal(hex, Convert.ToHexString(Protocol.Rgb(r, g, b)));

	[Fact]
	public void XyNormalizesWhenSumAboveOne() => Assert.Equal("4992B66D", Convert.ToHexString(Protocol.Xy(0.8, 0.6)));

	[Fact]
	public void PowerBytes()
	{
		Assert.Equal(new byte[] { 1 }, Protocol.Power(true));
		Assert.Equal(new byte[] { 0 }, Protocol.Power(false));
	}

	[Fact]
	public void KelvinRead()
	{
		Assert.Equal(3000, Protocol.Kelvin(new byte[] { 0x4D, 0x01 }));
		Assert.Null(Protocol.Kelvin(new byte[] { 0, 0 }));
		Assert.Null(Protocol.Kelvin(new byte[] { 0xFF, 0xFF }));
		Assert.Null(Protocol.Kelvin(new byte[] { 1 }));
		Assert.Null(Protocol.Kelvin(null));
	}

	[Fact]
	public void KelvinRoundTripsToRequestedValueIn50KSteps()
	{
		// Ampul mired saklar; okunan değer istenen değere (50 K adımlarında) geri dönmeli: 3000 K → 3000 K, 3003 K değil.
		for (int k = 2200; k <= 6500; k += 50)
		{
			Assert.Equal(k, Protocol.Kelvin(Protocol.Temperature(k)));
		}
		Assert.Equal(3000, Protocol.SnapKelvin(3003.0));
		Assert.Equal(2200, Protocol.SnapKelvin(2197.8));
	}

	[Fact]
	public void Uuids()
	{
		Assert.Equal(new Guid("932c32bd-0003-47a2-835a-a8d455b859dd"), Protocol.Characteristic(3));
		Assert.Equal(new Guid("932c32bd-0000-47a2-835a-a8d455b859dd"), HueGattTransport.HueService);
		Assert.True(Protocol.HasFe0fServiceData(new byte[] { 0x0F, 0xFE, 1 }));
		Assert.False(Protocol.HasFe0fServiceData(new byte[] { 0xFE, 0x0F }));
	}

	[Fact]
	public void InvalidInputsThrow()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => Protocol.Brightness(double.NaN));
		Assert.Throws<ArgumentOutOfRangeException>(() => Protocol.Temperature(0));
	}
}
