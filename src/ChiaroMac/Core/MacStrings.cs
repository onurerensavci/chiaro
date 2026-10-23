#nullable enable
using System.Collections.Generic;

namespace HueStudio;

/// <summary>
/// macOS'a özgü metinler. Strings.cs (Windows ile ortak) değiştirilmez; aynı anahtar burada varsa Mac metni kullanılır.
/// Tr ve En aynı anahtarlara sahip olmalı (birim testi denetler).
/// </summary>
public static class MacStrings
{
	public static readonly Dictionary<string, string> Tr = new Dictionary<string, string>
	{
		["help_text"] = "1. Ampulü aç ve Mac'e yaklaştır.\n\n2. Telefondaki Hue uygulamasının ampule bağlantısını kapat (ya da telefonun Bluetooth'unu kapat).\n\n3. İlk bağlantıda macOS Bluetooth izni sorar: İzin Ver. Yanlışlıkla reddettiysen: Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth → Chiaro'yu aç.\n\n4. Bağlantı reddedilirse Hue uygulamasında: Ayarlar → Sesli asistanlar → Görünür yap; sonra hemen Bağlan'a bas.\n\n5. macOS eşleştirme isterse onayla. Ampul sıfırlandıysa Sistem Ayarları → Bluetooth'ta ampulü unut ve yeniden tara.\n\n6. Sorun sürerse kayıt dosyasını gönder: ~/Library/Logs/Chiaro/chiaro.log",
		["ble_pairing_weak"] = "Eşleştirme yetersiz. Ampulü Hue uygulamasında görünür yap, sonra yeniden bağlan.",
		["tray_bg_title"] = "Chiaro menü çubuğunda",
		["tray_bg_text"] = "Menü çubuğundaki simgeden açabilirsin.",
		["hdr_hide_tip"] = "Menü çubuğuna gizle",
		["no_bulb_hint"] = "Ampulü yaklaştır, telefondaki Hue bağlantısını kapat.",
		["open_bt_settings_manual"] = "Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth",
		["mac_test_badge"] = "Mac test 1",
		["mac_soon"] = "Yakında",
		["mac_soon_desc"] = "Bu mod Mac test sürümünde henüz yok; sonraki sürümde gelecek.",
		["mac_open_log"] = "Kayıt dosyası",
		["mac_open_log_tip"] = "~/Library/Logs/Chiaro/chiaro.log dosyasını Finder'da göster",
		["mac_bt_privacy"] = "Bluetooth izni",
		["mac_bt_privacy_tip"] = "Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth",
		["mac_bt_unauthorized"] = "Bluetooth izni verilmedi. Sistem Ayarları → Gizlilik ve Güvenlik → Bluetooth'tan Chiaro'ya izin ver.",
		["mac_bt_off"] = "Bluetooth kapalı. Denetim Merkezi'nden aç.",
		["mac_no_le"] = "Bu Mac Bluetooth LE desteklemiyor.",
		["mac_peer_removed"] = "Ampul eski eşleştirmeyi unutmuş. Sistem Ayarları → Bluetooth'ta ampulü unut, sonra yeniden bağlan.",
		["mac_enc_timeout"] = "Şifreleme zaman aşımı. Ampulü Hue uygulamasında görünür yap, sonra yeniden bağlan.",
		["mac_unknown_device"] = "Ampul bu Mac'te bulunamadı. Yeniden tara.",
		["mac_st_connect"] = "Ampule bağlanma",
		["mac_bt_state_unknown"] = "Bluetooth durumu alınamadı. macOS izin penceresini yanıtla ya da Bluetooth'u kapatıp aç.",
		["mac_favorites"] = "Favoriler",
		["mac_menu_about"] = "Chiaro Hakkında",
		["mac_menu_quit"] = "Chiaro'dan Çık",
		["mac_menu_hide"] = "Pencereyi gizle",
		["mac_test_note"] = "Test sürümü • yayınlanmadı",
		["mac_log_saved"] = "Kayıt: {0}",
		["mac_wheel_hint"] = "Sürükle: canlı renk",
		["mac_sleep_custom"] = "Dakika",
		["mac_appearance"] = "Görünüm",
		["mac_lang_restart"] = "Dil hemen değişir.",
		["mac_brightness_pct"] = "Parlaklık {0}",
		["mac_no_scenes_menu"] = "Sahne yok",
	};

	public static readonly Dictionary<string, string> En = new Dictionary<string, string>
	{
		["help_text"] = "1. Turn the bulb on and bring it close to the Mac.\n\n2. Disconnect the Hue app on your phone from the bulb (or turn off the phone's Bluetooth).\n\n3. On first connect macOS asks for Bluetooth permission: Allow. If you denied it by mistake: System Settings → Privacy & Security → Bluetooth → enable Chiaro.\n\n4. If the connection is refused, in the Hue app: Settings → Voice assistants → Make visible; then press Connect right away.\n\n5. Accept if macOS asks to pair. If the bulb was reset, forget it in System Settings → Bluetooth and scan again.\n\n6. If it still fails, send the log file: ~/Library/Logs/Chiaro/chiaro.log",
		["ble_pairing_weak"] = "Pairing is insufficient. Make the bulb visible in the Hue app, then reconnect.",
		["tray_bg_title"] = "Chiaro is in the menu bar",
		["tray_bg_text"] = "Open it from the menu bar icon.",
		["hdr_hide_tip"] = "Hide to the menu bar",
		["no_bulb_hint"] = "Move the bulb closer and disconnect Hue on your phone.",
		["open_bt_settings_manual"] = "System Settings → Privacy & Security → Bluetooth",
		["mac_test_badge"] = "Mac test 1",
		["mac_soon"] = "Coming soon",
		["mac_soon_desc"] = "This mode isn't in the Mac test build yet; it's coming in a later version.",
		["mac_open_log"] = "Log file",
		["mac_open_log_tip"] = "Show ~/Library/Logs/Chiaro/chiaro.log in Finder",
		["mac_bt_privacy"] = "Bluetooth permission",
		["mac_bt_privacy_tip"] = "System Settings → Privacy & Security → Bluetooth",
		["mac_bt_unauthorized"] = "Bluetooth permission not granted. Allow Chiaro in System Settings → Privacy & Security → Bluetooth.",
		["mac_bt_off"] = "Bluetooth is off. Turn it on in Control Center.",
		["mac_no_le"] = "This Mac doesn't support Bluetooth LE.",
		["mac_peer_removed"] = "The bulb forgot the old pairing. Forget the bulb in System Settings → Bluetooth, then reconnect.",
		["mac_enc_timeout"] = "Encryption timed out. Make the bulb visible in the Hue app, then reconnect.",
		["mac_unknown_device"] = "The bulb wasn't found on this Mac. Scan again.",
		["mac_st_connect"] = "Connecting to the bulb",
		["mac_bt_state_unknown"] = "Couldn't get the Bluetooth state. Answer the macOS permission prompt or toggle Bluetooth off and on.",
		["mac_favorites"] = "Favorites",
		["mac_menu_about"] = "About Chiaro",
		["mac_menu_quit"] = "Quit Chiaro",
		["mac_menu_hide"] = "Hide window",
		["mac_test_note"] = "Test build • not published",
		["mac_log_saved"] = "Log: {0}",
		["mac_wheel_hint"] = "Drag: live color",
		["mac_sleep_custom"] = "Minutes",
		["mac_appearance"] = "Appearance",
		["mac_lang_restart"] = "Language changes right away.",
		["mac_brightness_pct"] = "Brightness {0}",
		["mac_no_scenes_menu"] = "No scenes",
	};
}
