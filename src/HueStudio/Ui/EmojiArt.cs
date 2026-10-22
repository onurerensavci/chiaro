#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HueStudio;

/// <summary>
/// Renkli emoji. WPF emojileri tek renk çizer; Emoji.Wpf, Windows'taki Segoe UI Emoji
/// yazı tipinin renk katmanlarını (COLR/CPAL) okuyup vektör çizim üretir. Yazı tipi yoksa
/// düz (tek renk) metne düşer.
/// </summary>
internal static class EmojiArt
{
	private static readonly Dictionary<string, ImageSource?> cache = new();
	private static bool? available;
	internal static string LastError = "";

	internal static string Diagnose(string e)
	{
		try
		{
			bool can = Emoji.Wpf.EmojiData.Typeface.CanRender(e);
			DrawingImage di = new DrawingImage();
			Emoji.Wpf.Image.SetSource(di, e);
			return $"can={can} drawing={(di.Drawing == null ? "null" : di.Drawing.Bounds.ToString())} glyphs={string.Join(",", Emoji.Wpf.EmojiData.Typeface.MakeGlyphIndexList(e))}";
		}
		catch (Exception ex)
		{
			return "diag " + ex;
		}
	}

	public static bool ColorAvailable
	{
		get
		{
			if (available == null)
			{
				try { available = ColrEmoji.Available || (Emoji.Wpf.EmojiData.Typeface != null && Emoji.Wpf.EmojiData.Typeface.CanRender("😀")); }
				catch { available = false; }
			}
			return available.Value;
		}
	}

	public static ImageSource? Source(string? emoji)
	{
		if (string.IsNullOrWhiteSpace(emoji)) return null;
		if (cache.TryGetValue(emoji, out ImageSource? hit)) return hit;
		ImageSource? result = null;
		try
		{
			// 1) Kendi COLR çizicimiz (WPF GlyphTypeface + COLR/CPAL), 2) Emoji.Wpf, 3) düz metin.
			Drawing? own = ColrEmoji.Render(emoji);
			if (own != null)
			{
				DrawingImage oi = new DrawingImage(own);
				oi.Freeze();
				cache[emoji] = oi;
				return oi;
			}
			if (ColorAvailable && Emoji.Wpf.EmojiData.Typeface.CanRender(emoji.Replace("\uFE0F", "")) || ColorAvailable && Emoji.Wpf.EmojiData.Typeface.CanRender(emoji))
			{
				DrawingImage di = new DrawingImage();
				Emoji.Wpf.Image.SetSource(di, emoji);
				if (di.Drawing != null && !di.Drawing.Bounds.IsEmpty && di.Drawing.Bounds.Width > 0)
				{
					di.Freeze();
					result = di;
				}
			}
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
			result = null;
		}
		if (result == null && LastError.Length == 0) LastError = "no drawing for " + emoji;
		cache[emoji] = result;
		return result;
	}

	/// <summary>Kare bir alanda emoji: renkli çizim ya da yedek metin.</summary>
	public static FrameworkElement View(string? emoji, double size)
	{
		ImageSource? src = Source(emoji);
		if (src != null)
		{
			Image img = new Image { Source = src, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
			RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
			return img;
		}
		TextBlock t = new TextBlock
		{
			Text = emoji ?? "",
			FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
			FontSize = size * 0.78,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			TextAlignment = TextAlignment.Center,
			IsHitTestVisible = false,
		};
		t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
		return new Grid { Width = size, Height = size, Children = { t } };
	}

	/// <summary>Tepsi menüsü (WinForms) için bitmap.</summary>
	public static System.Drawing.Bitmap? Bitmap(string? emoji, int px)
	{
		if (string.IsNullOrWhiteSpace(emoji)) return null;
		try
		{
			FrameworkElement v = View(emoji, px);
			if (v is TextBlock or Grid) v.SetValue(TextBlock.ForegroundProperty, Brushes.White);
			v.Measure(new Size(px, px));
			v.Arrange(new Rect(0, 0, px, px));
			v.UpdateLayout();
			RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
			rtb.Render(v);
			PngBitmapEncoder enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(rtb));
			MemoryStream ms = new MemoryStream();
			enc.Save(ms);
			ms.Position = 0;
			return new System.Drawing.Bitmap(ms);
		}
		catch
		{
			return null;
		}
	}
}

