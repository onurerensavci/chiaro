#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HueStudio.Ui;

/// <summary>Basit modal pencereler (bilgi, ad + emoji, emoji seçici, çoktan seçmeli).</summary>
public static class Dialogs
{
	private static Window Shell(string title, Control content, double width = 420)
	{
		Window w = new Window
		{
			Title = title,
			Width = width,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false,
			Content = new Border { Padding = new Thickness(20, 18), Child = content },
		};
		w.Classes.Add("dialog");
		w.Bind(Window.BackgroundProperty, w.GetResourceObservable("CardBg"));
		w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
		return w;
	}

	private static StackPanel Buttons(params Button[] buttons)
	{
		StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
		foreach (Button b in buttons) p.Children.Add(b);
		return p;
	}

	private static Button Btn(string text, bool primary)
	{
		Button b = new Button { Content = text, MinWidth = 80 };
		b.Classes.Add(primary ? "primary" : "pill");
		return b;
	}

	public static async Task Info(Window owner, string title, string text)
	{
		StackPanel root = new StackPanel { Spacing = 8 };
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold });
		root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18 });
		Button ok = Btn(L.T("ok"), true);
		root.Children.Add(Buttons(ok));
		Window w = Shell(title, root, 460);
		ok.Click += (_, _) => w.Close();
		await w.ShowDialog(owner);
	}

	public static async Task<(string Name, string? Emoji)?> AskNameEmoji(Window owner, string title, string name, string? emoji)
	{
		(string, string?)? result = null;
		StackPanel root = new StackPanel { Spacing = 10 };
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold });
		TextBox nameBox = new TextBox { Text = name, MaxLength = 24 };
		TextBox emojiBox = new TextBox { Text = emoji ?? "", Width = 70, MaxLength = 8, HorizontalContentAlignment = HorizontalAlignment.Center };
		Grid row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
		row.Children.Add(emojiBox);
		Grid.SetColumn(nameBox, 1);
		row.Children.Add(nameBox);
		root.Children.Add(row);
		WrapPanel picks = new WrapPanel();
		foreach (string e in Scene.EmojiChoices)
		{
			Button b = new Button { Content = e, Width = 34, Height = 32, Padding = new Thickness(0), FontSize = 16, Margin = new Thickness(0, 0, 4, 4), HorizontalContentAlignment = HorizontalAlignment.Center };
			b.Classes.Add("pill");
			b.Click += (_, _) => emojiBox.Text = e;
			picks.Children.Add(b);
		}
		root.Children.Add(picks);
		Button ok = Btn(L.T("save"), true), cancel = Btn(L.T("cancel"), false);
		root.Children.Add(Buttons(cancel, ok));
		Window w = Shell(title, root, 460);
		ok.Click += (_, _) => { result = (nameBox.Text ?? "", emojiBox.Text); w.Close(); };
		cancel.Click += (_, _) => w.Close();
		nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = (nameBox.Text ?? "", emojiBox.Text); w.Close(); } };
		w.Opened += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
		await w.ShowDialog(owner);
		return result;
	}

	public static async Task<string?> PickEmoji(Window owner, string title, string? current)
	{
		string? result = null;
		StackPanel root = new StackPanel { Spacing = 10 };
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold });
		root.Children.Add(new TextBlock { Text = L.T("emoji_hint"), Classes = { "sub" } });
		WrapPanel picks = new WrapPanel();
		Window? w = null;
		foreach (string e in Scene.EmojiChoices)
		{
			Button b = new Button { Content = e, Width = 38, Height = 36, Padding = new Thickness(0), FontSize = 18, Margin = new Thickness(0, 0, 4, 4), HorizontalContentAlignment = HorizontalAlignment.Center };
			b.Classes.Add(e == current ? "primary" : "pill");
			b.Click += (_, _) => { result = e; w?.Close(); };
			picks.Children.Add(b);
		}
		root.Children.Add(picks);
		TextBox custom = new TextBox { Watermark = L.T("emoji_custom"), Width = 140, MaxLength = 8 };
		Button none = Btn(L.T("emoji_none"), false), ok = Btn(L.T("ok"), true), cancel = Btn(L.T("cancel"), false);
		StackPanel bottom = Buttons(none, cancel, ok);
		bottom.Children.Insert(0, custom);
		root.Children.Add(bottom);
		w = Shell(title, root, 470);
		none.Click += (_, _) => { result = ""; w.Close(); };
		ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(custom.Text)) result = custom.Text.Trim(); w.Close(); };
		cancel.Click += (_, _) => w.Close();
		await w.ShowDialog(owner);
		return result;
	}

	public static async Task<int> Choose(Window owner, string title, string text, params string[] options)
	{
		int result = -1;
		StackPanel root = new StackPanel { Spacing = 8 };
		root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold });
		root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
		Window? w = null;
		Button[] buttons = new Button[options.Length];
		for (int i = options.Length - 1; i >= 0; i--)
		{
			int index = i;
			Button b = Btn(options[i], i == 0);
			b.Click += (_, _) => { result = index == options.Length - 1 ? -1 : index; w?.Close(); };
			buttons[options.Length - 1 - i] = b;
		}
		root.Children.Add(Buttons(buttons));
		w = Shell(title, root, 440);
		await w.ShowDialog(owner);
		return result;
	}
}
