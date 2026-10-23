#nullable enable annotations
#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HueStudio;

internal static class AmbientInput
{
	internal sealed class ScreenReader : IDisposable
	{
		private Bitmap? bitmap;

		private Graphics? graphics;

		public (byte R, byte G, byte B) Read()
		{
			Rectangle bounds = (Screen.PrimaryScreen ?? throw new InvalidOperationException("Ekran bulunamadı.")).Bounds;
			if (bitmap == null || bitmap.Size != bounds.Size)
			{
				Dispose();
				bitmap = new Bitmap(bounds.Width, bounds.Height);
				graphics = Graphics.FromImage(bitmap);
			}
			graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
			return Average(bitmap);
		}

		public void Dispose()
		{
			graphics?.Dispose();
			bitmap?.Dispose();
			graphics = null;
			bitmap = null;
		}
	}

	internal sealed class AudioReader : IDisposable
	{
		private readonly List<IMeter> meters = new List<IMeter>();

		private DateTime refreshed;

		public int Count => meters.Count;

		public float Peak()
		{
			if ((DateTime.UtcNow - refreshed).TotalSeconds > 3.0)
			{
				Refresh();
			}
			float num = 0f;
			int num2 = 0;
			foreach (IMeter meter in meters)
			{
				if (meter.GetPeakValue(out var peak) >= 0)
				{
					num = Math.Max(num, peak);
					num2++;
				}
			}
			if (num2 == 0)
			{
				refreshed = DateTime.MinValue;
				throw new InvalidOperationException("Ses çıkışı okunamadı.");
			}
			return Math.Clamp(num, 0f, 1f);
		}

		private void Refresh()
		{
			Dispose();
			IDeviceEnumerator deviceEnumerator = (IDeviceEnumerator)new DeviceEnumerator();
			ICollection devices = null;
			try
			{
				Marshal.ThrowExceptionForHR(deviceEnumerator.EnumAudioEndpoints(0, 1u, out devices));
				Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
				for (uint num = 0u; num < count; num++)
				{
					if (devices.Item(num, out IDevice device) < 0)
					{
						continue;
					}
					try
					{
						Guid iid = typeof(IMeter).GUID;
						if (device.Activate(ref iid, 23u, 0, out object result) >= 0)
						{
							meters.Add((IMeter)result);
						}
					}
					finally
					{
						Marshal.ReleaseComObject(device);
					}
				}
				refreshed = DateTime.UtcNow;
			}
			finally
			{
				if (devices != null)
				{
					Marshal.ReleaseComObject(devices);
				}
				Marshal.ReleaseComObject(deviceEnumerator);
			}
		}

		public void Dispose()
		{
			foreach (IMeter meter in meters)
			{
				Marshal.ReleaseComObject(meter);
			}
			meters.Clear();
		}
	}

	[ComImport]
	[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
	private class DeviceEnumerator
	{
	}

	[ComImport]
	[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDeviceEnumerator
	{
		[PreserveSig]
		int EnumAudioEndpoints(int flow, uint mask, out ICollection devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
	}

	[ComImport]
	[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ICollection
	{
		[PreserveSig]
		int GetCount(out uint count);

		[PreserveSig]
		int Item(uint index, out IDevice device);
	}

	[ComImport]
	[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
	}

	[ComImport]
	[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMeter
	{
		[PreserveSig]
		int GetPeakValue(out float peak);
	}

	public static (byte R, byte G, byte B) ScreenColor()
	{
		using ScreenReader screenReader = new ScreenReader();
		return screenReader.Read();
	}

	internal static (byte R, byte G, byte B) Average(Bitmap bitmap)
	{
		long num = 0L;
		long num2 = 0L;
		long num3 = 0L;
		long num4 = 0L;
		for (int i = 0; i < bitmap.Height; i += Math.Max(1, bitmap.Height / 24))
		{
			for (int j = 0; j < bitmap.Width; j += Math.Max(1, bitmap.Width / 40))
			{
				Color pixel = bitmap.GetPixel(j, i);
				num += pixel.R;
				num2 += pixel.G;
				num3 += pixel.B;
				num4++;
			}
		}
		return (R: (byte)(num / num4), G: (byte)(num2 / num4), B: (byte)(num3 / num4));
	}

	public static float AudioPeak()
	{
		using AudioReader audioReader = new AudioReader();
		return audioReader.Peak();
	}

	internal static double AudioLevel(double peak, double gain)
	{
		return 5.0 + 95.0 * Math.Sqrt(Math.Clamp(peak * gain, 0.0, 1.0));
	}

	/// <summary>En düşük / en yüksek parlaklık ayarlanabilir (varsayılan 5–100).</summary>
	internal static double AudioLevel(double peak, double gain, double min, double max)
	{
		return min + (max - min) * Math.Sqrt(Math.Clamp(peak * gain, 0.0, 1.0));
	}
}
