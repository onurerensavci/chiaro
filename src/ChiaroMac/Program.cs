#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace HueStudio;

internal static class Program
{
	public static string Version =>
		typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "3.8.0";

	[STAThread]
	public static int Main(string[] args)
	{
		AppLog.Write($"=== Chiaro {Version} başlıyor • {RuntimeInformation.OSDescription} • {RuntimeInformation.ProcessArchitecture} • .NET {Environment.Version}");
		AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("İŞLENMEMİŞ HATA: " + e.ExceptionObject);
		System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write("Gözlenmemiş görev hatası: " + e.Exception); e.SetObserved(); };
		// --selftest, --selftest-ble ve --selftest-capture: arayüz açmadan iç sınama.
		if (args.Any(a => a.StartsWith("--selftest", StringComparison.Ordinal)))
		{
			return SelfTest.Run();
		}
		using SingleInstance? single = SingleInstance.TryAcquire();
		if (single == null)
		{
			// Zaten açık: ileti gösterme; çalışan kopyadan penceresini öne getirmesini iste ve sessizce kapan.
			bool sent = SingleInstance.SignalFirst();
			AppLog.Write(sent ? "İkinci açılış: Chiaro zaten çalışıyor; açık olan pencere öne getirildi, bu kopya kapanıyor."
				: "İkinci açılış: Chiaro zaten çalışıyor ama yanıt vermiyor; bu kopya sessizce kapanıyor.");
			ActivateOtherInstance();
			return 0;
		}
		// Oturum açılışında (--background) yalnızca menü çubuğu simgesi: Dock simgesi hiç görünmesin (pencere açılınca gösterilir).
		showInDock = !args.Contains(LoginItem.BackgroundArgument);
		try
		{
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
			AppLog.Write("Çıkış.");
			return 0;
		}
		catch (Exception ex)
		{
			AppLog.Write("Başlatma hatası: " + ex);
			throw;
		}
	}

	/// <summary>Çalışan diğer Chiaro'yu öne getirir (yalnızca macOS).</summary>
	private static void ActivateOtherInstance()
	{
		if (!OperatingSystem.IsMacOS()) return;
		try
		{
			MacInterop.Native.Load(MacInterop.Native.AppKit);
			IntPtr pool = MacInterop.Objc.objc_autoreleasePoolPush();
			try
			{
				int self = Environment.ProcessId;
				IntPtr arr = MacInterop.Objc.Send(MacInterop.Objc.Cls("NSRunningApplication"), MacInterop.Objc.Sel("runningApplicationsWithBundleIdentifier:"), MacInterop.Objc.NSString(LoginItem.Label));
				foreach (IntPtr app in MacInterop.Objc.Items(arr))
				{
					if (MacInterop.Objc.SendInt(app, MacInterop.Objc.Sel("processIdentifier")) == self) continue;
					// NSApplicationActivateAllWindows (1) | NSApplicationActivateIgnoringOtherApps (2)
					MacInterop.Native.SendNUIntArg(app, MacInterop.Objc.Sel("activateWithOptions:"), 3);
					break;
				}
			}
			finally
			{
				MacInterop.Objc.objc_autoreleasePoolPop(pool);
			}
		}
		catch (Exception ex)
		{
			AppLog.Write("Diğer kopya öne getirilemedi: " + ex.Message);
		}
	}

	private static bool showInDock = true;

	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.With(new MacOSPlatformOptions { ShowInDock = showInDock })
			.With(new X11PlatformOptions { EnableIme = false })
			.LogToTrace();
}

