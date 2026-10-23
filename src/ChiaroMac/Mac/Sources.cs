#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HueStudio.MacInterop;
using static HueStudio.MacInterop.Native;

namespace HueStudio;

/// <summary>Ekran modu için renk kaynağı (macOS: ScreenCaptureKit; diğer: demo).</summary>
internal interface IScreenSource : IDisposable
{
	Task<string[]> MonitorNamesAsync(CancellationToken ct);
	Task<(byte R, byte G, byte B, double Luma)> ReadAsync(int monitor, int area, int method, bool ignoreDark, CancellationToken ct);
}

/// <summary>Ses modu için tepe ölçer.</summary>
internal interface IAudioSource : IDisposable
{
	Task StartAsync(CancellationToken ct);
	float Peak();
}

/// <summary>İzin / kullanılabilirlik denetimi yapan hata (arayüzde ayarlar düğmesiyle gösterilir).</summary>
internal sealed class PermissionException : Exception
{
	public string Pane { get; }

	public PermissionException(string message, string pane) : base(message)
	{
		Pane = pane;
	}
}

internal static class Privacy
{
	public const string ScreenPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";
	public const string MicrophonePane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";
	public const string AutomationPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation";

	public static PermissionException ScreenDenied() => new PermissionException(MacStrings.Tr["mac_perm_screen"], ScreenPane);

	public static PermissionException MicDenied() => new PermissionException(MacStrings.Tr["mac_perm_mic"], MicrophonePane);

	public static PermissionException AutomationDenied(string app) => new PermissionException(MacStrings.Tr[app == "Spotify" ? "mac_perm_auto_spotify" : "mac_perm_auto_music"], AutomationPane);

	public static async Task<bool> RequestMicrophoneAsync(CancellationToken ct)
	{
		TaskCompletionSource<IntPtr> tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
		IntPtr pool = Pool();
		try
		{
			Objc.SendVoid(Objc.Cls("AVCaptureDevice"), Objc.Sel("requestAccessForMediaType:completionHandler:"), Objc.NSString("soun"), ObjcBlock.BoolResult(tcs));
		}
		finally
		{
			Drain(pool);
		}
		Task done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2), ct));
		ct.ThrowIfCancellationRequested();
		return done == tcs.Task && tcs.Task.Result != IntPtr.Zero;
	}

	/// <summary>AVCaptureDevice ses yetkisi: 0 belirlenmedi, 1 kısıtlı, 2 reddedildi, 3 izin verildi; okunamazsa -1.</summary>
	public static int MicrophoneStatus()
	{
		if (!OperatingSystem.IsMacOS() || !Load(AVFoundation)) return -1;
		IntPtr pool = Pool();
		try
		{
			return (int)Native.SendNIntRet1(Objc.Cls("AVCaptureDevice"), Objc.Sel("authorizationStatusForMediaType:"), Objc.NSString("soun"));
		}
		catch (Exception ex)
		{
			AppLog.Write("Mikrofon yetkisi okunamadı: " + ex.Message);
			return -1;
		}
		finally
		{
			Drain(pool);
		}
	}
}

/// <summary>macOS ekran kaynağı: seçilen ekran + alan için ScreenCaptureKit akışı açık tutulur.</summary>
internal sealed class MacScreenSource : IScreenSource
{
	private SckStream? stream;
	private string key = "";
	private List<SckDisplay>? displays;
	private DateTime displaysAt;

	public async Task<string[]> MonitorNamesAsync(CancellationToken ct)
	{
		// İzin yokken ScreenCaptureKit'e sormak sistem izin penceresini açar; sekmeyi açmak bunu tetiklemesin.
		if (!SckStream.HasPermission()) return CoreGraphicsNames();
		List<SckDisplay> list = await Displays(ct, force: true);
		return list.Select((d, i) => L.F("monitor_n", i + 1, d.Main ? L.T("primary_suffix") : "", d.Width, d.Height)).ToArray();
	}

	/// <summary>İzin gerektirmeyen ekran listesi (birincil önce; SCK sırasıyla aynı).</summary>
	private static unsafe string[] CoreGraphicsNames()
	{
		uint* ids = stackalloc uint[16];
		uint count = 0;
		if (MacInterop.Native.CGGetActiveDisplayList(16, ids, &count) != 0 || count == 0) return Array.Empty<string>();
		uint main = MacInterop.Native.CGMainDisplayID();
		List<uint> list = new List<uint>();
		for (int i = 0; i < count; i++) list.Add(ids[i]);
		list = list.OrderBy(id => id == main ? 0 : 1).ThenBy(id => id).ToList();
		return list.Select((id, i) => L.F("monitor_n", i + 1, id == main ? L.T("primary_suffix") : "", (long)MacInterop.Native.CGDisplayPixelsWide(id), (long)MacInterop.Native.CGDisplayPixelsHigh(id))).ToArray();
	}

