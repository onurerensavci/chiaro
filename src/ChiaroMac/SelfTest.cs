#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HueStudio.MacInterop;
using static HueStudio.MacInterop.Objc;

namespace HueStudio;

/// <summary>
/// Terminalden: Chiaro.app/Contents/MacOS/Chiaro --selftest  (Objective-C köprüsü, CBUUID, temsilci sınıfı, Info.plist)
///              Chiaro.app/Contents/MacOS/Chiaro --selftest-ble (ek olarak: Bluetooth izni/durumu + 8 sn tarama)
///              Chiaro.app/Contents/MacOS/Chiaro --selftest-capture (ek olarak: ekran karesi, sistem sesi, çalan parça)
/// Sonuçlar ekrana ve ~/Library/Logs/Chiaro/chiaro.log dosyasına yazılır.
/// </summary>
internal static class SelfTest
{
	private static int failures;

	private static void Check(string name, Func<bool> test)
	{
		bool ok;
		string detail = "";
		try
		{
			ok = test();
		}
		catch (Exception ex)
		{
			ok = false;
			detail = " — " + ex.GetType().Name + ": " + ex.Message;
		}
		if (!ok) failures++;
		string line = (ok ? "OK   " : "HATA ") + name + detail;
		Console.WriteLine(line);
		AppLog.Write("selftest: " + line);
	}

