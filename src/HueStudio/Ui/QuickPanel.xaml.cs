#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HueStudio;

/// <summary>Windows 11 hızlı ayarlar tarzı açılır panel (simgeye sol tık).</summary>
public partial class QuickPanel : Window
{
	private readonly MainWindow host;
	private readonly SolidColorBrush chip = new SolidColorBrush(Colors.Gray);
	private readonly GradientStop glow0 = new GradientStop(Colors.Transparent, 0);
	private bool syncing;
	private DateTime hiddenAt = DateTime.MinValue;

	internal QuickPanel(MainWindow host)
	{
		InitializeComponent();
		this.host = host;
		Chip.Fill = chip;
		Glow.Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection { glow0, new GradientStop(Colors.Transparent, 1) } };
		Anim.AttachSwitch(Power);
		Deactivated += (_, _) => HidePanel();
		KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePanel(); };
		host.LightStateChanged += Sync;
		host.ScenesChanged += Rebuild;
		Rebuild();
		Sync();
	}

	/// <summary>Panel kapanır kapanmaz aynı tıkla yeniden açılmasın.</summary>
	public bool JustHidden => (DateTime.Now - hiddenAt).TotalMilliseconds < 350;

	public void ShowNear(Rect? iconRectDip)
	{
		Rebuild();
		Sync();
		Rect work = SystemParameters.WorkArea;
		Show();
		UpdateLayout();
		double w = ActualWidth > 0 ? ActualWidth : Width;
		double h = ActualHeight > 0 ? ActualHeight : 520;
		double cx = iconRectDip.HasValue ? iconRectDip.Value.Left + iconRectDip.Value.Width / 2 : work.Right - w / 2;
		Left = Math.Clamp(cx - w / 2, work.Left, work.Right - w);
		Top = Math.Max(work.Top, work.Bottom - h);
		Activate();
		if (Anim.Enabled)
		{
			TranslateTransform t = new TranslateTransform(0, 18);
			Surface.RenderTransform = t;
			Surface.Opacity = 0;
			Surface.BeginAnimation(OpacityProperty, Anim.To(1, 200));
			t.BeginAnimation(TranslateTransform.YProperty, Anim.To(0, 260, new CubicEase { EasingMode = EasingMode.EaseOut }));
		}
	}

	public void HidePanel()
	{
		if (!IsVisible) return;
		hiddenAt = DateTime.Now;
		Hide();
	}

	internal void Rebuild()
	{
		Favorites.Children.Clear();
		foreach (Color c in host.FavoriteColors.Take(8))
		{
			Button b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 6), ToolTip = Palette.NameOf(c) };
			b.Click += async (_, _) => await host.ApplyColorAsync(c);
			Favorites.Children.Add(b);
		}
		Scenes.Children.Clear();
		foreach (Scene s in host.SceneList.Take(8))
		{
			Scenes.Children.Add(MakeSceneChip(s, () => _ = host.ApplySceneAsync(s)));
		}
	}

	internal static Button MakeSceneChip(Scene s, Action click)
	{
		StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
		if (string.IsNullOrWhiteSpace(s.Emoji))
		{
			content.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(s.Accent), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
		}
		else
		{
			FrameworkElement e = EmojiArt.View(s.Emoji, 16);
			e.Margin = new Thickness(0, 0, 7, 0);
			e.VerticalAlignment = VerticalAlignment.Center;
			content.Children.Add(e);
		}
		content.Children.Add(new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
		Button b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), ToolTip = s.Subtitle };
		b.SetResourceReference(StyleProperty, "Small");
		b.Click += (_, _) => click();
		return b;
	}

	internal void Sync()
	{
		if (!IsVisible && IsLoaded) return;
		syncing = true;
		try
		{
			LampName.Text = host.LampLabel;
			Status.Text = host.StatusLine();
			Power.IsEnabled = host.IsReady;
			Power.IsChecked = host.PowerState == true;
			Bri.IsEnabled = host.IsReady;
			if (!Bri.IsMouseCaptureWithin)
			{
				Bri.Value = Math.Round(host.CurrentBrightness);
			}
			BriText.Text = $"%{Bri.Value:F0}";
			Color c = host.HasColor ? host.LiveColor : Colors.Gray;
			bool on = host.PowerState == true;
			Anim.Color(chip, on ? c : Color.FromRgb(0x55, 0x5C, 0x6E), 220);
			Anim.Color(glow0, Color.FromArgb(on ? (byte)190 : (byte)0, c.R, c.G, c.B), 220);
			string? live = host.LiveLine();
			LiveBox.Visibility = live == null ? Visibility.Collapsed : Visibility.Visible;
			LiveText.Text = live ?? "";
			SleepButton.Content = host.SleepActive ? "Sayacı iptal et" : "Uyku 30 dk";
		}
		finally
		{
			syncing = false;
		}
	}

	private async void Power_Click(object sender, RoutedEventArgs e) => await host.TogglePowerAsync();

	private void Bri_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (BriText != null) BriText.Text = $"%{e.NewValue:F0}";
		if (!syncing && IsLoaded) host.LiveBrightness(e.NewValue, final: false);
	}

	private void Bri_Release(object sender, MouseButtonEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private void Bri_KeyUp(object sender, KeyEventArgs e) => host.LiveBrightness(Bri.Value, final: true);

	private async void LiveStop_Click(object sender, RoutedEventArgs e) => await host.StopAutomationAsync();

	private void Open_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
		host.RestoreWindow();
	}

	private void Mini_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
		host.ShowMini();
	}

	private void Sleep_Click(object sender, RoutedEventArgs e)
	{
		if (host.SleepActive) host.StartSleepTimer(0);
		else host.StartSleepTimer(30);
		Sync();
	}
}
