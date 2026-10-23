#nullable enable annotations
#nullable disable warnings
using System;
using System.Buffers.Binary;

namespace HueStudio;

public static class Protocol
{
	public static readonly Guid AdvertisedService = new Guid("0000fe0f-0000-1000-8000-00805f9b34fb");

	public static Guid Characteristic(int code)
	{
		return new Guid($"932c32bd-{code:0000}-47a2-835a-a8d455b859dd");
	}

	public static byte[] Power(bool on)
	{
		return new byte[1] { on ? ((byte)1) : ((byte)0) };
	}

	public static byte[] Brightness(double percent)
	{
		if (!double.IsFinite(percent))
		{
			throw new ArgumentOutOfRangeException("percent");
		}
		return new byte[1] { (byte)Math.Clamp((int)Math.Round(Math.Clamp(percent, 1.0, 100.0) * 254.0 / 100.0), 1, 254) };
	}

	public static byte[] Temperature(double kelvin)
	{
		if (!double.IsFinite(kelvin) || kelvin <= 0.0)
		{
			throw new ArgumentOutOfRangeException("kelvin");
		}
		ushort value = (ushort)Math.Clamp((int)Math.Round(1000000.0 / Math.Clamp(kelvin, 2200.0, 6500.0)), 153, 454);
		byte[] array = new byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(array, value);
		return array;
	}

	public static byte[] Xy(double x, double y)
	{
		if (!double.IsFinite(x) || !double.IsFinite(y))
		{
			throw new ArgumentOutOfRangeException("x");
		}
		x = Math.Clamp(x, 0.0, 1.0);
		y = Math.Clamp(y, 0.0, 1.0);
		if (x + y > 1.0)
		{
			double num = x + y;
			x /= num;
			y /= num;
		}
		byte[] array = new byte[4];
		BinaryPrimitives.WriteUInt16LittleEndian(array, (ushort)Math.Round(x * 65535.0));
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(2), (ushort)Math.Round(y * 65535.0));
		return array;
	}

	public static byte[] Rgb(byte r, byte g, byte b)
	{
		double num = Linear(r);
		double num2 = Linear(g);
		double num3 = Linear(b);
		double num4 = 0.4124 * num + 0.3576 * num2 + 0.1805 * num3;
		double num5 = 0.2126 * num + 0.7152 * num2 + 0.0722 * num3;
		double num6 = 0.0193 * num + 0.1192 * num2 + 0.9505 * num3;
		double num7 = num4 + num5 + num6;
		if (num7 != 0.0)
		{
			return Xy(num4 / num7, num5 / num7);
		}
		return Xy(0.3127, 0.329);
		static double Linear(byte value)
		{
			double num8 = (double)(int)value / 255.0;
			if (!(num8 > 0.04045))
			{
				return num8 / 12.92;
			}
			return Math.Pow((num8 + 0.055) / 1.055, 2.4);
		}
	}

	public static int? Kelvin(byte[]? data)
	{
		if (data == null || data.Length != 2)
		{
			return null;
		}
		int num = BinaryPrimitives.ReadUInt16LittleEndian(data);
		if (num != 0 && num != 65535)
		{
			return (int)Math.Round(1000000.0 / (double)num);
		}
		return null;
	}

	public static bool HasFe0fServiceData(byte[] bytes)
	{
		if (bytes.Length >= 2 && bytes[0] == 15)
		{
			return bytes[1] == 254;
		}
		return false;
	}
}
