#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HueStudio;

/// <summary>Bildirim alanı simgesinin ekran dikdörtgeni (Shell_NotifyIconGetRect).</summary>
internal static class TrayIconRect
{
	[StructLayout(LayoutKind.Sequential)]
	public struct RECT { public int Left, Top, Right, Bottom; }

	[StructLayout(LayoutKind.Sequential)]
	private struct NOTIFYICONIDENTIFIER
	{
		public uint cbSize;
		public IntPtr hWnd;
		public uint uID;
		public Guid guidItem;
	}

	[DllImport("shell32.dll")]
	private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

	/// <summary>NotifyIcon'un iç penceresi ve kimliği yansıma ile okunur; bulunamazsa false.</summary>
	public static bool TryGet(NotifyIcon icon, out RECT rect)
	{
		rect = default;
		try
		{
			const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
			FieldInfo? idField = typeof(NotifyIcon).GetField("_id", F) ?? typeof(NotifyIcon).GetField("id", F);
			FieldInfo? winField = typeof(NotifyIcon).GetField("_window", F) ?? typeof(NotifyIcon).GetField("window", F);
			if (idField == null || winField == null) return false;
			if (winField.GetValue(icon) is not NativeWindow window || window.Handle == IntPtr.Zero) return false;
			uint id = Convert.ToUInt32(idField.GetValue(icon));
			NOTIFYICONIDENTIFIER nid = new NOTIFYICONIDENTIFIER
			{
				cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
				hWnd = window.Handle,
				uID = id,
			};
			if (Shell_NotifyIconGetRect(ref nid, out rect) != 0) return false;
			return rect.Right > rect.Left && rect.Bottom > rect.Top;
		}
		catch (Exception)
		{
			return false;
		}
	}
}

/// <summary>
/// Simge üzerinde fare tekerleği: NotifyIcon tekerlek olayı vermez; WH_MOUSE_LL kancası
/// imleç simgenin dikdörtgeni içindeyken tekerleği yakalar. Dikdörtgen okunamazsa kanca kurulmaz.
/// </summary>
internal sealed class TrayWheelHook : IDisposable
{
	private const int WH_MOUSE_LL = 14;
	private const int WM_MOUSEWHEEL = 0x020A;

	[StructLayout(LayoutKind.Sequential)]
	private struct MSLLHOOKSTRUCT
	{
		public int X, Y;
		public uint mouseData, flags, time;
		public IntPtr dwExtraInfo;
	}

	private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);

	private readonly HookProc proc;
	private readonly Func<TrayIconRect.RECT?> rect;
	private readonly Action<int> wheel;
	private IntPtr hook;

	public bool Active => hook != IntPtr.Zero;

	/// <param name="rect">Önbellekteki simge dikdörtgeni (hızlı olmalı; kanca içinde çağrılır).</param>
	/// <param name="wheel">+1 / -1 adım; UI iş parçacığına aktarılmalı.</param>
	public TrayWheelHook(Func<TrayIconRect.RECT?> rect, Action<int> wheel)
	{
		this.rect = rect;
		this.wheel = wheel;
		proc = Callback;
	}

	public bool Install()
	{
		if (hook != IntPtr.Zero) return true;
		using Process p = Process.GetCurrentProcess();
		hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
		return hook != IntPtr.Zero;
	}

	private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
		{
			TrayIconRect.RECT? r = rect();
			if (r.HasValue)
			{
				MSLLHOOKSTRUCT m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				TrayIconRect.RECT v = r.Value;
				if (m.X >= v.Left && m.X < v.Right && m.Y >= v.Top && m.Y < v.Bottom)
				{
					short delta = (short)((m.mouseData >> 16) & 0xFFFF);
					wheel(delta > 0 ? 1 : -1);
					return (IntPtr)1;
				}
			}
		}
		return CallNextHookEx(hook, nCode, wParam, lParam);
	}

	public void Dispose()
	{
		if (hook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(hook);
			hook = IntPtr.Zero;
		}
	}
}
