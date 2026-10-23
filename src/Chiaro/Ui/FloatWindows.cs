#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HueStudio;

/// <summary>Simgede tekerlek için küçük parlaklık göstergesi (OSD).</summary>
internal sealed class BrightnessOsd : Window
{
	private readonly TextBlock value = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 46, TextAlignment = TextAlignment.Right };
	private readonly Border fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Height = 6 };
	private readonly DispatcherTimer hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
	private const double BarWidth = 130;

	public BrightnessOsd()
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ShowInTaskbar = false;
		Topmost = true;
		ShowActivated = false;
		Focusable = false;
		SizeToContent = SizeToContent.WidthAndHeight;
		ResizeMode = ResizeMode.NoResize;
		SetResourceReference(ForegroundProperty, "Text");
		FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
		fill.SetResourceReference(Border.BackgroundProperty, "Accent");
		Border track = new Border { CornerRadius = new CornerRadius(3), Height = 6, Width = BarWidth, VerticalAlignment = VerticalAlignment.Center, Child = fill };
		track.SetResourceReference(Border.BackgroundProperty, "Track");
		Path sun = new Path
		{
			Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.6, Margin = new Thickness(0, 0, 12, 0),
			Data = Geometry.Parse("M12,7 A5,5 0 1 1 11.99,7 Z M12,1 V3 M12,21 V23 M1,12 H3 M21,12 H23 M4.2,4.2 L5.6,5.6 M18.4,18.4 L19.8,19.8 M4.2,19.8 L5.6,18.4 M18.4,5.6 L19.8,4.2"),
		};
		sun.SetResourceReference(Shape.StrokeProperty, "Text");
		StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
		row.Children.Add(sun);
		row.Children.Add(track);
		row.Children.Add(value);
		Content = new ContentControl { Style = (Style)Application.Current.FindResource("FloatGlass"), Padding = new Thickness(16, 12, 16, 12), Content = row };
		hide.Tick += (_, _) => { hide.Stop(); Hide(); };
	}

	public void ShowValue(double percent, Rect? iconRectDip)
	{
		value.Text = L.Pct(percent);
		fill.Width = Math.Clamp(percent / 100.0, 0.02, 1) * BarWidth;
		if (!IsVisible)
		{
			Show();
			UpdateLayout();
			Rect work = SystemParameters.WorkArea;
			double cx = iconRectDip.HasValue ? iconRectDip.Value.Left + iconRectDip.Value.Width / 2 : work.Right - ActualWidth / 2;
			Left = Math.Clamp(cx - ActualWidth / 2, work.Left, work.Right - ActualWidth);
			Top = work.Bottom - ActualHeight;
		}
		hide.Stop();
		hide.Start();
	}
}

/// <summary>Kısa metin girişi (sahne adı).</summary>
internal sealed class InputDialog : Window
{
	private readonly TextBox box = new TextBox();
	private string? emoji;

	private InputDialog(Window? owner, string title, string text, bool withEmoji = false, string? initialEmoji = null)
	{
		emoji = initialEmoji;
		Owner = owner;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.WidthAndHeight;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
		SetResourceReference(ForegroundProperty, "Text");
		FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
		FontSize = 14;
		box.Text = text;
		box.Width = 260;
		box.Margin = new Thickness(0, 10, 0, 14);
		box.SelectAll();
		Button ok = new Button { Content = L.T("save"), IsDefault = true, Margin = new Thickness(0) };
		ok.SetResourceReference(StyleProperty, "Primary");
		Button cancel = new Button { Content = L.T("cancel_alt"), IsCancel = true };
		ok.Click += (_, _) => { DialogResult = true; };
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(cancel);
		buttons.Children.Add(ok);
		StackPanel root = new StackPanel();
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
		if (withEmoji)
		{
			Button pick = new Button { Width = 44, Height = 40, Padding = new Thickness(0), Margin = new Thickness(0, 10, 8, 14), ToolTip = L.T("emoji_pick") };
			void Refresh() => pick.Content = string.IsNullOrWhiteSpace(emoji) ? new TextBlock { Text = "☺", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center } : EmojiArt.View(emoji, 22);
			Refresh();
			pick.Click += (_, _) =>
			{
				string? e = EmojiPicker.Ask(this, emoji);
				if (e != null) { emoji = e; Refresh(); }
			};
			box.Width = 216;
			DockPanel row = new DockPanel();
			row.Children.Add(pick);
			row.Children.Add(box);
			root.Children.Add(row);
		}
		else
		{
			root.Children.Add(box);
		}
		root.Children.Add(buttons);
		Content = new ContentControl { Style = (Style)Application.Current.FindResource("FloatGlass"), Padding = new Thickness(20, 16, 20, 16), Content = root };
		Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
		MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) try { DragMove(); } catch (InvalidOperationException) { } };
	}

	public static string? Ask(Window? owner, string title, string text)
	{
		InputDialog d = new InputDialog(owner, title, text);
		return d.ShowDialog() == true ? d.box.Text.Trim() : null;
	}

	/// <summary>Ad + emoji. Vazgeçilirse null.</summary>
	public static (string Name, string? Emoji)? AskWithEmoji(Window? owner, string title, string text, string? emoji)
	{
		InputDialog d = new InputDialog(owner, title, text, withEmoji: true, initialEmoji: emoji);
		return d.ShowDialog() == true ? (d.box.Text.Trim(), d.emoji) : null;
	}
}

/// <summary>Cam iletişim kutusu: iki seçenek + Vazgeç. Seçilen dizin (0/1) veya null.</summary>
internal sealed class ChoiceDialog : Window
{
	private int? result;

	private ChoiceDialog(Window? owner, string title, string text, string first, string second)
	{
		Owner = owner;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.WidthAndHeight;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
		SetResourceReference(ForegroundProperty, "Text");
		FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
		FontSize = 14;
		Button a = new Button { Content = first };
		Button b = new Button { Content = second, Margin = new Thickness(0) };
		b.SetResourceReference(StyleProperty, "Primary");
		Button cancel = new Button { Content = L.T("cancel_alt"), IsCancel = true };
		a.Click += (_, _) => { result = 0; DialogResult = true; };
		b.Click += (_, _) => { result = 1; DialogResult = true; };
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
		buttons.Children.Add(cancel);
		buttons.Children.Add(a);
		buttons.Children.Add(b);
		StackPanel root = new StackPanel { MaxWidth = 440 };
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
		TextBlock body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
		body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		root.Children.Add(body);
		root.Children.Add(buttons);
		Content = new ContentControl { Style = (Style)Application.Current.FindResource("FloatGlass"), Padding = new Thickness(20, 16, 20, 16), Content = root };
		MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
	}

	public static int? Ask(Window? owner, string title, string text, string first, string second)
	{
		ChoiceDialog d = new ChoiceDialog(owner, title, text, first, second);
		return d.ShowDialog() == true ? d.result : null;
	}
}
