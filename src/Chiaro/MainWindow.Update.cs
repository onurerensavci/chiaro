#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace HueStudio;

/// <summary>GitHub Releases üzerinden uygulama içi güncelleme.</summary>
public partial class MainWindow
{
	private UpdateInfo? availableUpdate;
	private bool updateChecking;
	private DispatcherTimer? updateNoteTimer;
	private UpdateDialog? updateDialog;

	internal event Action? UpdateStateChanged;

	internal UpdateInfo? AvailableUpdate => availableUpdate;

	internal string QuickPanelTab
	{
		get => settings.QuickTab == ScenesModes.ModesTab ? ScenesModes.ModesTab : ScenesModes.ScenesTab;
		set { settings.QuickTab = value; SaveSettings(); }
	}

	/// <summary>Açılış: kalan .old dosyasını sil, önbellekteki rozeti göster, gerekirse sessizce denetle.</summary>
	internal void StartUpdateServices(bool justUpdated)
	{
		if (!persistent) return;
		string? exe = Updater.RunningExe;
		if (exe != null) _ = Updater.CleanupAsync(exe);
		if (settings.KnownUpdate is UpdateInfo known && Updater.IsNewer(known.Tag, Updater.Current))
		{
			SetAvailableUpdate(known);
		}
		else if (settings.KnownUpdate != null)
		{
			settings.KnownUpdate = null;
			SaveSettings();
		}
		RefreshUpdateButton();
		if (justUpdated)
		{
			Log($"Güncelleme tamamlandı: {Updater.CurrentText}");
			DetailText.Text = L.F("upd_done", Updater.CurrentText);
		}
		bool due = settings.LastUpdateCheck is not DateTimeOffset last || DateTimeOffset.Now - last >= TimeSpan.FromHours(24) || last > DateTimeOffset.Now.AddMinutes(5);
		if (!due) return;
		DispatcherTimer delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
		delay.Tick += async (_, _) =>
		{
			delay.Stop();
			await CheckForUpdatesAsync(manual: false);
		};
		delay.Start();
	}

	/// <summary>Denetler. Sessiz denetimde ağ hataları hiçbir şey göstermez.</summary>
	internal async Task<UpdateCheckResult?> CheckForUpdatesAsync(bool manual)
	{
		if (updateChecking) return null;
		updateChecking = true;
		if (manual) RefreshUpdateButton(L.T("upd_checking"));
		UpdateCheckResult r;
		try
		{
			r = await Updater.CheckAsync();
		}
		catch (Exception ex)
		{
			r = new UpdateCheckResult(UpdateStatus.Error, null, L.T("upd_check_failed"));
			Log("Güncelleme denetimi: " + ex.Message);
		}
		finally
		{
			updateChecking = false;
		}
		Log($"Güncelleme denetimi ({(manual ? "elle" : "otomatik")}): {r.Status} {r.Info?.VersionText}");
		if (r.Status == UpdateStatus.UpToDate || r.Status == UpdateStatus.Available)
		{
			settings.LastUpdateCheck = DateTimeOffset.Now;
			settings.KnownUpdate = r.Info;
			SaveSettings();
			SetAvailableUpdate(r.Info);
		}
		if (!manual)
		{
			RefreshUpdateButton();
			return r;
		}
		string? note = r.Status switch
		{
			UpdateStatus.UpToDate => L.T("upd_uptodate"),
			UpdateStatus.Offline => L.T("upd_note_offline"),
			UpdateStatus.RateLimited => L.T("upd_note_rate"),
			UpdateStatus.Error => L.T("upd_note_error"),
			_ => null,
		};
		RefreshUpdateButton(note);
		if (r.Status != UpdateStatus.Available && r.Status != UpdateStatus.UpToDate)
		{
			UpdateButton.ToolTip = r.Message;
		}
		return r;
	}

	private void SetAvailableUpdate(UpdateInfo? info)
	{
		availableUpdate = info;
		RefreshUpdateButton();
		UpdateStateChanged?.Invoke();
	}

	/// <summary>Düğme: "Güncellemeleri denetle" ya da vurgulu "Güncelle (vX.Y)" + rozet. note birkaç saniye görünür.</summary>
	private void RefreshUpdateButton(string? note = null)
	{
		updateNoteTimer?.Stop();
		UpdateButton.IsEnabled = !updateChecking;
		if (availableUpdate != null)
		{
			UpdateText.Text = L.F("upd_button", availableUpdate.VersionText);
			UpdateButton.SetResourceReference(BackgroundProperty, "Accent");
			UpdateButton.SetResourceReference(ForegroundProperty, "AccentText");
			UpdateButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
			UpdateButton.FontWeight = FontWeights.SemiBold;
			UpdateButton.ToolTip = L.F("upd_button_tip", availableUpdate.VersionText);
			UpdateDot.Visibility = Visibility.Visible;
			return;
		}
		UpdateButton.ClearValue(BackgroundProperty);
		UpdateButton.ClearValue(ForegroundProperty);
		UpdateButton.ClearValue(BorderBrushProperty);
		UpdateButton.ClearValue(FontWeightProperty);
		UpdateButton.ToolTip = L.T("hdr_check_updates_tip");
		UpdateDot.Visibility = Visibility.Collapsed;
		UpdateText.Text = note ?? L.T("hdr_check_updates");
		if (note != null && !updateChecking)
		{
			updateNoteTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
			updateNoteTimer.Tick -= UpdateNoteElapsed;
			updateNoteTimer.Tick += UpdateNoteElapsed;
			updateNoteTimer.Start();
		}
	}

