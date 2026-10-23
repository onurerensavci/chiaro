#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HueStudio;

/// <summary>
/// Mac kendi kendini güncelleme: doğrulanmış zip'i geçici klasöre açar (sembolik bağlar ve çalıştırma izinleri korunur),
/// paketi denetler, karantina işaretini kaldırır, yeni paketi çalışan .app'in yanına (aynı birime) hazırlar ve küçük,
/// ayrık bir yardımcı betik başlatır. Betik uygulamanın kapanmasını bekler, eski paketi yedeğe alıp yenisini yerine koyar
/// (her ikisi de aynı birimde yeniden adlandırma = atomik), başarısızlıkta yedeği geri yükler ve uygulamayı yeniden açar.
/// Yazılamayan konumlarda (izin yok, salt-okunur birim, App Translocation) çağıran taraf İndirilenler'e indirmeye döner.
/// </summary>
public static class SelfUpdate
{
	public const string BundleId = "io.github.onurerensavci.chiaro";
	public const string StagingSuffix = ".chiaro-new";
	public const string BackupSuffix = ".chiaro-old";

	/// <summary>Bu paket yerinde güncellenebilir mi? Değilse kullanıcıya gösterilecek ileti anahtarını döndürür.</summary>
	public static string? CannotUpdateReason(string? bundle)
	{
		if (bundle == null) return "mac_upd_self_no_bundle";
		if (LoginItem.IsTranslocated(bundle)) return "mac_upd_self_translocated";
		string? parent = Path.GetDirectoryName(bundle.TrimEnd('/'));
		// Yalnızca üst klasör denenir: değiş tokuş aynı klasörde iki yeniden adlandırmadır. Paketin içine deneme dosyası
		// yazılmaz (imzalı paketi ve macOS "Uygulama Yönetimi" korumasını gereksiz yere tetiklememek için).
		if (parent == null || !CanWrite(parent)) return "mac_upd_self_readonly";
		return null;
	}

	/// <summary>Klasörde dosya oluşturup silerek gerçek yazma iznini dener (salt-okunur birim, izin, ACL hepsi yakalanır).</summary>
	public static bool CanWrite(string dir)
	{
		try
		{
			if (!Directory.Exists(dir)) return false;
			string probe = Path.Combine(dir, ".chiaro-write-test-" + Environment.ProcessId);
			using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Zip'i boş bir klasöre açar. macOS'ta ditto (Finder ile aynı), başka yerde unzip; ikisi de sembolik bağları ve izinleri korur.</summary>
	public static void Extract(string zip, string dest)
	{
		if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
		Directory.CreateDirectory(dest);
		if (OperatingSystem.IsMacOS()) Run("/usr/bin/ditto", "-x", "-k", zip, dest);
		else Run("unzip", "-q", zip, "-d", dest);
	}

	/// <summary>Açılan klasörde tek bir Chiaro .app bulur ve denetler; geçerliyse yolunu, değilse hata fırlatır.</summary>
	public static string FindAndVerifyBundle(string extracted, Version minimum)
	{
		string[] apps = Directory.GetDirectories(extracted, "*.app", SearchOption.TopDirectoryOnly);
		if (apps.Length != 1) throw new InvalidOperationException("Güncelleme paketinde tek bir Chiaro.app bulunamadı.");
		VerifyBundle(apps[0], minimum);
		return apps[0];
	}

	public static void VerifyBundle(string app, Version minimum)
	{
		string plist = Path.Combine(app, "Contents", "Info.plist");
		if (!File.Exists(plist)) throw new InvalidOperationException("Güncelleme paketinde Info.plist yok.");
		string xml = File.ReadAllText(plist);
		string? id = PlistString(xml, "CFBundleIdentifier");
		if (id != BundleId) throw new InvalidOperationException("Güncelleme paketi Chiaro'ya ait değil.");
		string exeName = PlistString(xml, "CFBundleExecutable") ?? "";
		if (exeName.Length == 0 || exeName.Contains('/') || exeName.Contains("..")) throw new InvalidOperationException("Güncelleme paketinde geçersiz uygulama adı.");
		string exe = Path.Combine(app, "Contents", "MacOS", exeName);
		if (!File.Exists(exe)) throw new InvalidOperationException("Güncelleme paketinde uygulama dosyası yok.");
		if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(exe) & UnixFileMode.UserExecute) == 0)
			throw new InvalidOperationException("Güncelleme paketindeki uygulama dosyası çalıştırılabilir değil.");
		if (!MacUpdater.TryParse(PlistString(xml, "CFBundleShortVersionString"), out Version v) || MacUpdater.Compare(v, minimum) < 0)
			throw new InvalidOperationException("Güncelleme paketinin sürümü beklenenden eski.");
		if (OperatingSystem.IsMacOS())
		{
			// Apple silicon imzasız ikili çalıştırmaz: imza bozuksa güncelleme yapılmaz (eski sürüm yerinde kalır).
			Run("/usr/bin/codesign", "--verify", "--deep", app);
		}
	}