	private async Task<List<SckDisplay>> Displays(CancellationToken ct, bool force = false)
	{
		if (force || displays == null || (DateTime.UtcNow - displaysAt).TotalSeconds > 30)
		{
			displays = await SckStream.DisplaysAsync(ct);
			displaysAt = DateTime.UtcNow;
		}
		return displays;
	}

	public async Task<(byte R, byte G, byte B, double Luma)> ReadAsync(int monitor, int area, int method, bool ignoreDark, CancellationToken ct)
	{
		if (!SckStream.Supported) throw new InvalidOperationException(MacStrings.Tr["mac_no_sck"]);
		if (stream == null && !SckStream.HasPermission())
		{
			SckStream.RequestPermission();
			if (!SckStream.HasPermission()) throw Privacy.ScreenDenied();
		}
		List<SckDisplay> list;
		try
		{
			list = await Displays(ct);
		}
		catch (MacError e)
		{
			AppLog.Write("Ekran listesi: " + e.Message);
			throw Privacy.ScreenDenied();
		}
		if (list.Count == 0) throw new InvalidOperationException(MacStrings.Tr["mac_no_display"]);
		SckDisplay d = list[monitor >= 0 && monitor < list.Count ? monitor : 0];
		string want = $"{d.Id}:{area}:{d.Width}x{d.Height}";
		if (stream != null && stream.StopError != null)
		{
			AppLog.Write("Ekran akışı yeniden başlatılıyor: " + stream.StopError);
			stream.Dispose();
			stream = null;
		}
		if (stream == null || key != want)
		{
			stream?.Dispose();
			stream = null;
			var r = ScreenMath.Region(0, 0, d.Width, d.Height, area);
			(int w, int h) = ScreenMath.OutputSize(r.W, r.H);
			AppLog.Write($"Ekran yakalama: ekran {d.Id} {d.Width}×{d.Height} alan {area} → {w}×{h}");
			try
			{
				stream = await SckStream.StartAsync(d.Id, area == 0 ? null : new CGRect(r.X, r.Y, r.W, r.H), w, h, audio: false, fps: 24, ct);
			}
			catch (MacError e) when (e.IsPermission || !SckStream.HasPermission())
			{
				AppLog.Write("Ekran yakalama izni: " + e.Message);
				throw Privacy.ScreenDenied();
			}
			key = want;
		}
		// İlk kare için kısa bekleme (ekran değişmiyorsa da ilk kare gelir).
		for (int i = 0; i < 60; i++)
		{
			if (stream.TryCopyFrame(out byte[] px, out int fw, out int fh, out int stride))
			{
				return ScreenMath.Analyze(px, fw, fh, stride, method, ignoreDark);
			}
			await Task.Delay(50, ct);
		}
		throw new InvalidOperationException(MacStrings.Tr["mac_screen_failed"]);
	}

	public void Dispose()
	{
		stream?.Dispose();
		stream = null;
	}
}

/// <summary>Sistem sesi: ScreenCaptureKit ses akışı (macOS 13+, Ekran Kaydı izni ister).</summary>
internal sealed class MacSystemAudio : IAudioSource
{
	private SckStream? stream;

	public async Task StartAsync(CancellationToken ct)
	{
		if (!SckStream.Supported) throw new InvalidOperationException(MacStrings.Tr["mac_no_sck_audio"]);
		if (!SckStream.HasPermission())
		{
			SckStream.RequestPermission();
			if (!SckStream.HasPermission()) throw Privacy.ScreenDenied();
		}
		uint main = CGMainDisplayID();
		try
		{
			stream = await SckStream.StartAsync(main, null, 2, 2, audio: true, fps: 1, ct);
		}
		catch (MacError e) when (e.IsPermission || !SckStream.HasPermission())
		{
			AppLog.Write("Sistem sesi izni: " + e.Message);
			throw Privacy.ScreenDenied();
		}
		AppLog.Write("Sistem sesi yakalama başladı.");
	}

	public float Peak()
	{
		if (stream == null) throw new InvalidOperationException("Ses çıkışı okunamadı.");
		if (stream.StopError != null) throw new InvalidOperationException("Ses yakalama durdu: " + stream.StopError);
		return stream.TakePeak();
	}

	public void Dispose()
	{
		stream?.Dispose();
		stream = null;
	}
}

/// <summary>Mikrofon girişi (AudioQueue). Sistem sesi kullanılamadığında seçilebilir yedek.</summary>
internal sealed class MacMicrophone : IAudioSource
{
	private static readonly Dictionary<IntPtr, MacMicrophone> live = new Dictionary<IntPtr, MacMicrophone>();
	private static int nextId = 1;
	private IntPtr queue;
	private IntPtr id;
	private int peakBits;

