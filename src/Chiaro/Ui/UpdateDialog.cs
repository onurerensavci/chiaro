#nullable enable
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HueStudio;

/// <summary>Cam güncelleme penceresi: sürüm + notlar, "Şimdi güncelle" / "Sonra", indirme ilerlemesi.</summary>
internal sealed class UpdateDialog : Window
{
	private readonly UpdateInfo info;
	private readonly Func<UpdateInfo, IProgress<double>, CancellationToken, Task<string?>>? run;
	private readonly Button now = new Button { Content = L.T("upd_now"), Margin = new Thickness(0), IsDefault = true };
	private readonly Button later = new Button { Content = L.T("upd_later") };
	private readonly Border track = new Border { CornerRadius = new CornerRadius(3), Height = 6 };
	private readonly Border fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
	private readonly TextBlock progressText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
	private readonly StackPanel progressBox = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
	private readonly TextBlock error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) };
	private CancellationTokenSource? cts;
	private bool busy;

	internal UpdateDialog(Window? owner, UpdateInfo info, Func<UpdateInfo, IProgress<double>, CancellationToken, Task<string?>>? run)
	{
		this.info = info;
		this.run = run;
		if (owner != null && owner.IsVisible) Owner = owner;
		Title = L.T("upd_title");
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.WidthAndHeight;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
		SetResourceReference(ForegroundProperty, "Text");
		FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
		FontSize = 14;

		now.SetResourceReference(StyleProperty, "Primary");
		now.Click += async (_, _) => await StartAsync();
		later.Click += (_, _) =>
		{
			if (busy) cts?.Cancel();
			else Close();
		};

		StackPanel root = new StackPanel { Width = 400 };
		DockPanel head = new DockPanel();
		Border badge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
		badge.SetResourceReference(Border.BackgroundProperty, "Accent");
		badge.Child = new TextBlock { Text = "v" + info.VersionText, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
		DockPanel.SetDock(badge, Dock.Right);
		head.Children.Add(badge);
		head.Children.Add(new TextBlock { Text = L.T("update_ready_short"), FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
		root.Children.Add(head);
		TextBlock sub = new TextBlock { Text = L.F("upd_current", Updater.CurrentText), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
		sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		root.Children.Add(sub);

		string notes = string.IsNullOrWhiteSpace(info.Notes) ? L.T("upd_no_notes") : info.Notes;
		TextBlock notesText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 20 };
		AddWithEmoji(notesText, notes);
		Border notesBox = new Border
		{
			CornerRadius = new CornerRadius(14),
			Padding = new Thickness(14, 10, 10, 10),
			Margin = new Thickness(0, 14, 0, 0),
			BorderThickness = new Thickness(1),
			Child = new ScrollViewer { Content = notesText, MaxHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) },
		};
		notesBox.SetResourceReference(Border.BackgroundProperty, "Control");
		notesBox.SetResourceReference(Border.BorderBrushProperty, "Stroke");
		root.Children.Add(notesBox);

		track.SetResourceReference(Border.BackgroundProperty, "Track");
		fill.SetResourceReference(Border.BackgroundProperty, "Accent");
		track.Child = fill;
		progressText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		progressBox.Children.Add(progressText);
		progressBox.Children.Add(track);
		root.Children.Add(progressBox);
		root.Children.Add(error);

		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
		buttons.Children.Add(later);
		buttons.Children.Add(now);
		root.Children.Add(buttons);

		Content = new ContentControl { Style = (Style)Application.Current.FindResource("FloatGlass"), Padding = new Thickness(22, 18, 22, 18), Content = root };
		MouseLeftButtonDown += (_, e) =>
		{
			if (e.OriginalSource is DependencyObject d && IsInScroll(d)) return;
			try { DragMove(); } catch (InvalidOperationException) { }
		};
		Closing += (_, e) => { if (busy) { e.Cancel = true; cts?.Cancel(); } };
	}

	/// <summary>Metni yazar; emojileri uygulamanın renkli emoji çizicisiyle satır içine koyar.</summary>
	internal static void AddWithEmoji(TextBlock target, string text)
	{
		StringBuilder run = new StringBuilder();
		void Flush()
		{
			if (run.Length == 0) return;
			target.Inlines.Add(new Run(run.ToString()));
			run.Clear();
		}
		TextElementEnumerator e = StringInfo.GetTextElementEnumerator(text);
		while (e.MoveNext())
		{
			string el = e.GetTextElement();
			if (el == "\n")
			{
				Flush();
				target.Inlines.Add(new LineBreak());
			}
			else if (IsEmoji(el))
			{
				Flush();
				FrameworkElement view = EmojiArt.View(el, 15);
				view.Margin = new Thickness(0, 0, 1, -2);
				target.Inlines.Add(new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Center });
			}
			else
			{
				run.Append(el);
			}
		}
		Flush();
	}

	private static bool IsEmoji(string el)
	{
		if (el.Length == 0) return false;
		int cp = char.ConvertToUtf32(el, 0);
		if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;
		if (el.Contains('\uFE0F') && cp > 0x2000) return true;
		return cp >= 0x2600 && cp <= 0x27BF && cp != 0x2713;
	}

	private static bool IsInScroll(DependencyObject d)
	{
		while (d != null)
		{
			if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
			d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
		}
		return false;
	}

	/// <summary>İndirme ilerlemesini gösterir (0..1).</summary>
	internal void ShowProgress(double p, string? text = null)
	{
		progressBox.Visibility = Visibility.Visible;
		error.Visibility = Visibility.Collapsed;
		double w = track.ActualWidth > 0 ? track.ActualWidth : 400;
		fill.Width = Math.Clamp(p, 0, 1) * w;
		progressText.Text = text ?? (p >= 1 ? L.T("upd_verifying") : L.F("upd_downloading", L.Pct(p * 100)) + (info.AssetSize > 0 ? $" • {info.AssetSize / 1048576.0:F0} MB" : ""));
	}

	internal void ShowError(string message)
	{
		progressBox.Visibility = Visibility.Collapsed;
		error.Text = message;
		error.Visibility = Visibility.Visible;
		now.Content = L.T("upd_retry");
		now.IsEnabled = true;
		later.Content = L.T("upd_later");
	}

	/// <summary>Ekran görüntüsü için: indirme sürüyormuş gibi düğmeleri ayarla.</summary>
	internal void SetBusyLook(bool on)
	{
		now.IsEnabled = !on;
		now.Content = on ? L.T("upd_updating") : L.T("upd_now");
		later.Content = on ? L.T("cancel") : L.T("upd_later");
	}

	private async Task StartAsync()
	{
		if (busy || run == null) return;
		busy = true;
		cts = new CancellationTokenSource();
		SetBusyLook(true);
		ShowProgress(0);
		string? failure;
		try
		{
			failure = await run(info, new Progress<double>(p => ShowProgress(p)), cts.Token);
		}
		catch (OperationCanceledException)
		{
			failure = L.T("upd_cancelled");
		}
		catch (Exception ex)
		{
			failure = L.F("upd_incomplete", ex.Message);
		}
		busy = false;
		cts.Dispose();
		cts = null;
		if (failure == null)
		{
			ShowProgress(1, L.T("upd_restarting"));
			return;
		}
		ShowError(failure);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			if (busy) cts?.Cancel();
			else Close();
			e.Handled = true;
			return;
		}
		base.OnKeyDown(e);
	}
}
