#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLitePCL;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HueStudio;

/// <summary>
/// Windows bildirimlerini izler.
/// 1) UserNotificationListener (resmi API): paketli uygulamalar için tasarlandı; paketsiz
///    bir exe'de çoğu sistemde erişim verilmez veya istisna atar.
/// 2) Yedek yol: Windows'un kendi bildirim veritabanı
///    (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db) salt okunur yoklanır.
///    Yalnızca yeni kayıtların uygulama kimliği okunur; içerik okunmaz, hiçbir şey yazılmaz.
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
	public event Action<string>? Arrived;
	public string Status { get; private set; } = "Kapalı";
	public string Method { get; private set; } = "";

	private CancellationTokenSource? cts;
	private readonly Action<string> log;

	public NotificationWatcher(Action<string> log)
	{
		this.log = log;
	}

	/// <summary>Yalnızca duman testi için.</summary>
	public static string? TestDatabase;
	public bool SkipListener;

	public static string DatabasePath => TestDatabase ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

	/// <summary>UI iş parçacığından çağrılmalı (izin istemi için).</summary>
	public async Task StartAsync()
	{
		Stop();
		cts = new CancellationTokenSource();
		CancellationToken ct = cts.Token;
		UserNotificationListener? listener = null;
		try
		{
			if (SkipListener) throw new InvalidOperationException("test");
			listener = UserNotificationListener.Current;
			UserNotificationListenerAccessStatus st = listener.GetAccessStatus();
			if (st == UserNotificationListenerAccessStatus.Unspecified)
			{
				st = await listener.RequestAccessAsync();
			}
			if (st != UserNotificationListenerAccessStatus.Allowed)
			{
				log("Bildirim dinleyici izni: " + st);
				listener = null;
			}
		}
		catch (Exception ex)
		{
			log("Bildirim dinleyici kullanılamıyor: " + ex.Message);
			listener = null;
		}
		if (listener != null)
		{
			Method = "Windows bildirim izni";
			Status = "Dinleniyor";
			_ = Task.Run(() => PollListener(listener, ct));
			return;
		}
		if (File.Exists(DatabasePath))
		{
			Method = "Windows bildirim kaydı";
			Status = "Dinleniyor";
			_ = Task.Run(() => PollDatabase(ct));
			return;
		}
		Method = "";
		Status = "Bildirimlere erişilemiyor";
	}

	public void Stop()
	{
		cts?.Cancel();
		cts?.Dispose();
		cts = null;
		Status = "Kapalı";
	}

	public void Dispose() => Stop();

	private async Task PollListener(UserNotificationListener listener, CancellationToken ct)
	{
		HashSet<uint> seen = new HashSet<uint>();
		bool first = true;
		while (!ct.IsCancellationRequested)
		{
			try
			{
				IReadOnlyList<UserNotification> list = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct);
				foreach (UserNotification n in list)
				{
					if (seen.Add(n.Id) && !first)
					{
						string app = "";
						try { app = (n.AppInfo?.AppUserModelId ?? "") + " " + (n.AppInfo?.DisplayInfo?.DisplayName ?? ""); } catch (Exception) { }
						Arrived?.Invoke(app);
					}
				}
				first = false;
			}
			catch (OperationCanceledException) { return; }
			catch (Exception ex)
			{
				log("Bildirim okuma: " + ex.Message);
			}
			try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return; }
		}
	}

	private async Task PollDatabase(CancellationToken ct)
	{
		long last = -1;
		string? tempCopy = null;
		bool useCopy = false;
		int errors = 0;
		while (!ct.IsCancellationRequested)
		{
			try
			{
				string path = DatabasePath;
				if (useCopy)
				{
					tempCopy ??= Path.Combine(Path.GetTempPath(), "HueStudio-wpn");
					Directory.CreateDirectory(tempCopy);
					foreach (string ext in new[] { "", "-wal", "-shm" })
					{
						string src = DatabasePath + ext;
						string dst = Path.Combine(tempCopy, "wpn.db" + ext);
						if (File.Exists(src))
						{
							using FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
							using FileStream ds = File.Create(dst);
							await fs.CopyToAsync(ds, ct);
						}
						else if (File.Exists(dst))
						{
							File.Delete(dst);
						}
					}
					path = Path.Combine(tempCopy, "wpn.db");
				}
				foreach (string app in ReadNew(path, readOnly: !useCopy, ref last))
				{
					Arrived?.Invoke(app);
				}
				errors = 0;
				Status = "Dinleniyor";
			}
			catch (OperationCanceledException) { return; }
			catch (Exception ex)
			{
				errors++;
				log("Bildirim kaydı okunamadı: " + ex.Message);
				if (!useCopy)
				{
					useCopy = true; // Doğrudan açılamıyorsa kopyadan oku.
				}
				else if (errors > 5)
				{
					Status = "Bildirimlere erişilemiyor";
				}
			}
			try { await Task.Delay(useCopy ? 3000 : 1500, ct); } catch (OperationCanceledException) { return; }
		}
	}

	private static bool sqliteReady;

	/// <summary>
	/// SQLitePCL ile doğrudan okuma (Microsoft.Data.Sqlite bağlantı sınıfı paketsiz/Wine
	/// ortamında WinRT ApplicationData'ya dokunduğu için kullanılmıyor).
	/// İlk çağrıda yalnızca son kimliği öğrenir; sonra yeni toast kayıtlarının uygulama kimliklerini döndürür.
	/// </summary>
	internal static List<string> ReadNew(string path, bool readOnly, ref long last)
	{
		if (!sqliteReady)
		{
			SQLitePCL.Batteries_V2.Init();
			sqliteReady = true;
		}
		List<string> apps = new List<string>();
		int flags = readOnly ? raw.SQLITE_OPEN_READONLY : raw.SQLITE_OPEN_READWRITE;
		int rc = raw.sqlite3_open_v2(path, out sqlite3 db, flags, null);
		try
		{
			if (rc != raw.SQLITE_OK) throw new IOException("sqlite open " + rc + ": " + raw.sqlite3_errmsg(db).utf8_to_string());
			raw.sqlite3_busy_timeout(db, 500);
			string sql = last < 0
				? "SELECT IFNULL(MAX(Id), 0), '' FROM Notification"
				: "SELECT n.Id, IFNULL(h.PrimaryId, '') FROM Notification n LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Id > ?1 AND n.Type = 'toast' ORDER BY n.Id";
			bool baseline = last < 0;
			rc = raw.sqlite3_prepare_v2(db, sql, out sqlite3_stmt stmt);
			if (rc != raw.SQLITE_OK) throw new IOException("sqlite prepare " + rc + ": " + raw.sqlite3_errmsg(db).utf8_to_string());
			try
			{
				if (!baseline) raw.sqlite3_bind_int64(stmt, 1, last);
				while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
				{
					long id = raw.sqlite3_column_int64(stmt, 0);
					last = Math.Max(baseline ? 0 : last, id);
					if (!baseline) apps.Add(raw.sqlite3_column_text(stmt, 1).utf8_to_string() ?? "");
				}
				if (rc != raw.SQLITE_DONE) throw new IOException("sqlite step " + rc + ": " + raw.sqlite3_errmsg(db).utf8_to_string());
			}
			finally
			{
				raw.sqlite3_finalize(stmt);
			}
		}
		finally
		{
			raw.sqlite3_close_v2(db);
		}
		return apps.Distinct().ToList();
	}

	/// <summary>Yalnızca duman testi: SQL çalıştırır.</summary>
	internal static void ExecForTest(string path, string sql)
	{
		if (!sqliteReady) { SQLitePCL.Batteries_V2.Init(); sqliteReady = true; }
		raw.sqlite3_open_v2(path, out sqlite3 db, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null);
		try
		{
			int rc = raw.sqlite3_exec(db, sql);
			if (rc != raw.SQLITE_OK) throw new IOException("sqlite exec " + rc);
		}
		finally
		{
			raw.sqlite3_close_v2(db);
		}
	}

	/// <summary>Uygulama kimliğinden kısa ad: Discord, WhatsApp, Teams, Outlook veya Diğer.</summary>
	public static string Classify(string appId)
	{
		string a = appId.ToLowerInvariant();
		if (a.Contains("discord")) return "Discord";
		if (a.Contains("whatsapp")) return "WhatsApp";
		if (a.Contains("teams")) return "Teams";
		if (a.Contains("outlook") || a.Contains("olk.exe")) return "Outlook";
		return "Diğer";
	}

	public static string AppColor(string app) => app switch
	{
		"Discord" => "#5865F2",
		"WhatsApp" => "#25D366",
		"Teams" => "#7B83EB",
		"Outlook" => "#0F6CBD",
		_ => "#FFFFFF",
	};
}
