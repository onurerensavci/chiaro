#nullable enable
using System;

namespace HueStudio;

/// <summary>
/// macOS kapanma bildirimi: NSWorkspaceWillPowerOffNotification (kapat, yeniden başlat ve çıkış yap için gönderilir;
/// normal Çık/⌘Q için gönderilmez). Gözlemci ana kuyrukta, bildirim anında zaman uyumlu çağrılır.
/// </summary>
public static class PowerOffObserver
{
	private static IntPtr token;

	public static bool Register(Action onPowerOff)
	{
		if (!OperatingSystem.IsMacOS() || token != IntPtr.Zero) return false;
		try
		{
			MacInterop.Native.Load(MacInterop.Native.AppKit);
			IntPtr name = IntPtr.Zero;
			try
			{
				IntPtr sym = System.Runtime.InteropServices.NativeLibrary.GetExport(System.Runtime.InteropServices.NativeLibrary.Load(MacInterop.Native.AppKit), "NSWorkspaceWillPowerOffNotification");
				name = System.Runtime.InteropServices.Marshal.ReadIntPtr(sym);
			}
			catch (Exception)
			{
			}
			if (name == IntPtr.Zero) name = MacInterop.Objc.NSString("NSWorkspaceWillPowerOffNotification");
			IntPtr ws = MacInterop.Objc.Send(MacInterop.Objc.Cls("NSWorkspace"), MacInterop.Objc.Sel("sharedWorkspace"));
			IntPtr center = MacInterop.Objc.Send(ws, MacInterop.Objc.Sel("notificationCenter"));
			IntPtr block = MacInterop.ObjcBlock.Repeating(onPowerOff);
			token = MacInterop.Objc.Send(center, MacInterop.Objc.Sel("addObserverForName:object:queue:usingBlock:"), name, IntPtr.Zero, IntPtr.Zero, block);
			token = MacInterop.Objc.Retain(token);
			AppLog.Write("Kapanma bildirimi dinleniyor (NSWorkspaceWillPowerOffNotification).");
			return token != IntPtr.Zero;
		}
		catch (Exception ex)
		{
			AppLog.Write("Kapanma bildirimi dinlenemedi: " + ex.Message);
			return false;
		}
	}
}
