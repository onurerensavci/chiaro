#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HueStudio;

/// <summary>Her zaman üstte, sürüklenebilir, yarı saydam mini kontrol çubuğu.</summary>
public partial class MiniBar : Window
{
	private readonly MainWindow host;
	private readonly SolidColorBrush chip = new SolidColorBrush(Colors.Gray);
	private readonly SolidColorBrush bulb = new SolidColorBrush(Color.FromRgb(0x6B, 0x73, 0x85));
	private readonly GradientStop glow0 = new GradientStop(Colors.Transparent, 0);
	private bool syncing;

	internal MiniBar(MainWindow host)
	{
		InitializeComponent();
		this.host = host;
		Chip.Fill = chip;
		Bulb.Fill = bulb;
		Glow.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { glow0, new GradientStop(Colors.Transparent, 1) } };
		host.LightStateChanged += Sync;
		host.ScenesChanged += Rebuild;
		SizeChanged += OnSizeChanged;
		panel = host.MiniPanel;
		Rebuild();
		Sync();
		ApplyPanel(animate: false);
	}

	internal void Rebuild()
	{
		if (panel != "" && PanelItems != null) FillPanel();
		Favorites.Children.Clear();
		foreach (Color c in host.FavoriteColors.Take(6))
		{
			Button b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), ToolTip = Palette.NameOf(c) };
			b.Click += async (_, _) => await host.ApplyColorAsync(c);
			Favorites.Children.Add(b);
		}
	}

	/// <summary>Dil değişti: kodla yazılan metinleri yeniden kur.</summary>
	internal void Relocalize()
	{
		Rebuild();
		Sync();
		if (panel != "") FillPanel();
	}

	internal void Sync()
	{
		syncing = true;
		try
		{
			bool on = host.PowerState == true;
			Color c = host.HasColor ? host.LiveColor : Colors.Gray;
			PowerButton.IsEnabled = host.IsReady;
			Bri.IsEnabled = host.IsReady;
			if (!Bri.IsMouseCaptureWithin) Bri.Value = Math.Round(host.CurrentBrightness);
			BriText.Text = L.Pct(Bri.Value);
			Anim.Color(chip, on ? c : Color.FromRgb(0x55, 0x5C, 0x6E), 220);
			Anim.Color(bulb, on ? ColorMath.Lerp(c, Colors.White, 0.2) : Color.FromRgb(0x6B, 0x73, 0x85), 220);
			Anim.Color(glow0, Color.FromArgb(on ? (byte)200 : (byte)0, c.R, c.G, c.B), 220);
			if (panel != "") FillPanel();
			string? live = host.LiveLine();
			ToolTip = live ?? host.StatusLine();
		}
		finally
		{
			syncing = false;
		}
	}

	private void Drag(object sender, MouseButtonEventArgs e)
	{
		if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
		try
		{
			DragMove();
		}
		catch (InvalidOperationException)
		{
		}
		host.SaveMiniPosition(Left, BarTop);
	}

	private static bool IsInteractive(DependencyObject? d)
	{
		while (d != null)
		{
			if (d is System.Windows.Controls.Primitives.ButtonBase || d is Slider) return true;
			d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
		}
		return false;
	}

	private async void Power_Click(object sender, RoutedEventArgs e) => await host.TogglePowerAsync();

	private void Bri_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (BriText != null) BriText.Text = L.Pct(e.NewValue);
		if (!syncing && IsLoaded) host.LiveBrightness(e.NewValue, final: false);
	}

	private void Bri_Release(object sender, MouseButtonEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private void Bri_KeyUp(object sender, KeyEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private void Expand_Click(object sender, RoutedEventArgs e)
	{
		host.HideMini();
		host.RestoreWindow();
	}

	private void Exit_Click(object sender, RoutedEventArgs e) => host.ExitApplication();

	// ---- Sahneler | Modlar açılır paneli ----

	private string panel = "";
	private bool upward;
	private bool placing;
	private double lastHeight;

	/// <summary>Çubuğun pencere içindeki dikey konumu (panel yukarıdaysa > 12).</summary>
	private double BarOffset => Surface.IsLoaded ? Surface.TranslatePoint(new Point(0, 0), this).Y : 12;

	/// <summary>Çubuğun ekrandaki üst kenarı (kaydedilen konum, panelden bağımsız).</summary>
	internal double BarTop => Top + BarOffset - 12;

	internal double BarHeight => Surface.ActualHeight + 24;

	private void Tab_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string tab }) return;
		string next = panel == tab ? "" : tab;
		if (panel == "" && next != "") ChooseDirection();
		panel = next;
		host.MiniPanel = next;
		ApplyPanel(animate: true);
	}

	private void ChooseDirection()
	{
		Rect work = SystemParameters.WorkArea;
		try
		{
			System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(BarTop + 30)));
			var src = PresentationSource.FromVisual(this);
			double k = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
			work = new Rect(scr.WorkingArea.X * k, scr.WorkingArea.Y * k, scr.WorkingArea.Width * k, scr.WorkingArea.Height * k);
		}
		catch
		{
		}
		double barBottom = BarTop + 12 + Surface.ActualHeight;
		upward = barBottom + 210 > work.Bottom;
		Grid.SetRow(PanelHost, upward ? 0 : 2);
		PanelHost.Margin = upward ? new Thickness(0, 0, 0, 8) : new Thickness(0, 8, 0, 0);
		PanelHost.LayoutTransform = new ScaleTransform(1, PanelHost.Visibility == Visibility.Visible ? 1 : 0) { CenterY = 0 };
	}

	internal void ApplyPanel(bool animate)
	{
		TabScenes.IsChecked = panel == "Sahneler";
		TabModes.IsChecked = panel == "Modlar";
		if (panel != "") FillPanel();
		Anim.Collapse(PanelHost, panel != "", animate);
	}

	private void FillPanel()
	{
		if (panel == ScenesModes.ScenesTab)
		{
			PanelTitle.Text = L.T("scenes_upper");
			ScenesModes.FillScenes(PanelItems, host);
		}
		else if (panel == ScenesModes.ModesTab)
		{
			PanelTitle.Text = L.T("modes_upper");
			ScenesModes.FillModes(PanelItems, host);
		}
		else
		{
			PanelItems.Children.Clear();
		}
	}

	/// <summary>Kayıtlı konuma yerleştirir (çubuk sabit kalır, panel yönü ekrana göre seçilir).</summary>
	internal void Place(double left, double barTop)
	{
		placing = true;
		Left = left;
		Top = barTop;
		UpdateLayout();
		if (panel != "")
		{
			ChooseDirection();
			UpdateLayout();
			Top = barTop - (BarOffset - 12);
		}
		lastHeight = ActualHeight;
		placing = false;
	}

	private void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (!e.HeightChanged) return;
		double delta = e.NewSize.Height - (lastHeight > 0 ? lastHeight : e.PreviousSize.Height);
		lastHeight = e.NewSize.Height;
		// Yukarı açılırken çubuğu yerinde tut: pencere yukarı doğru büyür.
		if (!placing && upward && e.PreviousSize.Height > 0) Top -= delta;
	}
}
