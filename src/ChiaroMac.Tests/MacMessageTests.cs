using HueStudio;
using Xunit;

namespace ChiaroMac.Tests;

/// <summary>Bluetooth / yakalama katmanının kullanıcıya ulaşan Türkçe iletileri İngilizce modda çevrilmeli.</summary>
[Collection("Loc")]
public class MacMessageTests
{
	public static IEnumerable<object[]> Messages() => new[]
	{
		"Bluetooth adaptörü kontrolü: Bluetooth hazır değil (PoweredOff).",
		"Ampule bağlanma: Ampule ulaşılamıyor. Açık ve yakında mı? (CBErrorDomain 6)",
		"Ampule bağlanma: Ampule ulaşılamıyor. Açık ve yakında mı? (NotConnected)",
		"Hue servislerini okuma: Bu cihaz Hue Bluetooth kontrolünü desteklemiyor.",
		"Hue kontrollerini okuma: Ampul aç/kapat komutunu desteklemiyor.",
		"Hue kontrollerini okuma: Güç durumu okunamadı.",
		"Ampulün güç durumunu okuma: Ampul yazılımı desteklenmiyor.",
		"Ampulün güç durumunu okuma: Eşleştirme yetersiz. Ampulü Hue uygulamasında görünür yap, sonra yeniden bağlan. (ATT 05)",
		"Model ve yazılım bilgisini okuma: Bağlanılamadı (TimeoutException).",
		"Ampule bağlanma: Bluetooth erişimi reddedildi.",
		"Komut: Ampul bu değeri kabul etmedi. (ATT 13)",
		"Komut: Ampul reddetti. Yeniden bağlan. (ATT 0E)",
		"Durum okuma: Ampule ulaşılamıyor. Açık ve yakında mı? (NotConnected)",
		"Bu ampulde bu kontrol yok.",
		"Model / yazılım bilgisi alınamadı",
		"Bluetooth durumu alınamadı. macOS izin penceresini yanıtla ya da Bluetooth'u kapatıp aç.",
		"Ses çıkışı okunamadı.",
		"Mikrofon okunamadı.",
		"ScreenCaptureKit yanıt vermedi.",
		"Ekran yakalama başlamadı.",
		"İndirilen dosyanın SHA-256 değeri tutmuyor.",
		"Önce Chiaro'yu Uygulamalar klasörüne taşı, sonra yeniden aç.",
		"Uygulama paketi bulunamadı.",
		MacStrings.Tr["mac_perm_screen"],
		MacStrings.Tr["mac_perm_mic"],
		MacStrings.Tr["mac_perm_auto_spotify"],
		MacStrings.Tr["mac_perm_auto_music"],
	}.Select(s => new object[] { s });

	[Theory]
	[MemberData(nameof(Messages))]
	public void Translates(string tr)
	{
		try
		{
			L.Apply("en");
			Assert.DoesNotMatch("[ğüşıöçĞÜŞİÖÇ]", L.Message(tr));
		}
		finally
		{
			L.Apply("tr");
		}
	}
}