	public async Task StartAsync(CancellationToken ct)
	{
		if (!Load(AudioToolbox)) throw new InvalidOperationException("AudioToolbox yüklenemedi.");
		int status = Privacy.MicrophoneStatus();
		if (status == 0)
		{
			// İlk kullanım: macOS izin penceresini göster ve yanıtı bekle.
			bool granted = await Privacy.RequestMicrophoneAsync(ct);
			AppLog.Write("Mikrofon izni yanıtı: " + (granted ? "verildi" : "reddedildi"));
			if (!granted) throw Privacy.MicDenied();
		}
		else if (status is 1 or 2)
		{
			throw Privacy.MicDenied();
		}
		Open();
	}

	private unsafe void Open()
	{
		AudioStreamBasicDescription f = new AudioStreamBasicDescription
		{
			SampleRate = 44100, FormatID = 0x6C70636D /* 'lpcm' */, FormatFlags = 1 | 8 /* float | packed */,
			BytesPerPacket = 4, FramesPerPacket = 1, BytesPerFrame = 4, ChannelsPerFrame = 1, BitsPerChannel = 32,
		};
		lock (live)
		{
			id = (IntPtr)nextId++;
			live[id] = this;
		}
		IntPtr q;
		int err = AudioQueueNewInput(&f, &Callback, id, IntPtr.Zero, IntPtr.Zero, 0, &q);
		if (err != 0) throw new InvalidOperationException($"Mikrofon açılamadı (OSStatus {err}).");
		queue = q;
		for (int i = 0; i < 3; i++)
		{
			IntPtr buf;
			if (AudioQueueAllocateBuffer(queue, 4096, &buf) == 0) AudioQueueEnqueueBuffer(queue, buf, 0, IntPtr.Zero);
		}
		err = AudioQueueStart(queue, IntPtr.Zero);
		if (err != 0)
		{
			Dispose();
			throw new InvalidOperationException($"Mikrofon başlatılamadı (OSStatus {err}). Mikrofon izni verildi mi?");
		}
		AppLog.Write("Mikrofon girişi başladı.");
	}

	[UnmanagedCallersOnly]
	private static unsafe void Callback(IntPtr user, IntPtr aq, IntPtr buffer, IntPtr startTime, uint packets, IntPtr descs)
	{
		try
		{
			MacMicrophone? m;
			lock (live) live.TryGetValue(user, out m);
			if (m == null || m.queue == IntPtr.Zero) return;
			IntPtr data = Marshal.ReadIntPtr(buffer, 8);
			int size = Marshal.ReadInt32(buffer, 16);
			if (data != IntPtr.Zero && size > 0)
			{
				float p = AudioMath.Peak(new ReadOnlySpan<byte>((void*)data, size), true, 32);
				int cur;
				do
				{
					cur = Volatile.Read(ref m.peakBits);
					if (BitConverter.Int32BitsToSingle(cur) >= p) break;
				}
				while (Interlocked.CompareExchange(ref m.peakBits, BitConverter.SingleToInt32Bits(p), cur) != cur);
			}
			AudioQueueEnqueueBuffer(aq, buffer, 0, IntPtr.Zero);
		}
		catch (Exception ex)
		{
			AppLog.Write("Mikrofon geri çağrısı: " + ex.Message);
		}
	}

	public float Peak()
	{
		if (queue == IntPtr.Zero) throw new InvalidOperationException("Mikrofon okunamadı.");
		return BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peakBits, 0));
	}

	public void Dispose()
	{
		IntPtr q = Interlocked.Exchange(ref queue, IntPtr.Zero);
		if (q != IntPtr.Zero)
		{
			AudioQueueStop(q, 1);
			AudioQueueDispose(q, 1);
		}
		lock (live) live.Remove(id);
	}
}

/// <summary>macOS dışı (Linux önizleme / testler) için yapay kaynaklar.</summary>
internal sealed class DemoScreenSource : IScreenSource
{
	private readonly DateTime start = DateTime.UtcNow;

	public Task<string[]> MonitorNamesAsync(CancellationToken ct) =>
		Task.FromResult(new[] { L.F("monitor_n", 1, L.T("primary_suffix"), 3024, 1964), L.F("monitor_n", 2, "", 2560, 1440) });

	public Task<(byte R, byte G, byte B, double Luma)> ReadAsync(int monitor, int area, int method, bool ignoreDark, CancellationToken ct)
	{
		double t = (DateTime.UtcNow - start).TotalSeconds;
		var c = ColorMath.FromHsv((200 + 40 * Math.Sin(t / 3) + area * 30) % 360, method == 1 ? 0.85 : 0.6, 0.8);
		return Task.FromResult((c.R, c.G, c.B, 0.45 + 0.1 * Math.Sin(t)));
	}

	public void Dispose()
	{
	}
}

internal sealed class DemoAudioSource : IAudioSource
{
	private readonly DateTime start = DateTime.UtcNow;

	public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

	public float Peak()
	{
		double t = (DateTime.UtcNow - start).TotalSeconds;
		return (float)Math.Clamp(0.08 + 0.07 * Math.Abs(Math.Sin(t * 7.3)) * Math.Abs(Math.Sin(t * 1.1)), 0, 1);
	}

	public void Dispose()
	{
	}
}
