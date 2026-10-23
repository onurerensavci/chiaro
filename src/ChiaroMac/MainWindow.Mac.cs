#nullable enable
// macOS'a özgü ek özellikler: oturum açılışında başlatma, güncelleme denetimi, mini çubuk.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HueStudio.Ui;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

public partial class MainWindow
{
	// =====================================================================
	//  Oturum açılışı ve güncelleme ayarları
	// =====================================================================

	private void InitMacExtras()
	{
		// Uygulama taşındıysa (ör. İndirilenler → Uygulamalar) oturum açılışı kaydındaki yolu düzelt.
		if (OperatingSystem.IsMacOS()) LoginItem.Refresh();
		// Önceki bir güncellemeden kalan hazırlık/yedek paketleri (ör. yarıda kalmış kurulum) temizle.
		if (OperatingSystem.IsMacOS()) SelfUpdate.CleanupLeftovers(LoginItem.BundlePath());
		// Güncelleme yardımcısının sonucu (yeniden açılıştan sonra bir kez gösterilir).
		if (OperatingSystem.IsMacOS() && SelfUpdate.TakeStatus() is int updateStatus)
		{
			Log("Güncelleme yardımcısı sonucu: " + updateStatus);
			DetailText.Text = updateStatus == 0 ? L.F("mac_upd_updated", Program.Version) : L.T("mac_upd_helper_failed");
		}
		AutostartBox.IsChecked = OperatingSystem.IsMacOS() && LoginItem.IsEnabled();
		AutostartBox.IsEnabled = OperatingSystem.IsMacOS();
		AutoUpdateBox.IsChecked = macPrefs.AutoUpdateCheck;
	}

	private void Autostart_Click(object? sender, RoutedEventArgs e)
	{
		bool want = AutostartBox.IsChecked == true;
		string? error = OperatingSystem.IsMacOS() ? LoginItem.Apply(want) : null;
		if (error != null)
		{
			AutostartBox.IsChecked = LoginItem.IsEnabled();
			DetailText.Text = L.F("mac_login_failed", L.Message(error));
			Log("Oturum açılışı: " + error);
			return;
		}
		DetailText.Text = L.T(want ? "autostart_tip" : "autostart");
		if (want && OperatingSystem.IsMacOS() && !LoginItem.IsInApplications(LoginItem.BundlePath())) DetailText.Text = L.T("mac_move_to_apps_hint");
		Log("Oturum açılışında başlat: " + (want ? "açık" : "kapalı"));
	}

	private void AutoUpdate_Click(object? sender, RoutedEventArgs e)
	{
		macPrefs.AutoUpdateCheck = AutoUpdateBox.IsChecked == true;
		macPrefs.Save();
	}

	private bool checkingUpdate;

	private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await CheckUpdates(manual: true);