	private void UpdateNoteElapsed(object? sender, EventArgs e) => RefreshUpdateButton();

	private async void UpdateButton_Click(object sender, RoutedEventArgs e)
	{
		if (availableUpdate != null)
		{
			ShowUpdateDialog();
			return;
		}
		UpdateCheckResult? r = await CheckForUpdatesAsync(manual: true);
		if (r?.Status == UpdateStatus.Available) ShowUpdateDialog();
	}

	internal void ShowUpdateDialog()
	{
		if (availableUpdate == null)
		{
			_ = CheckForUpdatesAsync(manual: true);
			return;
		}
		if (updateDialog != null)
		{
			updateDialog.Activate();
			return;
		}
		updateDialog = new UpdateDialog(this, availableUpdate, InstallUpdateAsync);
		try
		{
			updateDialog.ShowDialog();
		}
		finally
		{
			updateDialog = null;
		}
	}

	/// <summary>İndir, doğrula, yerine koy, yeni sürümü başlat. Hata metni ya da (başarıda) null döner.</summary>
	private async Task<string?> InstallUpdateAsync(UpdateInfo info, IProgress<double> progress, CancellationToken token)
	{
		string? exe = Updater.RunningExe;
		if (exe == null) return L.T("upd_cant_self");
		string downloaded;
		try
		{
			downloaded = await Updater.DownloadAsync(info, progress, token);
		}
		catch (UpdateException ex)
		{
			Log("Güncelleme indirilemedi: " + ex.Message + " " + ex.InnerException?.Message);
			return ex.Message;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return L.T("upd_cancelled");
		}
		catch (OperationCanceledException)
		{
			return L.T("upd_timeout");
		}
		catch (Exception ex)
		{
			Log("Güncelleme indirilemedi: " + ex);
			return L.T("upd_download_failed");
		}
		token.ThrowIfCancellationRequested();
		try
		{
			await Task.Run(() => Updater.Swap(exe, downloaded));
		}
		catch (UpdateException ex)
		{
			Log("Güncelleme kurulamadı: " + ex.Message + " " + ex.InnerException?.Message);
			return ex.Message;
		}
		catch (Exception ex)
		{
			Updater.Rollback(exe);
			Log("Güncelleme kurulamadı: " + ex);
			return L.T("upd_install_failed");
		}
		try
		{
			// Yeni kopya, bu kopya tek-kopya kilidini bırakana kadar bekler (--updated).
			Process? p = Process.Start(new ProcessStartInfo(exe, Updater.UpdatedArgument)
			{
				UseShellExecute = false,
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
			});
			if (p == null) throw new InvalidOperationException("Process.Start null");
		}
		catch (Exception ex)
		{
			bool restored = Updater.Rollback(exe);
			Log("Yeni sürüm başlatılamadı: " + ex.Message + $" (geri alındı: {restored})");
			return restored ? L.T("upd_start_rolled_back") : L.T("upd_start_failed");
		}
		try
		{
			File.Delete(downloaded);
		}
		catch (Exception)
		{
		}
		Log($"Güncelleme kuruldu: {info.VersionText}; yeniden başlatılıyor.");
		settings.KnownUpdate = null;
		SaveSettings();
		_ = Dispatcher.InvokeAsync(async () =>
		{
			await Task.Delay(400);
			ExitForUpdate();
		});
		return null;
	}

	/// <summary>Temiz çıkış: pencere/ampul/tepsi kapanır, kilit bırakılır, yeni kopya devralır.</summary>
	private void ExitForUpdate()
	{
		exitRequested = true;
		try
		{
			updateDialog?.Close();
			mini?.Close();
			Close();
		}
		catch (Exception)
		{
		}
		(Application.Current as App)?.ReleaseInstanceLock();
		// Güvenlik ağı: süreç kapanışta takılırsa .old dosyası kilitli kalmasın diye birkaç saniye sonra kesin çık.
		new System.Threading.Thread(() =>
		{
			System.Threading.Thread.Sleep(4000);
			try { Process.GetCurrentProcess().Kill(); } catch (Exception) { Environment.Exit(0); }
		}) { IsBackground = true, Name = "update-exit-watchdog" }.Start();
		Application.Current.Shutdown();
	}

	// ---- Duman testi ----
	internal FrameworkElement HeaderElement => HeaderActions;

	internal void SetUpdateForTest(UpdateInfo? info, string? note = null)
	{
		availableUpdate = info;
		RefreshUpdateButton(note);
		updateNoteTimer?.Stop();
		UpdateStateChanged?.Invoke();
	}
}
