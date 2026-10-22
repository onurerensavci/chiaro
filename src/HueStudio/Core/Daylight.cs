#nullable enable
using System;
using System.Globalization;

namespace HueStudio;

/// <summary>Gün ışığı eğrisi: öğlen serin/parlak, akşam sıcak/loş, gece çok sıcak.</summary>
public static class Daylight
{
	public static bool TryParseTime(string? text, out TimeSpan time)
	{
		time = default;
		if (string.IsNullOrWhiteSpace(text)) return false;
		string t = text.Trim().Replace('.', ':');
		if (TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm", "hhmm" }, CultureInfo.InvariantCulture, out time))
		{
			return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
		}
		return false;
	}

	/// <summary>0 (gece) .. 1 (öğlen) arası gün ışığı oranı.</summary>
	public static double Level(TimeSpan now, TimeSpan wake, TimeSpan sleep)
	{
		double day = (sleep - wake).TotalHours;
		if (day <= 0) day += 24;
		double since = (now - wake).TotalHours;
		if (since < 0) since += 24;
		if (since >= day)
		{
			return 0;
		}
		double p = since / day;
		// Sabah hızlı yükselir, akşam yavaşça iner; öğlen düz bir tepe.
		double rise = Math.Clamp(p / 0.18, 0, 1);
		double fall = Math.Clamp((1 - p) / 0.38, 0, 1);
		double v = Math.Min(Smooth(rise), Smooth(fall));
		return v;
	}

	private static double Smooth(double x) => x * x * (3 - 2 * x);

	public static (double Kelvin, double Brightness) At(DaylightOptions o, DateTime time)
	{
		TimeSpan wake = TryParseTime(o.WakeTime, out var w) ? w : new TimeSpan(7, 0, 0);
		TimeSpan sleep = TryParseTime(o.SleepTime, out var s) ? s : new TimeSpan(23, 0, 0);
		double v = Level(time.TimeOfDay, wake, sleep);
		double minK = Math.Min(o.MinKelvin, o.MaxKelvin), maxK = Math.Max(o.MinKelvin, o.MaxKelvin);
		double minB = Math.Min(o.MinBrightness, o.MaxBrightness), maxB = Math.Max(o.MinBrightness, o.MaxBrightness);
		double k = minK + (maxK - minK) * v;
		// Gece: en sıcak ton, en düşük parlaklığın yarısı (en az %1).
		double b = v <= 0 ? Math.Max(1, minB * 0.5) : minB + (maxB - minB) * v;
		return (Math.Round(k / 50) * 50, Math.Round(b));
	}
}
