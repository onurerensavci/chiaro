// Windows sürümündeki mod simgelerinin (Path verisi + renkler) Avalonia karşılığı.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace HueStudio.Ui;

internal static class ModeIcons
{
	public static string? Data(string mode) => mode switch
	{
		"Manuel" => "M3,7 H21 M3,17 H21 M8,4 V10 M16,14 V20",
		"Ekran" => "M3,4 H21 V16 H3 Z M9,20 H15 M12,16 V20",
		"Ses" => "M4,10 V14 M8,7 V17 M12,4 V20 M16,8 V16 M20,11 V13",
		"Spotify" => "M12,2 A10,10 0 1 1 11.99,2 Z M6.5,9.2 C10,8 14.5,8.3 17.8,10.2 M7.2,12.4 C10.2,11.5 13.8,11.8 16.6,13.3 M7.8,15.4 C10.3,14.8 13,15 15.2,16.1",
		"Gökkuşağı" => "M2,18 A10,10 0 0 1 22,18 M6,18 A6,6 0 0 1 18,18 M10,18 A2,2 0 0 1 14,18",
		"Şömine" => "M12,2 C14,7 19,9 19,15 A7,7 0 0 1 5,15 C5,11 8,9 9,6 C10,9 11,10 12,11 C13,8 12,5 12,2 Z",
		"Gün ışığı" => "M2,18 H22 M6,18 A6,6 0 0 1 18,18 M12,6 V8 M4.5,10.5 L6,12 M19.5,10.5 L18,12",
		_ => null,
	};

	private static IBrush? Stroke(string mode) => mode switch
	{
		"Ekran" => new SolidColorBrush(Color.Parse("#64D2FF")),
		"Ses" => new SolidColorBrush(Color.Parse("#30D158")),
		"Spotify" => new SolidColorBrush(Color.Parse("#1ED760")),
		"Gün ışığı" => new SolidColorBrush(Color.Parse("#FFD27A")),
		"Gökkuşağı" => new LinearGradientBrush
		{
			StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
			EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
			GradientStops =
			{
				new GradientStop(Color.Parse("#FF453A"), 0), new GradientStop(Color.Parse("#FFD60A"), 0.3),
				new GradientStop(Color.Parse("#30D158"), 0.55), new GradientStop(Color.Parse("#0A84FF"), 0.8),
				new GradientStop(Color.Parse("#BF5AF2"), 1),
			},
		},
		"Şömine" => new LinearGradientBrush
		{
			StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
			EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
			GradientStops = { new GradientStop(Color.Parse("#FF3B30"), 0), new GradientStop(Color.Parse("#FF9F0A"), 0.6), new GradientStop(Color.Parse("#FFD60A"), 1) },
		},
		_ => null,
	};

	public static Control Icon(string mode, double size)
	{
		Path p = new Path
		{
			Data = Geometry.Parse(Data(mode) ?? "M4,12 H20"),
			StrokeThickness = mode is "Ses" or "Gökkuşağı" ? 2.2 : mode == "Spotify" ? 1.9 : 2,
			StrokeLineCap = PenLineCap.Round,
			StrokeJoin = PenLineJoin.Round,
			Stretch = Stretch.None,
			Width = 24,
			Height = 24,
		};
		IBrush? brush = Stroke(mode);
		if (brush != null) p.Stroke = brush;
		else p.Bind(Shape.StrokeProperty, p.GetResourceObservable("TextMain"));
		return new Viewbox { Width = size, Height = size, Child = p, HorizontalAlignment = HorizontalAlignment.Center };
	}
}
