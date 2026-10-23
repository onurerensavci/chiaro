#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace HueStudio.MacInterop;

/// <summary>Ekran/ses/medya için ek objc_msgSend imzaları ve C işlevleri (CoreGraphics, CoreMedia, CoreVideo, AudioToolbox).</summary>
internal static unsafe class Native
{
	private const string ObjcLib = "/usr/lib/libobjc.A.dylib";
	public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	public const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
	public const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
	public const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
	public const string ScreenCaptureKit = "/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit";
	public const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
	public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

	[StructLayout(LayoutKind.Sequential)]
	public struct CMTime
	{
		public long Value;
		public int Timescale;
		public uint Flags;
		public long Epoch;

		public static CMTime Make(long value, int timescale) => new CMTime { Value = value, Timescale = timescale, Flags = 1, Epoch = 0 };
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct CGRect
	{
		public double X, Y, Width, Height;

		public CGRect(double x, double y, double w, double h)
		{
			X = x; Y = y; Width = w; Height = h;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct AudioStreamBasicDescription
	{
		public double SampleRate;
		public uint FormatID;
		public uint FormatFlags;
		public uint BytesPerPacket;
		public uint FramesPerPacket;
		public uint BytesPerFrame;
		public uint ChannelsPerFrame;
		public uint BitsPerChannel;
		public uint Reserved;
	}

	// ---- objc_msgSend ek imzaları
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendNUIntArg(IntPtr r, IntPtr s, nuint v);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendNIntArg(IntPtr r, IntPtr s, nint v);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendUIntArg(IntPtr r, IntPtr s, uint v);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendBoolArg(IntPtr r, IntPtr s, sbyte v);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendCMTime(IntPtr r, IntPtr s, CMTime t);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern void SendRect(IntPtr r, IntPtr s, CGRect rect);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern uint SendUIntRet(IntPtr r, IntPtr s);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern sbyte SendBoolRet0(IntPtr r, IntPtr s);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern nint SendNIntRet1(IntPtr r, IntPtr s, IntPtr a);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern sbyte SendAddOutput(IntPtr r, IntPtr s, IntPtr output, nint type, IntPtr queue, IntPtr* error);
	[DllImport(ObjcLib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendErr(IntPtr r, IntPtr s, IntPtr* error);

	// ---- CoreGraphics
	[DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGPreflightScreenCaptureAccess();
	[DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGRequestScreenCaptureAccess();
	[DllImport(CoreGraphics)] public static extern uint CGMainDisplayID();
	[DllImport(CoreGraphics)] public static extern int CGGetActiveDisplayList(uint maxDisplays, uint* displays, uint* count);
	[DllImport(CoreGraphics)] public static extern nuint CGDisplayPixelsWide(uint display);
	[DllImport(CoreGraphics)] public static extern nuint CGDisplayPixelsHigh(uint display);

	// ---- CoreMedia / CoreVideo
	[DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetImageBuffer(IntPtr sbuf);
	[DllImport(CoreMedia)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CMSampleBufferIsValid(IntPtr sbuf);
	[DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetDataBuffer(IntPtr sbuf);
	[DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetFormatDescription(IntPtr sbuf);
	[DllImport(CoreMedia)] public static extern AudioStreamBasicDescription* CMAudioFormatDescriptionGetStreamBasicDescription(IntPtr desc);
	[DllImport(CoreMedia)] public static extern int CMBlockBufferGetDataPointer(IntPtr buffer, nuint offset, nuint* lengthAtOffset, nuint* totalLength, IntPtr* dataPointer);
	[DllImport(CoreVideo)] public static extern int CVPixelBufferLockBaseAddress(IntPtr pb, ulong flags);
	[DllImport(CoreVideo)] public static extern int CVPixelBufferUnlockBaseAddress(IntPtr pb, ulong flags);
	[DllImport(CoreVideo)] public static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr pb);
	[DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pb);
	[DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetWidth(IntPtr pb);
	[DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetHeight(IntPtr pb);
	[DllImport(CoreVideo)] public static extern uint CVPixelBufferGetPixelFormatType(IntPtr pb);

	// ---- AudioToolbox (mikrofon)
	[DllImport(AudioToolbox)] public static extern int AudioQueueNewInput(AudioStreamBasicDescription* format, delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, uint, IntPtr, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, IntPtr* queue);
	[DllImport(AudioToolbox)] public static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, IntPtr* buffer);
	[DllImport(AudioToolbox)] public static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint numPackets, IntPtr packetDescs);
	[DllImport(AudioToolbox)] public static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
	[DllImport(AudioToolbox)] public static extern int AudioQueueStop(IntPtr queue, byte immediate);
	[DllImport(AudioToolbox)] public static extern int AudioQueueDispose(IntPtr queue, byte immediate);

	private static readonly ConcurrentDictionary<string, bool> loaded = new ConcurrentDictionary<string, bool>();

	public static bool Load(string path)
	{
		return loaded.GetOrAdd(path, p =>
		{
			try
			{
				NativeLibrary.Load(p);
				return true;
			}
			catch (Exception ex)
			{
				AppLog.Write($"Çerçeve yüklenemedi: {System.IO.Path.GetFileName(p)}: {ex.Message}");
				return false;
			}
		});
	}

	public static IntPtr Pool() => Objc.objc_autoreleasePoolPush();

	public static void Drain(IntPtr pool) => Objc.objc_autoreleasePoolPop(pool);
}

/// <summary>
/// Objective-C blok (^) oluşturucu. Tamamlama işleyicisi bekleyen API'ler için (SCShareableContent, SCStream start/stop).
/// Blok "global" bayrağıyla işaretlenir: _Block_copy aynı işaretçiyi döndürür, kopyalama/yok etme yardımcıları gerekmez.
/// Bağlam, bloğun sonuna eklenen bir GCHandle alanında taşınır. Bloğun kendi belleği bilinçli olarak serbest bırakılmaz
/// (çerçeve _Block_release ile bayrağı okuyabilir); tek seferlik çağrılarda çağrı başına ~48 bayt.
/// </summary>
internal static unsafe class ObjcBlock
{
	[StructLayout(LayoutKind.Sequential)]
	private struct Literal
	{
		public IntPtr Isa;
		public int Flags;
		public int Reserved;
		public IntPtr Invoke;
		public IntPtr Descriptor;
		public IntPtr Context;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Descriptor
	{
		public nuint Reserved;
		public nuint Size;
		public IntPtr Signature;
	}

	private const int BlockIsGlobal = 1 << 28;
	private const int BlockHasSignature = 1 << 30;
	private static IntPtr isa;
	private static readonly ConcurrentDictionary<string, IntPtr> descriptors = new ConcurrentDictionary<string, IntPtr>();

	public static IntPtr Create(IntPtr invoke, string signature, object state)
	{
		if (isa == IntPtr.Zero)
		{
			isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
		}
		IntPtr d = descriptors.GetOrAdd(signature, s =>
		{
			IntPtr p = Marshal.AllocHGlobal(sizeof(Descriptor));
			*(Descriptor*)p = new Descriptor { Reserved = 0, Size = (nuint)sizeof(Literal), Signature = Marshal.StringToHGlobalAnsi(s) };
			return p;
		});
		IntPtr b = Marshal.AllocHGlobal(sizeof(Literal));
		*(Literal*)b = new Literal
		{
			Isa = isa,
			Flags = BlockIsGlobal | BlockHasSignature,
			Reserved = 0,
			Invoke = invoke,
			Descriptor = d,
			Context = GCHandle.ToIntPtr(GCHandle.Alloc(state)),
		};
		return b;
	}

	/// <summary>Bağlamı alır ve GCHandle'ı bırakır (tek seferlik bloklar için).</summary>
	public static T? Take<T>(IntPtr block) where T : class
	{
		Literal* l = (Literal*)block;
		if (l->Context == IntPtr.Zero) return null;
		GCHandle h = GCHandle.FromIntPtr(l->Context);
		l->Context = IntPtr.Zero;
		T? t = h.Target as T;
		h.Free();
		return t;
	}

	// ---- Hazır tamamlama blokları: (id nesne, NSError*) ve (NSError*)
	public static IntPtr ObjectOrError(TaskCompletionSource<IntPtr> tcs) => Create((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&InvokeObject, "v24@?0@8@16", tcs);

	public static IntPtr ErrorOnly(TaskCompletionSource<IntPtr> tcs) => Create((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&InvokeError, "v16@?0@8", tcs);

	/// <summary>(BOOL) bloğu; sonuç 1/0 olarak IntPtr'de.</summary>
	public static IntPtr BoolResult(TaskCompletionSource<IntPtr> tcs) => Create((IntPtr)(delegate* unmanaged<IntPtr, sbyte, void>)&InvokeBool, "v12@?0B8", tcs);

	[UnmanagedCallersOnly]
	private static void InvokeBool(IntPtr block, sbyte value)
	{
		try
		{
			Take<TaskCompletionSource<IntPtr>>(block)?.TrySetResult(value != 0 ? (IntPtr)1 : IntPtr.Zero);
		}
		catch (Exception ex)
		{
			AppLog.Write("Blok geri çağrısı: " + ex.Message);
		}
	}

	[UnmanagedCallersOnly]
	private static void InvokeObject(IntPtr block, IntPtr obj, IntPtr error)
	{
		try
		{
			TaskCompletionSource<IntPtr>? tcs = Take<TaskCompletionSource<IntPtr>>(block);
			if (tcs == null) return;
			if (obj != IntPtr.Zero && error == IntPtr.Zero) tcs.TrySetResult(Objc.Retain(obj));
			else tcs.TrySetException(MacError.From(error));
		}
		catch (Exception ex)
		{
			AppLog.Write("Blok geri çağrısı: " + ex.Message);
		}
	}

	[UnmanagedCallersOnly]
	private static void InvokeError(IntPtr block, IntPtr error)
	{
		try
		{
			TaskCompletionSource<IntPtr>? tcs = Take<TaskCompletionSource<IntPtr>>(block);
			if (tcs == null) return;
			if (error == IntPtr.Zero) tcs.TrySetResult(IntPtr.Zero);
			else tcs.TrySetException(MacError.From(error));
		}
		catch (Exception ex)
		{
			AppLog.Write("Blok geri çağrısı: " + ex.Message);
		}
	}
}

/// <summary>NSError → .NET özel durumu (alan + kod korunur).</summary>
public sealed class MacError : Exception
{
	public string Domain { get; }
	public long Code { get; }

	public MacError(string domain, long code, string message) : base(message)
	{
		Domain = domain;
		Code = code;
	}

	internal static MacError From(IntPtr nsError)
	{
		if (nsError == IntPtr.Zero) return new MacError("", 0, "Bilinmeyen hata");
		(string? d, long c, string? m) = Objc.Error(nsError);
		return new MacError(d ?? "", c, $"{d} {c}: {m}");
	}

	/// <summary>SCStreamErrorDomain -3801 (userDeclined) ya da TCC reddi.</summary>
	public bool IsPermission => Domain.Contains("ScreenCaptureKit", StringComparison.OrdinalIgnoreCase) && Code is -3801 or -3803
		|| Domain == "com.apple.ScreenCaptureKit.SCStreamErrorDomain" && Code == -3801;
}