	/// <summary>Günde en çok bir kez, sessizce (yalnız yeni Mac paketi varsa sorar).</summary>
	private async Task AutoCheckUpdates()
	{
		if (demoMode || !macPrefs.AutoUpdateCheck) return;
		if (macPrefs.LastUpdateCheck is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
		await Task.Delay(TimeSpan.FromSeconds(8));
		await CheckUpdates(manual: false);
	}

	private async Task CheckUpdates(bool manual)
	{
		if (checkingUpdate || closed) return;
		checkingUpdate = true;
		UpdateButton.IsEnabled = false;
		try
		{
			MacUpdate? u;
			string status;
			try
			{
				using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
				(u, status) = await MacUpdater.CheckAsync(cts.Token);
				macPrefs.LastUpdateCheck = DateTime.UtcNow;
				macPrefs.Save();
			}
			catch (Exception ex)
			{
				Log("Güncelleme denetimi: " + ex.Message);
				if (manual) DetailText.Text = L.F("mac_upd_failed", L.Message(ex.Message));
				return;
			}
			Log($"Güncelleme denetimi: {status} {u?.Version}");
			if (u == null)
			{
				// Mac paketi yoksa (yalnız Windows sürümü yayınlandıysa) sessizce geç.
				if (manual) DetailText.Text = L.T(status);
				return;
			}
			string text = L.F("mac_upd_available", u.Version) + $" (v{MacUpdater.Current.ToString(3)})" + (u.Notes.Length > 0 ? "\n\n" + Trim(u.Notes, 600) : "");
			// Yerinde güncellenebilir mi (paket, yazma izni, App Translocation)? Değilse eski davranış: İndirilenler'e indir.
			string? bundle = LoginItem.BundlePath();
			string? cannot = OperatingSystem.IsMacOS() ? SelfUpdate.CannotUpdateReason(bundle) : "mac_upd_self_no_bundle";
			if (cannot != null) Log("Güncelleme: yerinde kurulum yok (" + cannot + ")");
			int choice = await Dialogs.Choose(this, "Chiaro", text, L.T(cannot == null ? "mac_upd_install" : "mac_upd_download"), L.T("mac_upd_page"), L.T("mac_upd_later"));
			if (choice == 1)
			{
				OpenExternal(u.PageUrl);
				return;
			}
			// "Şimdi değil": sürüm atlanmaz; günlük otomatik denetim ertesi gün yeniden sorar.
			if (choice != 0) return;
			try
			{
				Progress<double> p = new Progress<double>(v => DetailText.Text = $"{u.AssetName} • {L.Pct(v * 100)}");
				using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
				if (cannot == null && bundle != null)
				{
					string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chiaro-update-" + Guid.NewGuid().ToString("N")[..8]);
					string zip = await MacUpdater.DownloadAsync(u, p, cts.Token, work);
					DetailText.Text = L.T("mac_upd_installing");
					try
					{
						Version want = MacUpdater.TryParse(u.Version, out Version wv) ? wv : MacUpdater.Current;
						string staged = await Task.Run(() =>
						{
							string extracted = System.IO.Path.Combine(work, "x");
							SelfUpdate.Extract(zip, extracted);
							string app = SelfUpdate.FindAndVerifyBundle(extracted, want);
							SelfUpdate.RemoveQuarantine(app);
							return SelfUpdate.Stage(app, bundle);
						});
						System.IO.Directory.CreateDirectory(AppLog.Folder);
						System.IO.Directory.CreateDirectory(MacUpdater.DownloadsFolder);
						SelfUpdate.StartHelper(work, Environment.ProcessId, bundle, staged, System.IO.Path.Combine(AppLog.Folder, "update.log"), "/usr/bin/open",
							SelfUpdate.StatusFile, zip, System.IO.Path.Combine(MacUpdater.DownloadsFolder, u.AssetName));
						Log($"Güncelleme: v{u.Version} hazırlandı; yardımcı başlatıldı, uygulama yeniden açılacak.");
						DetailText.Text = L.T("mac_upd_restarting");
						await Task.Delay(600);
						ExitApplication();
						return;
					}
					catch (Exception ex)
					{
						// Kurulum yapılamadı: doğrulanmış zip'i İndirilenler'e taşı (eski davranış).
						Log("Güncelleme kurulumu: " + ex);
						SelfUpdate.CleanupLeftovers(bundle);
						string target = System.IO.Path.Combine(MacUpdater.DownloadsFolder, u.AssetName);
						System.IO.Directory.CreateDirectory(MacUpdater.DownloadsFolder);
						System.IO.File.Move(zip, target, overwrite: true);
						try { System.IO.Directory.Delete(work, recursive: true); } catch (Exception) { }
						DetailText.Text = L.F("mac_upd_self_failed", L.Message(ex.Message)) + "\n" + L.F("mac_upd_done", u.AssetName);
						if (OperatingSystem.IsMacOS()) StartProcess("/usr/bin/open", "-R", target);
						return;
					}
				}
				string path = await MacUpdater.DownloadAsync(u, p, cts.Token);
				DetailText.Text = (cannot != null && OperatingSystem.IsMacOS() ? L.T(cannot) + "\n" : "") + L.F("mac_upd_done", System.IO.Path.GetFileName(path));
				if (OperatingSystem.IsMacOS()) StartProcess("/usr/bin/open", "-R", path);
			}
			catch (Exception ex)
			{
				Log("Güncelleme indirme: " + ex);
				DetailText.Text = L.F("mac_upd_dl_failed", L.Message(ex.Message));
			}
		}
		finally
		{
			checkingUpdate = false;
			UpdateButton.IsEnabled = true;
		}
	}

	private static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

	// =====================================================================
	//  Mini çubuk (her zaman üstte)
	// =====================================================================

	private Window? mini;
	private ToggleButton? miniPower;
	private Ellipse? miniDot;
	private Slider? miniSlider;
	private TextBlock? miniPct, miniTitle;
	private StackPanel? miniFavs;
	private WrapPanel? miniPanel;
	private Button? miniModesBtn, miniScenesBtn;
	private int miniSilent;

	private void Mini_Click(object? sender, RoutedEventArgs e) => ShowMini();

	// =====================================================================
	//  Dock simgesi: yalnız menü çubuğu simgesi görünürken gizli (Accessory), bir pencere açıkken görünür (Regular)
	// =====================================================================

	/// <summary>Ana pencere ya da mini çubuk görünürse Dock simgesi (ve ⌘Q'lu uygulama menüsü) gösterilir.</summary>
	private void UpdateDockIcon() => SetDockIcon(IsVisible || mini?.IsVisible == true || tray == null);

	/// <summary>NSApplicationActivationPolicy: 0 Regular (Dock + menü), 1 Accessory (yalnız menü çubuğu simgesi).</summary>
	internal static void SetDockIcon(bool visible)
	{
		if (!OperatingSystem.IsMacOS()) return;
		try
		{
			MacInterop.Native.Load(MacInterop.Native.AppKit);
			IntPtr app = MacInterop.Objc.Send(MacInterop.Objc.Cls("NSApplication"), MacInterop.Objc.Sel("sharedApplication"));
			if (app == IntPtr.Zero) return;
			nint want = visible ? 0 : 1;
			if (MacInterop.Objc.SendNInt(app, MacInterop.Objc.Sel("activationPolicy")) == want) return;
			MacInterop.Native.SendNIntArg(app, MacInterop.Objc.Sel("setActivationPolicy:"), want);
			// Accessory → Regular sonrası uygulama öne gelmezse pencere ve menü çubuğu arkada kalır.
			if (visible) MacInterop.Objc.SendBool(app, MacInterop.Objc.Sel("activateIgnoringOtherApps:"), 1);
			AppLog.Write("Dock simgesi: " + (visible ? "görünür" : "gizli"));
		}
		catch (Exception ex)
		{
			AppLog.Write("Dock simgesi ayarlanamadı: " + ex.Message);
		}
	}

	internal void ShowMini()
	{
		SetDockIcon(true);
		if (mini == null) BuildMini();
		RefreshMini();
		mini!.Show();
		mini.Activate();
		Hide();
	}

	private void LeaveMini()
	{
		mini?.Hide();
		RestoreWindow();
	}

	private Button MiniIconButton(string glyph, string tipKey, Action click)
	{
		Button b = new Button { Classes = { "pill" }, Content = glyph, Width = 32, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14 };
		ToolTip.SetTip(b, L.T(tipKey));
		b.Click += (_, _) => click();
		return b;
	}

	private void BuildMini()
	{
		Window w = new Window
		{
			Title = "Chiaro Mini",
			SystemDecorations = SystemDecorations.None,
			Topmost = true,
			CanResize = false,
			ShowInTaskbar = false,
			SizeToContent = SizeToContent.WidthAndHeight,
			Background = Brushes.Transparent,
			TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
			FontFamily = FontFamily,
		};
		w.RequestedThemeVariant = RequestedThemeVariant;

		StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
		row.Children.Add(MiniIconButton("⤢", "mac_mini_back", LeaveMini));

		miniPower = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
		ToolTip.SetTip(miniPower, L.T("power"));
		miniPower.Click += async (_, _) =>
		{
			if (miniSilent > 0) return;
			if (!ble.Ready) { miniPower.IsChecked = false; return; }
			await SetPower(miniPower.IsChecked == true);
		};
		row.Children.Add(miniPower);

		miniDot = new Ellipse { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
		row.Children.Add(miniDot);

		miniTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis };
		row.Children.Add(miniTitle);

		miniSlider = new Slider { Minimum = 1, Maximum = 100, Width = 140, VerticalAlignment = VerticalAlignment.Center };
		miniSlider.ValueChanged += (_, e) =>
		{
			if (miniSilent > 0) return;
			miniPct!.Text = L.Pct(e.NewValue);
			if (ble.Ready) LiveBrightness(e.NewValue, false);
		};
		miniSlider.AddHandler(PointerReleasedEvent, (_, _) => { if (ble.Ready) LiveBrightness(miniSlider.Value, true); }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
		row.Children.Add(miniSlider);
		miniPct = new TextBlock { FontSize = 11.5, Width = 36, VerticalAlignment = VerticalAlignment.Center, Classes = { "sub" } };
		row.Children.Add(miniPct);

		miniFavs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
		row.Children.Add(miniFavs);

		miniModesBtn = new Button { Classes = { "pill" }, Height = 30, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
		miniScenesBtn = new Button { Classes = { "pill" }, Height = 30, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
		miniModesBtn.Click += (_, _) => SetMiniPanel(macPrefs.MiniPanel == "modes" ? "" : "modes");
		miniScenesBtn.Click += (_, _) => SetMiniPanel(macPrefs.MiniPanel == "scenes" ? "" : "scenes");
		row.Children.Add(miniModesBtn);
		row.Children.Add(miniScenesBtn);
		row.Children.Add(MiniIconButton("✕", "close", () => { mini?.Hide(); macPrefs.MiniPanel = ""; LeaveMini(); }));

		miniPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), IsVisible = false, MaxWidth = 720 };

		StackPanel root = new StackPanel();
		root.Children.Add(row);
		root.Children.Add(miniPanel);
		Border card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8), BorderThickness = new Thickness(1), Child = root, Margin = new Thickness(6) };
		card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CardBg"));
		card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("CardBorder"));
		card.PointerPressed += (_, e) =>
		{
			if (e.Source is Border or StackPanel or WrapPanel or TextBlock or Ellipse && e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) w.BeginMoveDrag(e);
		};
		w.Content = card;
		w.PositionChanged += (_, _) =>
		{
			if (!w.IsVisible) return;
			macPrefs.MiniLeft = w.Position.X;
			macPrefs.MiniTop = w.Position.Y;
		};
		w.Closing += (_, e) =>
		{
			if (!exitRequested)
			{
				e.Cancel = true;
				LeaveMini();
			}
		};
		w.Opened += (_, _) =>
		{
			// Kayıtlı konum hâlâ bağlı bir ekranın içindeyse kullan (ikinci monitör çıkarıldıysa çubuk ekran dışında kalmasın).
			if (macPrefs.MiniLeft is double x && macPrefs.MiniTop is double y
				&& w.Screens.All.Any(sc => sc.Bounds.Contains(new PixelPoint((int)x + 20, (int)y + 10))))
			{
				w.Position = new PixelPoint((int)x, (int)y);
			}
			else if (w.Screens.Primary is Avalonia.Platform.Screen s)
			{
				w.Position = new PixelPoint(s.WorkingArea.X + (int)(s.WorkingArea.Width - w.Bounds.Width * s.Scaling) / 2, s.WorkingArea.Y + (int)(24 * s.Scaling));
			}
		};
		w.Deactivated += (_, _) => macPrefs.Save();
		mini = w;
		if (macPrefs.MiniPanel is "modes" or "scenes") SetMiniPanel(macPrefs.MiniPanel);
	}

	private void SetMiniPanel(string panel)
	{
		if (miniPanel == null) return;
		macPrefs.MiniPanel = panel;
		macPrefs.Save();
		miniModesBtn!.Classes.Set("primary", panel == "modes");
		miniModesBtn.Classes.Set("pill", panel != "modes");
		miniScenesBtn!.Classes.Set("primary", panel == "scenes");
		miniScenesBtn.Classes.Set("pill", panel != "scenes");
		miniPanel.Children.Clear();
		miniPanel.IsVisible = panel.Length > 0;
		if (panel == "modes")
		{
			foreach (string m in UserModes)
			{
				StackPanel c = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
				c.Children.Add(ModeIcons.Icon(m, 15));
				c.Children.Add(new TextBlock { Text = (modeName == m ? "● " : "") + L.Mode(m), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
				Button b = new Button { Classes = { "pill" }, Content = c, Margin = new Thickness(0, 0, 6, 6), IsEnabled = ble.Ready && ModeSupported(m) };
				b.Click += async (_, _) =>
				{
					if (modeName == m) await StopModeAsync();
					else await StartMode(m);
					SetMiniPanel("modes");
				};
				miniPanel.Children.Add(b);
			}
		}
		else if (panel == "scenes")
		{
			foreach (Scene s in Scenes.Take(12))
			{
				Button b = new Button { Classes = { "pill" }, Content = (s.Emoji is { Length: > 0 } e ? e + " " : "") + s.DisplayName, Margin = new Thickness(0, 0, 6, 6), IsEnabled = ble.Ready, FontSize = 12 };
				b.Click += async (_, _) => await ApplySceneAsync(s);
				miniPanel.Children.Add(b);
			}
		}
	}

	private void RefreshMini()
	{
		if (mini == null || miniPower == null) return;
		miniSilent++;
		try
		{
			bool ready = ble.Ready;
			mini.RequestedThemeVariant = RequestedThemeVariant;
			miniPower.IsChecked = ready && powerOn == true;
			miniPower.IsEnabled = ready && !busy;
			miniDot!.Fill = new SolidColorBrush(ready && colorKnown ? A(currentColor) : Color.Parse("#6B7385"));
			miniTitle!.Text = !ready ? L.T("not_connected") : modeName.Length > 0 ? L.Mode(modeName) : connectedName;
			double v = liveBrightness ?? BrightnessSlider.Value;
			miniSlider!.IsEnabled = ready;
			if (Math.Abs(miniSlider.Value - v) > 0.4) miniSlider.Value = v;
			miniPct!.Text = L.Pct(v);
			miniModesBtn!.Content = L.T("modes");
			miniScenesBtn!.Content = L.T("scenes");
			miniFavs!.Children.Clear();
			foreach (string hex in settings.Favorites.Take(5))
			{
				if (!ColorMath.TryParseHex(hex, out MColor c)) continue;
				Button b = new Button { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(0), Background = new SolidColorBrush(A(c)), BorderThickness = new Thickness(1), IsEnabled = ready };
				b.Bind(Button.BorderBrushProperty, b.GetResourceObservable("CardBorder"));
				ToolTip.SetTip(b, Palette.NameOf(c));
				b.Click += async (_, _) => await ApplyColor(c);
				miniFavs.Children.Add(b);
			}
		}
		finally
		{
			miniSilent--;
		}
	}
}
