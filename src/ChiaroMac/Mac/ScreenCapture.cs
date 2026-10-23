#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static HueStudio.MacInterop.Objc;
using static HueStudio.MacInterop.Native;

namespace HueStudio.MacInterop;

public sealed record SckDisplay(uint Id, int Width, int Height, bool Main);

/// <summary>
/// ScreenCaptureKit (macOS 13+) üzerinden sürekli yakalama: küçük ölçekli BGRA kareler (ekran modu) ve/veya
/// sistem sesi tepe değeri (ses modu). Kareler GPU'da hedef boyuta (ör. 128×72) küçültülür, böylece CPU yükü düşüktür.
/// Tüm Objective-C çağrıları libobjc üzerinden; Swift/Xcode gerekmez.
/// </summary>
internal sealed class SckStream : IDisposable
{
	private const nint OutputScreen = 0;
	private const nint OutputAudio = 1;
	private const uint PixelBGRA = 0x42475241; // 'BGRA'

	private static IntPtr outputClass;
	private static readonly ConcurrentDictionary<IntPtr, SckStream> live = new ConcurrentDictionary<IntPtr, SckStream>();
	private static DispatchQueue? sampleQueue;

	private IntPtr stream;
	private IntPtr output;
	private readonly object frameLock = new object();
	private byte[] frame = Array.Empty<byte>();
	private int frameW, frameH, frameStride;
	private long frames;
	private int peakBits;
	private long audioBuffers;

	public string? StopError { get; private set; }
	public long Frames => Interlocked.Read(ref frames);
	public long AudioBuffers => Interlocked.Read(ref audioBuffers);

	public static bool Supported
	{
		get
		{
			if (!OperatingSystem.IsMacOS() || !Load(ScreenCaptureKit)) return false;
			Load(CoreMedia);
			Load(CoreVideo);
			Load(CoreGraphics);
			return objc_getClass("SCStream") != IntPtr.Zero;
		}
	}

	/// <summary>Ekran Kaydı izni (TCC). İstemez; yalnızca durum.</summary>
	public static bool HasPermission()
	{
		try
		{
			return CGPreflightScreenCaptureAccess();
		}
		catch (Exception ex)
		{
			AppLog.Write("Ekran izni okunamadı: " + ex.Message);
			return false;
		}
	}

	/// <summary>İlk seferde sistem iznini sorar (sonraki seferlerde yalnızca durumu döndürür).</summary>
	public static bool RequestPermission()
	{
		try
		{
			return CGRequestScreenCaptureAccess();
		}
		catch (Exception ex)
		{
			AppLog.Write("Ekran izni istenemedi: " + ex.Message);
			return false;
		}
	}

	private static async Task<IntPtr> ShareableContentAsync(CancellationToken ct)
	{
		TaskCompletionSource<IntPtr> tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
		IntPtr pool = Pool();
		try
		{
			IntPtr block = ObjcBlock.ObjectOrError(tcs);
			SendVoid(Cls("SCShareableContent"), Sel("getShareableContentWithCompletionHandler:"), block);
		}
		finally
		{
			Drain(pool);
		}
		Task done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
		ct.ThrowIfCancellationRequested();
		if (done != tcs.Task) throw new TimeoutException("ScreenCaptureKit yanıt vermedi.");
		return await tcs.Task; // retain edilmiş SCShareableContent
	}

	public static async Task<List<SckDisplay>> DisplaysAsync(CancellationToken ct)
	{
		if (!Supported) throw new PlatformNotSupportedException("ScreenCaptureKit yok.");
		IntPtr content = await ShareableContentAsync(ct);
		IntPtr pool = Pool();
		try
		{
			uint main = CGMainDisplayID();
			List<SckDisplay> list = new List<SckDisplay>();
			foreach (IntPtr d in Items(Send(content, Sel("displays"))))
			{
				uint id = SendUIntRet(d, Sel("displayID"));
				list.Add(new SckDisplay(id, (int)SendNInt(d, Sel("width")), (int)SendNInt(d, Sel("height")), id == main));
			}
			// Birincil ekran önce, sonra kimliğe göre (izinsiz CoreGraphics listesiyle aynı sıra).
			return list.OrderBy(x => x.Main ? 0 : 1).ThenBy(x => x.Id).ToList();
		}
		finally
		{
			Drain(pool);
			Release(content);
		}
	}