	/// <summary>--selftest-capture: bir ekran karesi (ortalama renk), 3 sn sistem sesi tepe değeri ve çalan parça.</summary>
	private static void CaptureTest()
	{
		try
		{
			using MacScreenSource screen = new MacScreenSource();
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			string[] names = screen.MonitorNamesAsync(cts.Token).GetAwaiter().GetResult();
			Console.WriteLine("OK   Ekranlar: " + string.Join(" | ", names));
			var c = screen.ReadAsync(0, 0, 0, false, cts.Token).GetAwaiter().GetResult();
			Console.WriteLine($"OK   Ekran rengi: #{c.R:X2}{c.G:X2}{c.B:X2} parlaklık {c.Luma:F2}");
		}
		catch (Exception ex)
		{
			failures++;
			Console.WriteLine("HATA Ekran yakalama: " + ex.Message);
		}
		try
		{
			using MacSystemAudio audio = new MacSystemAudio();
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			audio.StartAsync(cts.Token).GetAwaiter().GetResult();
			float peak = 0;
			for (int i = 0; i < 30; i++)
			{
				Thread.Sleep(100);
				peak = Math.Max(peak, audio.Peak());
			}
			Console.WriteLine($"OK   Sistem sesi tepe: {peak:F3} (müzik çalıyorsa > 0)");
		}
		catch (Exception ex)
		{
			failures++;
			Console.WriteLine("HATA Sistem sesi: " + ex.Message);
		}
		try
		{
			NowPlayingTrack? t = new MacNowPlaying().ReadAsync(false, CancellationToken.None).GetAwaiter().GetResult();
			Console.WriteLine("OK   Çalan: " + (t == null ? "yok" : $"{t.Title} — {t.Artist} ({t.App}, {(t.Playing ? "çalıyor" : "duraklatıldı")}, kapak {(t.Color is { } col ? $"#{col.R:X2}{col.G:X2}{col.B:X2}" : "—")})"));
		}
		catch (Exception ex)
		{
			Console.WriteLine("BİLGİ Çalan parça: " + ex.Message);
		}
	}

	public static int Run()
	{
		bool ble = Environment.GetCommandLineArgs().Contains("--selftest-ble");
		Console.WriteLine($"Chiaro {Program.Version} öz-sınama • {RuntimeInformation.OSDescription} • {RuntimeInformation.ProcessArchitecture}");
		Check("Protokol: parlaklık %72 → B7", () => Protocol.Brightness(72)[0] == 0xB7);
		Check("Protokol: 3000 K → 4D01", () => Convert.ToHexString(Protocol.Temperature(3000)) == "4D01");
		if (!OperatingSystem.IsMacOS())
		{
			Console.WriteLine("macOS değil: Objective-C sınamaları atlandı.");
			return failures == 0 ? 0 : 1;
		}
		LoadFrameworks();
		IntPtr pool = objc_autoreleasePoolPush();
		try
		{
			Check("NSString gidiş-dönüş (UTF-8, Türkçe)", () => ToStr(NSString("Chiaro ışık ğüşiöç ✓")) == "Chiaro ışık ğüşiöç ✓");
			Check("NSArray", () =>
			{
				IntPtr a = NSArray(new[] { NSString("a"), NSString("b") });
				List<IntPtr> items = Items(a);
				return items.Count == 2 && ToStr(items[1]) == "b";
			});
			Check("NSData gidiş-dönüş", () => Bytes(NSData(new byte[] { 1, 0xB7, 0x4D, 0x01 })).SequenceEqual(new byte[] { 1, 0xB7, 0x4D, 0x01 }));
			Check("NSLocale tercih edilen dil", () => { string? l = PreferredLanguage(); Console.WriteLine("     dil: " + l + " → arayüz: " + L.Resolve("auto")); return l != null; });
			IntPtr cb = NativeLibrary.Load("/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth");
			Check("CoreBluetooth yüklendi", () => cb != IntPtr.Zero && objc_getClass("CBCentralManager") != IntPtr.Zero);
			Check("CBUUID 128-bit gidiş-dönüş (0002 güç)", () => CoreBluetoothBackend.FromCBUUID(CoreBluetoothBackend.CBUUID(Protocol.Characteristic(2))) == Protocol.Characteristic(2));
			Check("CBUUID 16-bit gidiş-dönüş (FE0F)", () => CoreBluetoothBackend.FromCBUUID(CoreBluetoothBackend.CBUUID(Protocol.AdvertisedService)) == Protocol.AdvertisedService);
			Check("Reklam anahtarı sabitleri", () => ToStr(ExportedString(cb, "CBAdvertisementDataLocalNameKey", "x")) is string k && k.Length > 1);
			Check("Temsilci sınıfı + yöntemler", () =>
			{
				IntPtr cls = CoreBluetoothBackend.DelegateClass();
				IntPtr obj = Send(Send(cls, Sel("alloc")), Sel("init"));
				string[] sels =
				{
					"centralManagerDidUpdateState:", "centralManager:didDiscoverPeripheral:advertisementData:RSSI:", "centralManager:didConnectPeripheral:",
					"centralManager:didFailToConnectPeripheral:error:", "centralManager:didDisconnectPeripheral:error:", "peripheral:didDiscoverServices:",
					"peripheral:didDiscoverCharacteristicsForService:error:", "peripheral:didUpdateValueForCharacteristic:error:", "peripheral:didWriteValueForCharacteristic:error:",
				};
				bool all = sels.All(s => SendBoolRet(obj, Sel("respondsToSelector:"), Sel(s)) != 0);
				Release(obj);
				return all;
			});
			Check("GCD kuyruğu", () => new DispatchQueue("io.github.onurerensavci.chiaro.selftest").Run(() => DispatchQueue.OnQueue));
			Check("Info.plist: NSBluetoothAlwaysUsageDescription", () =>
			{
				IntPtr bundle = Send(Cls("NSBundle"), Sel("mainBundle"));
				string? v = ToStr(Send(bundle, Sel("objectForInfoDictionaryKey:"), NSString("NSBluetoothAlwaysUsageDescription")));
				string? id = ToStr(Send(bundle, Sel("bundleIdentifier")));
				Console.WriteLine("     paket: " + id);
				return !string.IsNullOrEmpty(v);
			});
			foreach (string key in new[] { "NSScreenCaptureUsageDescription", "NSMicrophoneUsageDescription", "NSAppleEventsUsageDescription" })
			{
				Check("Info.plist: " + key, () =>
				{
					IntPtr bundle = Send(Cls("NSBundle"), Sel("mainBundle"));
					return !string.IsNullOrEmpty(ToStr(Send(bundle, Sel("objectForInfoDictionaryKey:"), NSString(key))));
				});
			}
			Check("ScreenCaptureKit (SCStream) yüklendi", () => SckStream.Supported);
			Check("Yakalama çıktı sınıfı + yöntemler", () =>
			{
				IntPtr obj = Send(Send(SckStream.OutputClass(), Sel("alloc")), Sel("init"));
				bool all = new[] { "stream:didOutputSampleBuffer:ofType:", "stream:didStopWithError:" }.All(s => SendBoolRet(obj, Sel("respondsToSelector:"), Sel(s)) != 0);
				Release(obj);
				return all;
			});
			Check("Objective-C blok nesnesi", () => Send(ObjcBlock.ErrorOnly(new TaskCompletionSource<IntPtr>()), Sel("class")) != IntPtr.Zero);
			Check("osascript mevcut", () => File.Exists("/usr/bin/osascript"));
			Console.WriteLine("     Ekran Kaydı izni: " + (SckStream.HasPermission() ? "var" : "yok") + " • Mikrofon: " + Privacy.MicrophoneStatus());
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
		if (Environment.GetCommandLineArgs().Contains("--selftest-capture")) CaptureTest();
		if (ble)
		{
			Console.WriteLine("Bluetooth: durum bekleniyor (izin penceresi çıkabilir)…");
			CoreBluetoothBackend backend = new CoreBluetoothBackend(m => { Console.WriteLine("     " + m); AppLog.Write(m); });
			try
			{
				string status = backend.AdapterStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
				Console.WriteLine("OK   " + status);
				HashSet<string> seen = new HashSet<string>();
				backend.ScanAsync(a =>
				{
					lock (seen)
					{
						if (!seen.Add(a.Id)) return;
					}
					bool hue = HueGattTransport.IsHueCandidate(a);
					Console.WriteLine($"     {(hue ? "HUE " : "    ")} {a.Name ?? "?"} …{HueGattTransport.Tail(a.Id)} RSSI={a.Rssi} servis={string.Join(",", a.ServiceUuids.Select(BleUuid.ToCoreBluetooth))}");
				}, TimeSpan.FromSeconds(8), CancellationToken.None).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				failures++;
				Console.WriteLine("HATA Bluetooth: " + ex.Message);
			}
		}
		Console.WriteLine(failures == 0 ? "Tümü geçti." : $"{failures} sınama başarısız.");
		Console.WriteLine("Kayıt: " + AppLog.FilePath);
		return failures == 0 ? 0 : 1;
	}
}
