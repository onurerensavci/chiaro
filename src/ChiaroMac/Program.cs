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
		typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "3.4.0";

	[STAThread]
	public static int Main(string[] args)
	{
		AppLog.Write($"=== Chiaro {Version} başlıyor • {RuntimeInformation.OSDescription} • {RuntimeInformation.ProcessArchitecture} • .NET {Environment.Version}");
		AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("İŞLENMEMİŞ HATA: " + e.ExceptionObject);
		System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write("Gözlenmemiş görev hatası: " + e.Exception); e.SetObserved(); };
		if (args.Contains("--selftest"))
		{
			return SelfTest.Run();
		}
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

	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.With(new MacOSPlatformOptions { ShowInDock = true })
			.With(new X11PlatformOptions { EnableIme = false })
			.LogToTrace();
}