	/// <summary>Yakalamayı başlatır. source: ekran noktası cinsinden bölge (null = tüm ekran).</summary>
	public static async Task<SckStream> StartAsync(uint displayId, CGRect? source, int outWidth, int outHeight, bool audio, int fps, CancellationToken ct)
	{
		if (!Supported) throw new PlatformNotSupportedException("ScreenCaptureKit yok.");
		IntPtr content = await ShareableContentAsync(ct);
		SckStream s = new SckStream();
		TaskCompletionSource<IntPtr> started = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			s.Create(content, displayId, source, outWidth, outHeight, audio, fps, started);
		}
		catch
		{
			s.Dispose();
			throw;
		}
		finally
		{
			Release(content);
		}
		try
		{
			Task done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
			ct.ThrowIfCancellationRequested();
			if (done != started.Task) throw new TimeoutException("Ekran yakalama başlamadı.");
			await started.Task;
		}
		catch
		{
			s.Dispose();
			throw;
		}
		return s;
	}

	private unsafe void Create(IntPtr content, uint displayId, CGRect? source, int outWidth, int outHeight, bool audio, int fps, TaskCompletionSource<IntPtr> started)
	{
		IntPtr pool = Pool();
		try
		{
			IntPtr display = IntPtr.Zero;
			List<IntPtr> displays = Items(Send(content, Sel("displays")));
			foreach (IntPtr d in displays)
			{
				if (SendUIntRet(d, Sel("displayID")) == displayId) display = d;
			}
			if (display == IntPtr.Zero && displays.Count > 0) display = displays[0];
			if (display == IntPtr.Zero) throw new InvalidOperationException("Ekran bulunamadı.");

			IntPtr filter = Send(Send(Cls("SCContentFilter"), Sel("alloc")), Sel("initWithDisplay:excludingWindows:"), display, NSArray(Array.Empty<IntPtr>()));
			IntPtr cfg = Send(Send(Cls("SCStreamConfiguration"), Sel("alloc")), Sel("init"));
			SendNUIntArg(cfg, Sel("setWidth:"), (nuint)Math.Max(2, outWidth));
			SendNUIntArg(cfg, Sel("setHeight:"), (nuint)Math.Max(2, outHeight));
			SendCMTime(cfg, Sel("setMinimumFrameInterval:"), CMTime.Make(1, Math.Clamp(fps, 1, 60)));
			SendUIntArg(cfg, Sel("setPixelFormat:"), PixelBGRA);
			SendBoolArg(cfg, Sel("setShowsCursor:"), 0);
			SendNIntArg(cfg, Sel("setQueueDepth:"), 3);
			if (source is CGRect r) SendRect(cfg, Sel("setSourceRect:"), r);
			if (audio)
			{
				SendBoolArg(cfg, Sel("setCapturesAudio:"), 1);
				SendBoolArg(cfg, Sel("setExcludesCurrentProcessAudio:"), 1);
				SendNIntArg(cfg, Sel("setSampleRate:"), 48000);
				SendNIntArg(cfg, Sel("setChannelCount:"), 2);
			}

			output = Send(Send(OutputClass(), Sel("alloc")), Sel("init"));
			live[output] = this;
			stream = Send(Send(Cls("SCStream"), Sel("alloc")), Sel("initWithFilter:configuration:delegate:"), filter, cfg, output);
			Release(filter);
			Release(cfg);
			if (stream == IntPtr.Zero) throw new InvalidOperationException("SCStream oluşturulamadı.");
			sampleQueue ??= new DispatchQueue("io.github.onurerensavci.chiaro.capture");
			IntPtr err = IntPtr.Zero;
			if (SendAddOutput(stream, Sel("addStreamOutput:type:sampleHandlerQueue:error:"), output, OutputScreen, sampleQueue.Handle, &err) == 0)
			{
				throw MacError.From(err);
			}
			if (audio)
			{
				err = IntPtr.Zero;
				if (SendAddOutput(stream, Sel("addStreamOutput:type:sampleHandlerQueue:error:"), output, OutputAudio, sampleQueue.Handle, &err) == 0)
				{
					throw MacError.From(err);
				}
			}
			SendVoid(stream, Sel("startCaptureWithCompletionHandler:"), ObjcBlock.ErrorOnly(started));
		}
		finally
		{
			Drain(pool);
		}
	}

	/// <summary>Son karenin kopyası (BGRA). Kare yoksa false.</summary>
	public bool TryCopyFrame(out byte[] pixels, out int width, out int height, out int stride)
	{
		lock (frameLock)
		{
			pixels = frame;
			width = frameW;
			height = frameH;
			stride = frameStride;
			return frameW > 0 && frameH > 0;
		}
	}

	/// <summary>Son okumadan bu yana en yüksek ses tepe değeri (0..1) ve sıfırlar.</summary>
	public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peakBits, 0));

	private void OnScreen(IntPtr sampleBuffer)
	{
		if (sampleBuffer == IntPtr.Zero || !CMSampleBufferIsValid(sampleBuffer)) return;
		IntPtr pb = CMSampleBufferGetImageBuffer(sampleBuffer);
		if (pb == IntPtr.Zero) return; // boş/değişmeyen kare
		if (CVPixelBufferLockBaseAddress(pb, 1) != 0) return;
		try
		{
			if (CVPixelBufferGetPixelFormatType(pb) != PixelBGRA) return;
			IntPtr basePtr = CVPixelBufferGetBaseAddress(pb);
			int w = (int)CVPixelBufferGetWidth(pb), h = (int)CVPixelBufferGetHeight(pb), stride = (int)CVPixelBufferGetBytesPerRow(pb);
			if (basePtr == IntPtr.Zero || w <= 0 || h <= 0 || stride < w * 4 || (long)stride * h > 8_000_000) return;
			byte[] copy = new byte[stride * h];
			Marshal.Copy(basePtr, copy, 0, copy.Length);
			lock (frameLock)
			{
				frame = copy;
				frameW = w;
				frameH = h;
				frameStride = stride;
			}
			Interlocked.Increment(ref frames);
		}
		finally
		{
			CVPixelBufferUnlockBaseAddress(pb, 1);
		}
	}

	private unsafe void OnAudio(IntPtr sampleBuffer)
	{
		if (sampleBuffer == IntPtr.Zero || !CMSampleBufferIsValid(sampleBuffer)) return;
		IntPtr fmt = CMSampleBufferGetFormatDescription(sampleBuffer);
		AudioStreamBasicDescription* asbd = fmt == IntPtr.Zero ? null : CMAudioFormatDescriptionGetStreamBasicDescription(fmt);
		bool isFloat = asbd == null || (asbd->FormatFlags & 1) != 0;
		int bits = asbd == null ? 32 : (int)asbd->BitsPerChannel;
		IntPtr bb = CMSampleBufferGetDataBuffer(sampleBuffer);
		if (bb == IntPtr.Zero) return;
		nuint offset = 0, total = 0;
		float peak = 0;
		do
		{
			nuint len;
			IntPtr data;
			if (CMBlockBufferGetDataPointer(bb, offset, &len, &total, &data) != 0 || len == 0) break;
			peak = Math.Max(peak, AudioMath.Peak(new ReadOnlySpan<byte>((void*)data, (int)len), isFloat, bits));
			offset += len;
		}
		while (offset < total);
		Interlocked.Increment(ref audioBuffers);
		int cur;
		do
		{
			cur = Volatile.Read(ref peakBits);
			if (BitConverter.Int32BitsToSingle(cur) >= peak) break;
		}
		while (Interlocked.CompareExchange(ref peakBits, BitConverter.SingleToInt32Bits(peak), cur) != cur);
	}

	internal static unsafe IntPtr OutputClass()
	{
		if (outputClass != IntPtr.Zero) return outputClass;
		IntPtr existing = objc_getClass("ChiaroCaptureOutput");
		if (existing != IntPtr.Zero) return outputClass = existing;
		IntPtr cls = objc_allocateClassPair(Cls("NSObject"), "ChiaroCaptureOutput", 0);
		if (!class_addMethod(cls, Sel("stream:didOutputSampleBuffer:ofType:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, void>)&DidOutput, "v@:@^vq")
			|| !class_addMethod(cls, Sel("stream:didStopWithError:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidStop, "v@:@@"))
		{
			throw new InvalidOperationException("Yakalama sınıfı oluşturulamadı.");
		}
		foreach (string p in new[] { "SCStreamOutput", "SCStreamDelegate" })
		{
			IntPtr proto = objc_getProtocol(p);
			if (proto != IntPtr.Zero) class_addProtocol(cls, proto);
		}
		objc_registerClassPair(cls);
		return outputClass = cls;
	}

	[UnmanagedCallersOnly]
	private static void DidOutput(IntPtr self, IntPtr cmd, IntPtr stream, IntPtr sampleBuffer, nint type)
	{
		try
		{
			if (!live.TryGetValue(self, out SckStream? s)) return;
			if (type == OutputScreen) s.OnScreen(sampleBuffer);
			else if (type == OutputAudio) s.OnAudio(sampleBuffer);
		}
		catch (Exception ex)
		{
			AppLog.Write("Yakalama karesi: " + ex.Message);
		}
	}

	[UnmanagedCallersOnly]
	private static void DidStop(IntPtr self, IntPtr cmd, IntPtr stream, IntPtr error)
	{
		try
		{
			if (!live.TryGetValue(self, out SckStream? s)) return;
			s.StopError = MacError.From(error).Message;
			AppLog.Write("Ekran yakalama durdu: " + s.StopError);
		}
		catch (Exception ex)
		{
			AppLog.Write("Yakalama durdu: " + ex.Message);
		}
	}

	/// <summary>
	/// Durdurmayı iş parçacığı havuzunda yapar: stopCapture tamamlanmasını beklemek (en çok 2 sn) arayüzü dondurmasın.
	/// </summary>
	public static Task StopAsync(SckStream s) => Task.Run(() =>
	{
		try
		{
			s.Dispose();
		}
		catch (Exception ex)
		{
			AppLog.Write("Yakalama durdurma: " + ex.Message);
		}
	});

	public void Dispose()
	{
		IntPtr st = Interlocked.Exchange(ref stream, IntPtr.Zero);
		IntPtr outp = output;
		bool stoppedCleanly = true;
		if (st != IntPtr.Zero)
		{
			IntPtr pool = Pool();
			try
			{
				TaskCompletionSource<IntPtr> stopped = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
				SendVoid(st, Sel("stopCaptureWithCompletionHandler:"), ObjcBlock.ErrorOnly(stopped));
				// Durdurma eşzamansız; kısa bekleriz ki son kareler serbest bırakılmış nesneye gelmesin.
				stoppedCleanly = stopped.Task.Wait(TimeSpan.FromSeconds(2));
			}
			catch (Exception ex)
			{
				AppLog.Write("Yakalama durdurma: " + (ex.InnerException?.Message ?? ex.Message));
			}
			finally
			{
				Drain(pool);
			}
			Release(st);
		}
		if (outp != IntPtr.Zero)
		{
			live.TryRemove(outp, out _);
			output = IntPtr.Zero;
			// Durdurma 2 sn'de onaylanmadıysa geç gelen bir kare serbest bırakılmış nesneye gitmesin: küçük çıktı nesnesi bilerek tutulur.
			if (stoppedCleanly) Release(outp);
			else AppLog.Write("Ekran yakalama durdurması onaylanmadı; çıktı nesnesi serbest bırakılmadı.");
		}
	}
}
