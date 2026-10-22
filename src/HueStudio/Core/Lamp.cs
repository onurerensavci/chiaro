#nullable enable annotations
#nullable disable warnings
using System;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;

namespace HueStudio;

public sealed record Lamp(ulong Address, BluetoothAddressType AddressType, string Name, string? DeviceId, bool Paired, short? Rssi)
{
	public string Key
	{
		get
		{
			string text;
			if (Address == 0L)
			{
				text = DeviceId;
				if (text == null)
				{
					return Name;
				}
			}
			else
			{
				text = Address.ToString("X12");
			}
			return text;
		}
	}

	public string ShortName
	{
		get
		{
			if (!Name.Contains("Hue Play", StringComparison.OrdinalIgnoreCase))
			{
				string text = Regex.Replace(Name, "[\\s·_-]+(?:[0-9a-fA-F]{12}|[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5}).*$", "").Trim();
				if (text == null || text.Length <= 0)
				{
					return "Hue";
				}
				return text.Substring(0, Math.Min(32, text.Length));
			}
			return "Hue Play";
		}
	}

	public string Display => ShortName;

	/// <summary>Son 4 hex hane; aynı adlı ampulleri ayırmak için.</summary>
	public string Tail => Address != 0L ? Address.ToString("X12")[^4..] : (Key.Length > 4 ? Key[^4..] : Key);

	public override string ToString() => Display;
}
