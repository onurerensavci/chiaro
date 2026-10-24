#nullable enable
using System;
using System.Linq;
using System.Text.Json;

namespace HueStudio;

/// <summary>
/// "Son modu geri aç" için sürekli kaydedilen son etkin mod. Bir mod (ya da mod başlatan bir sahne) başlayınca yazılır,
/// ayarları değişince güncellenir ve kullanıcı modu bilerek durdurunca (Durdur, sabit renk/beyaz, ışığı kapatma, başka
/// bir mod) silinir. Uygulama kapanırken, bilgisayar kapanırken/uyurken, bağlantı koparken ya da çökmede silinmez.
/// </summary>
public sealed class LastModeState
{
	/// <summary>Mod kimliği (Modlar karosu): Ekran, Ses, Spotify, Gökkuşağı, Şömine, Gün ışığı.</summary>
	public string Mode { get; set; } = "";
	/// <summary>Modu bir sahne başlattıysa sahne kimliği (yalnız gösterim/vurgulama için).</summary>
	public string? SceneId { get; set; }
	/// <summary>Modun ayarları (ScreenOptions, AudioOptions… JSON); sahnelerdeki ModeOptions ile aynı biçim.</summary>
	public string? Options { get; set; }
	/// <summary>Mod başlarken parlaklık (%).</summary>
	public double? Brightness { get; set; }
	/// <summary>Mac: Ses modunun kaynağı (0 sistem sesi, 1 mikrofon).</summary>
	public int? AudioSource { get; set; }
	public DateTime SavedAtUtc { get; set; }
}

public enum StartupAction
{
	None,
	PowerOn,
	RestoreMode,
}

public static class LastMode
{
	/// <summary>Geri açılabilen modlar: Modlar kartındaki canlı modlar. Uyku/Uyandırma zamanlayıcıya aittir, geri açılmaz.</summary>
	public static readonly string[] RestorableModes = { "Ekran", "Ses", "Spotify", "Gökkuşağı", "Şömine", "Gün ışığı" };

	public static bool IsRestorable(string? mode) => mode != null && RestorableModes.Contains(mode);

	public static bool IsRestorable(LastModeState? s) => s != null && IsRestorable(s.Mode);

	public static string? OptionsFor(StudioSettings s, string mode) => mode switch
	{
		"Ekran" => JsonSerializer.Serialize(s.Screen),
		"Ses" => JsonSerializer.Serialize(s.Audio),
		"Spotify" => JsonSerializer.Serialize(s.Spotify),
		"Gökkuşağı" => JsonSerializer.Serialize(s.Rainbow),
		"Şömine" => JsonSerializer.Serialize(s.Fire),
		"Gün ışığı" => JsonSerializer.Serialize(s.Daylight),
		_ => null,
	};

	/// <summary>Mod başladı: kaydı yazar (geri açılabilen modlar için). Değiştiyse true.</summary>
	public static bool OnModeStarted(StudioSettings s, string mode, string? sceneId, double? brightness, int? audioSource, DateTime utcNow)
	{
		if (!IsRestorable(mode)) return false;
		s.LastMode = new LastModeState
		{
			Mode = mode,
			SceneId = sceneId,
			Options = OptionsFor(s, mode),
			Brightness = brightness.HasValue ? Math.Clamp(Math.Round(brightness.Value), 1, 100) : null,
			AudioSource = audioSource,
			SavedAtUtc = utcNow,
		};
		return true;
	}

	/// <summary>Çalışan modun ayarları değiştiyse kaydı günceller. Değiştiyse true.</summary>
	public static bool Refresh(StudioSettings s, string runningMode, int? audioSource, DateTime utcNow)
	{
		if (s.LastMode is not LastModeState last || last.Mode != runningMode || !IsRestorable(runningMode)) return false;
		string? options = OptionsFor(s, runningMode);
		if (options == last.Options && audioSource == last.AudioSource) return false;
		last.Options = options;
		last.AudioSource = audioSource;
		last.SavedAtUtc = utcNow;
		return true;
	}

	/// <summary>Mod durdu. Yalnız bilerek durdurulduysa (kapanış/kopma/çökme değil) kayıt silinir. Değiştiyse true.</summary>
	public static bool OnModeStopped(StudioSettings s, string mode, bool deliberate)
	{
		if (!deliberate || s.LastMode == null || s.LastMode.Mode != mode) return false;
		s.LastMode = null;
		return true;
	}

	/// <summary>Mod yokken sabit renk/beyaz verildi ya da ışık kapatıldı: geri açılacak mod kalmaz. Değiştiyse true.</summary>
	public static bool OnStaticChange(StudioSettings s)
	{
		if (s.LastMode == null) return false;
		s.LastMode = null;
		return true;
	}

	/// <summary>
	/// Açılışta ne yapılacak: yalnız otomatik başlatmada ve başlangıç açıkken. Son modu geri açma (kayıtlı mod varsa)
	/// ışığı açmaya göre önceliklidir; mod başlatmak ışığı zaten açar, ikinci bir açma komutu gönderilmez.
	/// </summary>
	public static StartupAction Decide(bool autostartLaunch, bool startupEnabled, bool lightOnStartup, bool restoreLastMode, LastModeState? last)
	{
		if (!autostartLaunch || !startupEnabled) return StartupAction.None;
		if (restoreLastMode && IsRestorable(last)) return StartupAction.RestoreMode;
		return lightOnStartup ? StartupAction.PowerOn : StartupAction.None;
	}

	/// <summary>Mod geri açılamadıysa: "Bilgisayar açılınca ışığı aç" açıksa yalnızca ışık açılır.</summary>
	public static StartupAction AfterFailedRestore(bool lightOnStartup) => lightOnStartup ? StartupAction.PowerOn : StartupAction.None;

	/// <summary>Kayıtlı ekran yoksa (çıkarıldıysa) birincil ekrana (0) düşer. Düştüyse true.</summary>
	public static bool ClampMonitor(ScreenOptions o, int monitorCount)
	{
		if (monitorCount <= 0 || (o.Monitor >= 0 && o.Monitor < monitorCount)) return false;
		o.Monitor = 0;
		return true;
	}
}
