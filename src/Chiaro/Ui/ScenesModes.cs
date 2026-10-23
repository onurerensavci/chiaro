#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HueStudio;

/// <summary>
/// Sahneler | Modlar çiplerinin ortak üreticisi. Mini mod paneli ve tepsi hızlı paneli
/// aynı görünümü ve aynı davranışı (uygula / başlat-durdur, etkin olanı vurgula) kullanır.
/// </summary>
internal static class ScenesModes
{
	internal const string ScenesTab = "Sahneler";
	internal const string ModesTab = "Modlar";

	/// <summary>Sahne çiplerini doldurur; etkin sahne vurgulanır.</summary>
	internal static void FillScenes(Panel target, MainWindow host, int max = int.MaxValue)
	{
		target.Children.Clear();
		int n = 0;
		foreach (Scene s in host.SceneList)
		{
			if (n++ >= max) break;
			Scene scene = s;
			Button chip = SceneChip(scene, () => _ = host.ApplySceneAsync(scene));
			if (host.ActiveSceneId == scene.Id) Highlight(chip, scene.Accent);
			target.Children.Add(chip);
		}
	}

	/// <summary>Mod düğmelerini doldurur (ana penceredeki simgelerle); çalışan mod vurgulanır.
	/// compact: hızlı panelde yedi mod iki satıra sığsın diye daha sıkı çipler.</summary>
	internal static void FillModes(Panel target, MainWindow host, bool compact = false)
	{
		target.Children.Clear();
		string active = string.IsNullOrEmpty(host.ActiveMode) ? "Manuel" : host.ActiveMode;
		foreach (string m in MainWindow.MiniModes)
		{
			string mode = m;
			StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
			FrameworkElement icon = ModeIcon(mode, compact ? 13 : 15);
			icon.Margin = new Thickness(0, 0, compact ? 4 : 7, 0);
			content.Children.Add(icon);
			content.Children.Add(new TextBlock { Text = compact && mode == "Ekran" ? L.T("mode_screen_short") : L.Mode(mode), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
			Button b = new Button
			{
				Content = content,
				Margin = compact ? new Thickness(0, 0, 5, 8) : new Thickness(0, 0, 8, 8),
				Padding = compact ? new Thickness(7, 6, 8, 6) : new Thickness(11, 6, 12, 6),
			};
			b.SetResourceReference(FrameworkElement.StyleProperty, "Small");
			bool on = mode == active;
			b.ToolTip = mode == "Manuel" ? L.T("stop_mode") : on ? L.T("stop") : L.T("start");
			if (on) Highlight(b, ModeColor(mode));
			b.Click += async (_, _) => await host.ToggleModeAsync(mode);
			target.Children.Add(b);
		}
	}

	internal static Button SceneChip(Scene s, Action click)
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
		content.Children.Add(new TextBlock { Text = s.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
		Button b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), ToolTip = s.Subtitle };
		b.SetResourceReference(FrameworkElement.StyleProperty, "Small");
		b.Click += (_, _) => click();
		return b;
	}

	internal static void Highlight(Button b, Color c)
	{
		// Açık temada beyaz yazı açık zeminde okunmaz: koyulaştırılmış vurgu rengi kullan.
		bool light = Application.Current?.TryFindResource("Text") is SolidColorBrush t && (t.Color.R + t.Color.G + t.Color.B) < 300;
		b.BorderBrush = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
		b.BorderThickness = new Thickness(1.5);
		b.Background = new SolidColorBrush(Color.FromArgb(light ? (byte)70 : (byte)110, c.R, c.G, c.B));
		b.Foreground = light ? new SolidColorBrush(ColorMath.Lerp(c, Colors.Black, 0.62)) : Brushes.White;
		b.FontWeight = FontWeights.SemiBold;
	}

	internal static Color ModeColor(string mode) => mode switch
	{
		"Ekran" => Color.FromRgb(100, 210, 255),
		"Ses" => Color.FromRgb(48, 209, 88),
		"Spotify" => Color.FromRgb(30, 215, 96),
		"Gökkuşağı" => Color.FromRgb(191, 90, 242),
		"Şömine" => Color.FromRgb(255, 120, 40),
		"Gün ışığı" => Color.FromRgb(255, 210, 122),
		_ => Color.FromRgb(200, 205, 215),
	};

	/// <summary>Ana penceredeki mod kutucuklarının simgeleri, küçük boyutta.</summary>
	internal static FrameworkElement ModeIcon(string mode, double size)
	{
		string data = mode switch
		{
			"Ekran" => "M3,4 H21 V16 H3 Z M9,20 H15 M12,16 V20",
			"Ses" => "M4,10 V14 M8,7 V17 M12,4 V20 M16,8 V16 M20,11 V13",
			"Spotify" => "M12,2 A10,10 0 1 1 11.99,2 Z M6.5,9.2 C10,8 14.5,8.3 17.8,10.2 M7.2,12.4 C10.2,11.5 13.8,11.8 16.6,13.3 M7.8,15.4 C10.3,14.8 13,15 15.2,16.1",
			"Gökkuşağı" => "M2,18 A10,10 0 0 1 22,18 M6,18 A6,6 0 0 1 18,18 M10,18 A2,2 0 0 1 14,18",
			"Şömine" => "M12,2 C14,7 19,9 19,15 A7,7 0 0 1 5,15 C5,11 8,9 9,6 C10,9 11,10 12,11 C13,8 12,5 12,2 Z",
			"Gün ışığı" => "M2,18 H22 M6,18 A6,6 0 0 1 18,18 M12,6 V8 M4.5,10.5 L6,12 M19.5,10.5 L18,12",
			_ => "M3,7 H21 M3,17 H21 M8,4 V10 M16,14 V20",
		};
		Path p = new Path
		{
			Data = Geometry.Parse(data),
			StrokeThickness = mode == "Ses" || mode == "Gökkuşağı" ? 2.4 : 2.1,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			Stretch = Stretch.Uniform,
			Width = size,
			Height = size,
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = false,
		};
		if (mode == "Manuel") p.SetResourceReference(Shape.StrokeProperty, "Text");
		else p.Stroke = ModeBrush(mode);
		return p;
	}

	internal static Brush ModeBrush(string mode)
	{
		if (mode == "Gökkuşağı")
		{
			return new LinearGradientBrush(new GradientStopCollection
			{
				new GradientStop(Color.FromRgb(255, 69, 58), 0), new GradientStop(Color.FromRgb(255, 214, 10), 0.3),
				new GradientStop(Color.FromRgb(48, 209, 88), 0.55), new GradientStop(Color.FromRgb(10, 132, 255), 0.8),
				new GradientStop(Color.FromRgb(191, 90, 242), 1),
			}, 0);
		}
		if (mode == "Şömine")
		{
			return new LinearGradientBrush(new GradientStopCollection
			{
				new GradientStop(Color.FromRgb(255, 59, 48), 0), new GradientStop(Color.FromRgb(255, 159, 10), 0.6), new GradientStop(Color.FromRgb(255, 214, 10), 1),
			}, new Point(0, 1), new Point(0, 0));
		}
		return new SolidColorBrush(ModeColor(mode));
	}
}
