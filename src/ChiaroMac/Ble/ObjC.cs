#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace HueStudio.MacInterop;

/// <summary>
/// Objective-C çalışma zamanına doğrudan P/Invoke (libobjc + libdispatch). Xcode, Swift ya da .NET macOS iş yükü gerekmez;
/// Linux'ta derlenir, yalnızca macOS'ta çalışır. objc_msgSend her imza için ayrı, sabit argümanlı bildirilir
/// (arm64'te objc_msgSend değişken argümanlı çağrılmamalıdır).
/// </summary>
internal static unsafe class Objc
{
	private const string Lib = "/usr/lib/libobjc.A.dylib";
	private const string LibSystem = "/usr/lib/libSystem.B.dylib";

	[DllImport(Lib)] public static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
	[DllImport(Lib)] public static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
	[DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);
	[DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
	[DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
	[DllImport(Lib)] public static extern IntPtr objc_getProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
	[DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
	[DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
	[DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendNUInt(IntPtr receiver, IntPtr selector, IntPtr a, nuint b);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendBool(IntPtr receiver, IntPtr selector, sbyte value);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidWrite(IntPtr receiver, IntPtr selector, IntPtr data, IntPtr characteristic, nint type);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint SendNInt(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nuint SendNUIntRet(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern int SendInt(IntPtr receiver, IntPtr selector);
	[DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern sbyte SendBoolRet(IntPtr receiver, IntPtr selector, IntPtr a);

	[DllImport(LibSystem)] public static extern IntPtr dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr attr);
	[DllImport(LibSystem)] public static extern void dispatch_sync_f(IntPtr queue, IntPtr context, delegate* unmanaged<IntPtr, void> work);

	private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new ConcurrentDictionary<string, IntPtr>();
	private static readonly ConcurrentDictionary<string, IntPtr> Classes = new ConcurrentDictionary<string, IntPtr>();
	private static bool frameworksLoaded;

	public static void LoadFrameworks()
	{
		if (frameworksLoaded) return;
		NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
		frameworksLoaded = true;
	}

	public static IntPtr Sel(string name) => Selectors.GetOrAdd(name, n => sel_registerName(n));

	public static IntPtr Cls(string name)
	{
		IntPtr c = Classes.GetOrAdd(name, n => objc_getClass(n));
		if (c == IntPtr.Zero)
		{
			Classes.TryRemove(name, out _);
			throw new InvalidOperationException("Objective-C sınıfı yok: " + name);
		}
		return c;
	}

	public static IntPtr NSString(string value)
	{
		IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
		try
		{
			return Send(Cls("NSString"), Sel("stringWithUTF8String:"), utf8);
		}
		finally
		{
			Marshal.FreeCoTaskMem(utf8);
		}
	}

	public static string? ToStr(IntPtr nsString)
	{
		if (nsString == IntPtr.Zero) return null;
		IntPtr p = Send(nsString, Sel("UTF8String"));
		return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
	}

	public static IntPtr NSArray(IReadOnlyList<IntPtr> items)
	{
		IntPtr[] arr = new IntPtr[items.Count];
		for (int i = 0; i < arr.Length; i++) arr[i] = items[i];
		fixed (IntPtr* p = arr)
		{
			return SendNUInt(Cls("NSArray"), Sel("arrayWithObjects:count:"), (IntPtr)p, (nuint)arr.Length);
		}
	}

	public static List<IntPtr> Items(IntPtr nsArray)
	{
		List<IntPtr> list = new List<IntPtr>();
		if (nsArray == IntPtr.Zero) return list;
		nuint n = SendNUIntRet(nsArray, Sel("count"));
		for (nuint i = 0; i < n; i++) list.Add(SendIndex(nsArray, Sel("objectAtIndex:"), i));
		return list;
	}

	public static IntPtr Get(IntPtr nsDictionary, IntPtr key) =>
		nsDictionary == IntPtr.Zero || key == IntPtr.Zero ? IntPtr.Zero : Send(nsDictionary, Sel("objectForKey:"), key);

	public static IntPtr Dictionary(IntPtr value, IntPtr key) => Send(Cls("NSDictionary"), Sel("dictionaryWithObject:forKey:"), value, key);

	public static IntPtr Bool(bool v) => SendBool(Cls("NSNumber"), Sel("numberWithBool:"), v ? (sbyte)1 : (sbyte)0);

	public static byte[] Bytes(IntPtr nsData)
	{
		if (nsData == IntPtr.Zero) return Array.Empty<byte>();
		nuint n = SendNUIntRet(nsData, Sel("length"));
		if (n == 0) return Array.Empty<byte>();
		IntPtr p = Send(nsData, Sel("bytes"));
		byte[] b = new byte[(int)n];
		Marshal.Copy(p, b, 0, b.Length);
		return b;
	}

	public static IntPtr NSData(byte[] data)
	{
		fixed (byte* p = data)
		{
			return SendNUInt(Cls("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, (nuint)data.Length);
		}
	}

	public static IntPtr Retain(IntPtr o) => o == IntPtr.Zero ? o : Send(o, Sel("retain"));

	public static void Release(IntPtr o)
	{
		if (o != IntPtr.Zero) SendVoid(o, Sel("release"));
	}

	public static (string? Domain, long Code, string? Description) Error(IntPtr nsError)
	{
		if (nsError == IntPtr.Zero) return (null, 0, null);
		return (ToStr(Send(nsError, Sel("domain"))), SendNInt(nsError, Sel("code")), ToStr(Send(nsError, Sel("localizedDescription"))));
	}

	/// <summary>Framework'ün dışa aktardığı NSString sabiti (ör. CBAdvertisementDataLocalNameKey); yoksa yedek değer.</summary>
	public static IntPtr ExportedString(IntPtr library, string symbol, string fallback)
	{
		try
		{
			if (NativeLibrary.TryGetExport(library, symbol, out IntPtr addr) && addr != IntPtr.Zero)
			{
				IntPtr v = Marshal.ReadIntPtr(addr);
				if (v != IntPtr.Zero) return v;
			}
		}
		catch
		{
		}
		return Retain(NSString(fallback));
	}

	/// <summary>macOS tercih edilen ilk dil (ör. "tr-TR").</summary>
	public static string? PreferredLanguage()
	{
		LoadFrameworks();
		IntPtr pool = objc_autoreleasePoolPush();
		try
		{
			IntPtr langs = Send(Cls("NSLocale"), Sel("preferredLanguages"));
			List<IntPtr> items = Items(langs);
			return items.Count > 0 ? ToStr(items[0]) : null;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}
}

/// <summary>Seri GCD kuyruğu: tüm CoreBluetooth çağrıları ve geri çağrıları bu kuyrukta çalışır.</summary>
internal sealed unsafe class DispatchQueue
{
	[ThreadStatic] private static int depth;

	public IntPtr Handle { get; }

	public DispatchQueue(string label)
	{
		Handle = Objc.dispatch_queue_create(label, IntPtr.Zero);
		if (Handle == IntPtr.Zero) throw new InvalidOperationException("dispatch_queue_create başarısız");
	}

	public static bool OnQueue => depth > 0;

	/// <summary>Geri çağrı başında/sonunda (kuyruk iş parçacığında) çağrılır.</summary>
	public static void Enter() => depth++;

	public static void Exit() => depth--;

	private sealed class Job
	{
		public Action Work = null!;
		public Exception? Error;
	}

	[UnmanagedCallersOnly]
	private static void Trampoline(IntPtr context)
	{
		Job? job = null;
		try
		{
			job = (Job)GCHandle.FromIntPtr(context).Target!;
			depth++;
			IntPtr pool = Objc.objc_autoreleasePoolPush();
			try
			{
				job.Work();
			}
			finally
			{
				Objc.objc_autoreleasePoolPop(pool);
				depth--;
			}
		}
		catch (Exception ex)
		{
			if (job != null) job.Error = ex;
		}
	}

	public void Run(Action work)
	{
		if (depth > 0)
		{
			work();
			return;
		}
		Job job = new Job { Work = work };
		GCHandle h = GCHandle.Alloc(job);
		try
		{
			Objc.dispatch_sync_f(Handle, GCHandle.ToIntPtr(h), &Trampoline);
		}
		finally
		{
			h.Free();
		}
		if (job.Error != null) ExceptionDispatchInfo.Capture(job.Error).Throw();
	}

	public T Run<T>(Func<T> work)
	{
		T result = default!;
		Run(() => { result = work(); });
		return result;
	}
}