/// <summary>Cam emoji seçici. Dönen: seçilen emoji, "" (emojisiz) veya null (vazgeç).</summary>
internal sealed class EmojiPicker : Window
{
	private string? result;
	private readonly TextBox custom = new TextBox();

	private EmojiPicker(Window? owner, string? current, string title)
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
		Content = new ContentControl { Style = (Style)Application.Current.FindResource("FloatGlass"), Padding = new Thickness(18, 14, 18, 14), Content = Build(current, title) };
		MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) try { DragMove(); } catch (InvalidOperationException) { } };
		Loaded += (_, _) => Keyboard.Focus(this);
	}

	internal FrameworkElement Build(string? current, string title)
	{
		StackPanel root = new StackPanel();
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
		TextBlock hint = new TextBlock { Text = "Birini seç ya da kendin yaz.", FontSize = 11, Margin = new Thickness(0, 2, 0, 10) };
		hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		root.Children.Add(hint);
		UniformGrid grid = new UniformGrid { Columns = 9 };
		foreach (string e in Scene.EmojiChoices)
		{
			Button b = new Button
			{
				Width = 40,
				Height = 40,
				Padding = new Thickness(0),
				Margin = new Thickness(0, 0, 4, 4),
				Content = EmojiArt.View(e, 22),
				ToolTip = e,
			};
			if (e == current) b.SetResourceReference(BorderBrushProperty, "Accent");
			string pick = e;
			b.Click += (_, _) => { result = pick; DialogResult = true; };
			grid.Children.Add(b);
		}
		root.Children.Add(grid);
		StackPanel customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		DockPanel row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
		Button ok = new Button { Content = "Tamam", IsDefault = true, Margin = new Thickness(0) };
		ok.SetResourceReference(StyleProperty, "Primary");
		ok.Click += (_, _) => { string t = custom.Text.Trim(); result = t.Length > 8 ? t[..8] : t; DialogResult = true; };
		DockPanel.SetDock(ok, Dock.Right);
		Button none = new Button { Content = "Emojisiz" };
		none.Click += (_, _) => { result = ""; DialogResult = true; };
		DockPanel.SetDock(none, Dock.Right);
		Button cancel = new Button { Content = "Vazgeç", IsCancel = true };
		DockPanel.SetDock(cancel, Dock.Right);
		custom.Text = Scene.EmojiChoices.Contains(current) ? "" : current ?? "";
		custom.Width = 90;
		custom.MaxLength = 8;
		custom.ToolTip = "Kendi emojin (Win + . ile seç)";
		custom.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI");
		row.Children.Add(ok);
		row.Children.Add(cancel);
		DockPanel.SetDock(none, Dock.Left);
		none.Margin = new Thickness(0);
		row.Children.Add(none);
		TextBlock label = new TextBlock { Text = "Kendi emojin", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 10, 0) };
		label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		customRow.Children.Add(label);
		customRow.Children.Add(custom);
		TextBlock tip = new TextBlock { Text = "Win + . ile emoji paneli açılır.", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(10, 0, 0, 0) };
		tip.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		customRow.Children.Add(tip);
		root.Children.Add(customRow);
		root.Children.Add(row);
		return root;
	}

	public static string? Ask(Window? owner, string? current, string title = "Emoji seç")
	{
		EmojiPicker p = new EmojiPicker(owner, current, title);
		return p.ShowDialog() == true ? p.result : null;
	}

	/// <summary>Duman testi için içerik.</summary>
	internal static FrameworkElement PreviewForTest(string? current)
	{
		EmojiPicker p = new EmojiPicker(null, current, "Emoji seç");
		FrameworkElement c = (FrameworkElement)p.Content;
		p.Content = null;
		p.Close();
		return c;
	}
}
