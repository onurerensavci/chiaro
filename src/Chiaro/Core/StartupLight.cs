#nullable enable
using System;
using System.Linq;
using System.Reflection;

namespace HueStudio;

/// <summary>
/// "Bilgisayar açılınca ışığı aç": yalnızca uygulama otomatik başlatmayla açıldığında (Windows: Run kaydındaki --tray,
/// macOS: LaunchAgent'taki --background) ve seçenek açıksa çalışır. Elle açılışta, güncelleme sonrası yeniden açılışta
/// (--updated) ya da uykudan uyanınca hiçbir şey yapmaz. Bluetooth oturum açılışında hemen hazır olmayabileceği için
/// son ampule artan aralıklarla yaklaşık 60 saniye boyunca yeniden denenir.
/// </summary>
public static class StartupLight
{
	/// <summary>Yeni deneme başlatılabilecek toplam süre.</summary>
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

	/// <summary>Denemeler arası bekleme (saniye); son değer tekrarlanır.</summary>
	public static readonly int[] DelaysSeconds = { 2, 4, 8, 15 };

	public static bool ShouldRun(bool autostartLaunch, bool optionOn, bool startupEnabled) => autostartLaunch && optionOn && startupEnabled;

	/// <summary>attempt (0'dan) başarısız olduktan sonraki bekleme. Son deneme pencerenin sonuna (~60 sn) denk gelecek şekilde
	/// kısaltılır; pencere dolduysa null (vazgeç).</summary>
	public static TimeSpan? NextDelay(int attempt, TimeSpan elapsed)
	{
		TimeSpan remaining = Window - elapsed;
		if (remaining < TimeSpan.FromSeconds(1)) return null;
		TimeSpan d = TimeSpan.FromSeconds(DelaysSeconds[Math.Clamp(attempt, 0, DelaysSeconds.Length - 1)]);
		return d < remaining ? d : remaining;
	}

	/// <summary>Bağlandıktan sonra: ışık açılmalı mı, yoksa neden atlanmalı (kayıt için)?</summary>
	public static string? SkipReason(bool userIntervened, string activeMode, bool? powerOn)
	{
		if (userIntervened) return "kullanıcı müdahale etti";
		if (activeMode.Length > 0) return "bir mod zaten çalışıyor (" + activeMode + ")";
		if (powerOn == true) return "ışık zaten açık";
		return null;
	}
}

/// <summary>Deneme yapılarında görünen etiket (ör. "test1"); yayın yapısında boş. Derlemede ChiaroTestTag özelliğinden gelir.</summary>
public static class BuildTag
{
	public static string? Test
	{
		get
		{
			string? v = typeof(BuildTag).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ChiaroTestTag")?.Value;
			return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
		}
	}

	public static string Suffix => Test is string t ? " • " + t : "";
}