	public static string? PlistString(string xml, string key)
	{
		Match m = Regex.Match(xml, "<key>" + Regex.Escape(key) + @"</key>\s*<string>([^<]*)</string>");
		return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
	}

	/// <summary>Karantina işaretini kaldırır (yoksa ya da xattr yoksa sessizce geçer).</summary>
	public static void RemoveQuarantine(string app)
	{
		if (!OperatingSystem.IsMacOS()) return;
		try { Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", app); } catch (Exception ex) { AppLog.Write("Güncelleme: xattr: " + ex.Message); }
	}

	/// <summary>
	/// Yeni paketi çalışan paketin yanına (aynı klasör → aynı birim) kopyalar: &lt;Chiaro.app&gt;.chiaro-new.
	/// Böylece betikteki değiş tokuş iki yeniden adlandırmadan ibarettir.
	/// </summary>
	public static string Stage(string newApp, string bundle)
	{
		string staged = bundle.TrimEnd('/') + StagingSuffix;
		if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
		// Aynı birimdeyse taşı; değilse sembolik bağları koruyarak kopyala.
		try
		{
			Directory.Move(newApp, staged);
		}
		catch (IOException)
		{
			if (OperatingSystem.IsMacOS()) Run("/usr/bin/ditto", newApp, staged);
			else Run("cp", "-Rp", newApp, staged);
		}
		return staged;
	}

	/// <summary>
	/// Yardımcı betik. Argümanlar: PID, eski paket, hazırlanmış yeni paket, günlük dosyası, yeniden açma komutu, durum dosyası,
	/// indirilen zip, başarısızlıkta zip'in taşınacağı yol (İndirilenler). Durum dosyasına çıkış kodu yazılır; uygulama yeniden
	/// açılınca okuyup kullanıcıya sonucu söyler.
	/// </summary>
	public const string HelperScript = """
#!/bin/sh
# Chiaro güncelleme yardımcısı: uygulama kapanınca paketi değiştirir ve yeniden açar.
PID="$1"; OLD="$2"; NEW="$3"; LOG="$4"; OPEN="$5"; STATUS="$6"; ZIP="$7"; FALLBACK="$8"
exec >>"$LOG" 2>&1
trap '' HUP INT TERM
BACKUP="$OLD.chiaro-old"
WORK="$(dirname "$0")"
done_() {
  # Başarısızlıkta doğrulanmış zip İndirilenler'e taşınır (elle kurulum için).
  if [ "$1" != 0 ] && [ -n "$ZIP" ] && [ -f "$ZIP" ] && [ -n "$FALLBACK" ]; then mv -f "$ZIP" "$FALLBACK"; fi
  [ -n "$STATUS" ] && echo "$1" > "$STATUS"
  # Geçici çalışma klasörünü (indirilen zip, bu betik) sil; yalnızca bizim klasörümüzse.
  case "$WORK" in *chiaro-update-*) rm -rf "$WORK" ;; esac
  exit "$1"
}
echo "$(date) güncelleme yardımcısı: pid=$PID"
i=0
while kill -0 "$PID" 2>/dev/null; do
  i=$((i+1))
  if [ "$i" -gt 1200 ]; then
    echo "uygulama 120 sn içinde kapanmadı; güncelleme iptal"
    rm -rf "$NEW"
    done_ 2
  fi
  sleep 0.1
done
if [ ! -d "$NEW" ]; then
  echo "hazırlanmış paket yok: $NEW"
  "$OPEN" "$OLD"
  done_ 3
fi
rm -rf "$BACKUP"
if ! mv "$OLD" "$BACKUP"; then
  echo "eski paket yedeğe alınamadı; güncelleme iptal"
  rm -rf "$NEW"
  "$OPEN" "$OLD"
  done_ 4
fi
if mv "$NEW" "$OLD" && [ -d "$OLD/Contents/MacOS" ]; then
  echo "paket değiştirildi"
  rm -rf "$BACKUP"
  "$OPEN" "$OLD"
  done_ 0
fi
echo "değiştirme başarısız; yedek geri yükleniyor"
rm -rf "$OLD"
mv "$BACKUP" "$OLD"
rm -rf "$NEW"
"$OPEN" "$OLD"
done_ 5
""";

	/// <summary>Betiği yazar ve ayrık olarak başlatır (uygulama kapanınca da sürer). Başlatılan süreci döndürür.</summary>
	public static Process StartHelper(string workDir, int pid, string bundle, string staged, string log, string opener, string status = "", string zip = "", string fallback = "")
	{
		Directory.CreateDirectory(workDir);
		string script = Path.Combine(workDir, "chiaro-update.sh");
		File.WriteAllText(script, HelperScript.Replace("\r\n", "\n") + "\n");
		if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		ProcessStartInfo psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = false, RedirectStandardError = false };
		foreach (string a in new[] { script, pid.ToString(), bundle.TrimEnd('/'), staged, log, opener, status, zip, fallback }) psi.ArgumentList.Add(a);
		Process p = Process.Start(psi) ?? throw new InvalidOperationException("Güncelleme yardımcısı başlatılamadı.");
		p.StandardInput.Close();
		return p;
	}

