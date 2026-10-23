#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace HueStudio;

/// <summary>
/// 3.3: Arayüz dili (Otomatik / Türkçe / English). XAML metinleri {l:T} ile kendiliğinden,
/// kodla yazılan metinler ApplyLanguage ile yeniden çizilir; yeniden başlatma gerekmez.
/// </summary>
public partial class MainWindow
{
	private static readonly string[] LanguageValues = { L.Auto, L.Turkish, L.English };
	private bool languageFilling;

	private void InitLanguage()
	{
		FillLanguageBox();
		L.Changed += ApplyLanguage;
		Closed += (_, _) => L.Changed -= ApplyLanguage;
	}

	private void FillLanguageBox()
	{
		languageFilling = true;
		try
		{
			LanguageBox.ItemsSource = new[] { L.T("lang_auto"), L.T("lang_tr"), L.T("lang_en") };
			LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, settings.Language));
		}
		finally
		{
			languageFilling = false;
		}
	}

	private void Language_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (languageFilling || LanguageBox.SelectedIndex < 0) return;
		SetLanguage(LanguageValues[LanguageBox.SelectedIndex]);
	}

	/// <summary>Ayarı kaydeder ve dili hemen uygular.</summary>
	internal void SetLanguage(string value)
	{
		settings.Language = value;
		SaveSettings();
		L.Apply(value);
		FillLanguageBox();
	}

	internal string LanguageSetting => settings.Language;

	/// <summary>Dil değişince kodla üretilen tüm metinleri yeniler.</summary>
	private void ApplyLanguage()
	{
		if (closed) return;
		FillLanguageBox();
		UpdateLightOnStartupBox();

		// Seçenek listeleri: seçimleri koru (SelectedIndex bağları ayarlara yazar).
		Refill(ScreenArea, ScreenSampler.AreaNames);
		Refill(ScreenMethod, ScreenSampler.MethodNames);
		Refill(RainbowDirectionBox, RainbowEngine.DirectionNames);
		Refill(RainbowPaletteBox, RainbowEngine.PaletteNames);
		RefreshMonitors();
		UpdateScreenIntervalText();

		// Dile göre biçimlenen bağlamalar (yüzde, dk, sn…) yeniden değerlendirilsin.
		RefreshBindings(this);
		BrightnessLabel.Text = L.Pct(BrightnessSlider.Value);
		SatLabel.Text = L.Pct(customSat * 100);

		// Kodla doldurulan bölümler
		BuildPalette();
		RebuildQuickSwatches();
		RebuildRecent();
		RebuildScenes();
		RebuildSchedules();
		for (int i = 0; i < WakeDays.Children.Count && i < 7; i++)
		{
			if (WakeDays.Children[i] is ToggleButton t)
			{
				t.Content = DayShort[i];
				t.ToolTip = DayLong[i];
			}
		}
		InitSections();
		UpdateTimerUi();
		UpdateSpotifySubtitle();
		if (NowPlayingTitle.Text == Strings.Tr["nothing_playing"] || NowPlayingTitle.Text == Strings.En["nothing_playing"]) ShowNowPlaying(null);
		NotifyStatus.Text = NotifyStatusText();
		ApplyTheme(darkTheme);
		ApplyGlass();
		RefreshUpdateButton();
		SetHint(FireNote, ble.Ready && !ble.Supports(3) ? L.T("fire_color_only") : "");

		// Durum satırları
		if (IsReady)
		{
			StatusText.Text = L.F("connected_to", demo ? "Hue Play" : connectedName);
			DetailText.Text = L.T("ready");
		}
		else if (!busy)
		{
			StatusText.Text = L.T("status_connect_bulb");
			DetailText.Text = L.T("status_remembered");
			StateText.Text = L.T("not_connected_dot");
		}
		if (modeName.Length == 0) LiveText.Text = IsReady ? L.T("applied_instantly") : L.T("connect_to_start");
		if (demo)
		{
			ApplyDemoControls();
			LiveText.Text = L.F("live_value", "50 ms");
		}
		UpdateBulbVisual(false);
		UpdateControls();
		if (demo) ApplyDemoControls();
		UpdateDaylightBar();

		tray?.Relocalize();
		tray?.SetScenes(Scenes);
		quick?.Relocalize();
		mini?.Relocalize();
		ScenesChanged?.Invoke();
		RaiseLightState();
	}

	private static void Refill(ComboBox box, string[] items)
	{
		int keep = box.SelectedIndex;
		box.ItemsSource = items;
		box.SelectedIndex = keep;
	}

	/// <summary>Görsel ağaçtaki tüm Text/Content bağlamalarını kaynağından yeniden okur.</summary>
	internal static void RefreshBindings(DependencyObject root)
	{
		foreach (DependencyObject o in LogicalAndVisual(root))
		{
			if (o is TextBlock tb) BindingOperations.GetBindingExpressionBase(tb, TextBlock.TextProperty)?.UpdateTarget();
			else if (o is ContentControl cc) BindingOperations.GetBindingExpressionBase(cc, ContentControl.ContentProperty)?.UpdateTarget();
		}
	}

	/// <summary>Mantıksal + görsel ağaçtaki tüm öğeler (kapalı/daraltılmış bölümler dahil).</summary>
	internal static IEnumerable<DependencyObject> LogicalAndVisual(DependencyObject root)
	{
		HashSet<DependencyObject> seen = new HashSet<DependencyObject>();
		Stack<DependencyObject> stack = new Stack<DependencyObject>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			DependencyObject o = stack.Pop();
			if (!seen.Add(o)) continue;
			yield return o;
			foreach (object c in LogicalTreeHelper.GetChildren(o))
			{
				if (c is DependencyObject d) stack.Push(d);
			}
			if (o is Visual || o is System.Windows.Media.Media3D.Visual3D)
			{
				int n = VisualTreeHelper.GetChildrenCount(o);
				for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(o, i));
			}
		}
	}
}
