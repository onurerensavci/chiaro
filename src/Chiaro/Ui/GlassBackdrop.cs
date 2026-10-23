#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HueStudio;

/// <summary>
/// Windows 11 (22H2+, build 22621) üzerinde gerçek sistem bulanıklığı (Acrylic) uygular.
/// Windows 10'da veya başarısız olursa false döner; uygulama opak cam görünümüne düşer.
/// </summary>
internal static class GlassBackdrop
{
	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
	private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
	private const int DWMSBT_NONE = 1;
	private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int Left, Right, Top, Bottom;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

	public static bool Supported => Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Build >= 22621;

	public static void SetDarkTitle(Window window, bool dark)
	{
		nint hwnd = new WindowInteropHelper(window).Handle;
		if (hwnd == 0) return;
		int v = dark ? 1 : 0;
		try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4); } catch (Exception) { }
	}

	public static bool Apply(Window window, bool enable)
	{
		nint hwnd = new WindowInteropHelper(window).Handle;
		if (hwnd == 0 || !Supported) return false;
		try
		{
			HwndSource? source = HwndSource.FromHwnd(hwnd);
			if (source?.CompositionTarget == null) return false;
			if (enable)
			{
				source.CompositionTarget.BackgroundColor = Colors.Transparent;
				Margins m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
				DwmExtendFrameIntoClientArea(hwnd, ref m);
				int type = DWMSBT_TRANSIENTWINDOW;
				return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) == 0;
			}
			else
			{
				int type = DWMSBT_NONE;
				DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4);
				Margins m = new Margins();
				DwmExtendFrameIntoClientArea(hwnd, ref m);
				source.CompositionTarget.BackgroundColor = SystemColors.WindowColor;
				return false;
			}
		}
		catch (Exception)
		{
			return false;
		}
	}
}
