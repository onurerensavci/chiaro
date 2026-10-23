#nullable enable
using System;
using System.Text.RegularExpressions;

namespace HueStudio;

/// <summary>
/// Bulunan/hatırlanan ampul. macOS MAC adresi vermez: Id, CoreBluetooth'un bu Mac'e özgü
/// çevre birimi kimliğidir (NSUUID, ör. "6F1C…"). Aynı ampul başka bir Mac'te farklı Id alır.
/// </summary>
public sealed record Lamp(string Id, string Name, bool Known, short? Rssi)
{
	public string Key => string.IsNullOrEmpty(Id) ? Name : Id;

	public string ShortName
	{
		get
		{
			if (Name.Contains("Hue Play", StringComparison.OrdinalIgnoreCase)) return "Hue Play";
			string text = Regex.Replace(Name, "[\\s·_-]+(?:[0-9a-fA-F]{12}|[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5}).*$", "").Trim();
			if (text.Length == 0) return "Hue";
			return text.Substring(0, Math.Min(32, text.Length));
		}
	}

	public string Display => Name == "Hue adayı" ? L.T("hue_candidate") : ShortName;

	/// <summary>Son 4 hane; aynı adlı ampulleri ayırmak için.</summary>
	public string Tail => Key.Replace("-", "").Length > 4 ? Key.Replace("-", "")[^4..] : Key;

	public override string ToString() => Display;
}