	private static void Run(string file, params string[] args)
	{
		ProcessStartInfo psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string a in args) psi.ArgumentList.Add(a);
		using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Komut başlatılamadı: " + Path.GetFileName(file));
		string err = p.StandardError.ReadToEnd();
		p.StandardOutput.ReadToEnd();
		if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("Komut zaman aşımına uğradı: " + Path.GetFileName(file)); }
		if (p.ExitCode != 0) throw new InvalidOperationException($"Komut başarısız: {Path.GetFileName(file)} ({p.ExitCode})" + (err.Trim().Length > 0 ? ": " + err.Trim() : ""));
	}

	public static string StatusFile => Path.Combine(AppLog.Folder, "update-status");

	/// <summary>Yardımcının bıraktığı sonuç: null (güncelleme yok), 0 başarılı, diğerleri başarısız. Dosya okunup silinir.</summary>
	public static int? TakeStatus(string? file = null)
	{
		file ??= StatusFile;
		try
		{
			if (!File.Exists(file)) return null;
			string text = File.ReadAllText(file).Trim();
			File.Delete(file);
			return int.TryParse(text, out int code) ? code : -1;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Önceki bir güncellemeden kalan hazırlık/yedek klasörlerini temizler (uygulama açılışında).</summary>
	public static void CleanupLeftovers(string? bundle)
	{
		if (bundle == null) return;
		foreach (string suffix in new[] { StagingSuffix, BackupSuffix })
		{
			string p = bundle.TrimEnd('/') + suffix;
			try { if (Directory.Exists(p)) Directory.Delete(p, recursive: true); } catch (Exception) { }
		}
	}
}
