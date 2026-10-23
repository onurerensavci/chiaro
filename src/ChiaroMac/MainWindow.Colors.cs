#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

public partial class MainWindow
{
	private MColor customColor = MColor.FromRgb(0xFF, 0x8C, 0x1A);
	private bool updatingCustom;

	private Button MakeSwatch(MColor c, string name, double size, bool favorite)
	{
		Button b = new Button
		{
			Classes = { "swatch" },
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(size / 2),
			Background = new RadialGradientBrush
			{
				GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
				GradientStops =
				{
					new GradientStop(Color.FromRgb((byte)Math.Min(255, c.R + 50), (byte)Math.Min(255, c.G + 50), (byte)Math.Min(255, c.B + 50)), 0),
					new GradientStop(A(c), 0.7),
				},
			},
			BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x30, 0, 0, 0)),
		};
		ToolTip.SetTip(b, L.F(favorite ? "fav_tip_remove" : "fav_tip_add", name, ColorMath.Hex(c)).Replace("Sağ tık", "Sağ tık / Ctrl-tık").Replace("Right-click", "Right-click / Ctrl-click"));
		b.Click += async (_, _) => await ApplyColor(c);
		ContextMenu menu = new ContextMenu();
		MenuItem item = new MenuItem { Header = favorite ? L.T("delete") + " (" + L.T("mac_favorites") + ")" : L.T("fav_add_tip") };
		item.Click += (_, _) => { if (favorite) RemoveFavorite(c); else AddFavorite(c); };
		menu.Items.Add(item);
		b.ContextMenu = menu;
		return b;
	}

	private void BuildPalette()
	{
		PaletteHost.Children.Clear();
		foreach (var group in Palette.All.GroupBy(n => n.Group))
		{
			PaletteHost.Children.Add(new TextBlock { Classes = { "caps" }, Text = L.Upper(L.Color(group.Key)), Margin = new Thickness(0, 6, 0, 3) });
			WrapPanel wrap = new WrapPanel();
			foreach (NamedColor n in group) wrap.Children.Add(MakeSwatch(n.Color, L.Color(n.Name), 24, settings.Favorites.Contains(n.Hex, StringComparer.OrdinalIgnoreCase)));
			PaletteHost.Children.Add(wrap);
		}
	}

	private void RebuildPaletteBorders()
	{
		if (!initialized) return;
		BuildPalette();
		RebuildRecent();
		RebuildQuickSwatches();
	}

	private void RebuildQuickSwatches()
	{
		DockFavorites.Children.Clear();
		foreach (string hex in settings.Favorites.Take(12))
		{
			if (!ColorMath.TryParseHex(hex, out MColor c)) continue;
			Button b = MakeSwatch(c, Palette.NameOf(c), 20, favorite: true);
			b.Margin = new Thickness(0, 0, 6, 0);
			DockFavorites.Children.Add(b);
		}
		RebuildTrayMenu();
	}

	private void RebuildRecent()
	{
		RecentHost.Children.Clear();
		foreach (string hex in settings.Recent.Take(12))
		{
			if (ColorMath.TryParseHex(hex, out MColor c)) RecentHost.Children.Add(MakeSwatch(c, Palette.NameOf(c), 22, settings.Favorites.Contains(hex, StringComparer.OrdinalIgnoreCase)));
		}
		RecentEmpty.IsVisible = RecentHost.Children.Count == 0;
	}

	private void AddFavorite(MColor c)
	{
		string hex = ColorMath.Hex(c);
		if (!settings.Favorites.Contains(hex, StringComparer.OrdinalIgnoreCase))
		{
			settings.Favorites.Add(hex);
			SaveSettings();
		}
		DetailText.Text = L.F("fav_added", Palette.NameOf(c));
		BuildPalette();
		RebuildRecent();
		RebuildQuickSwatches();
	}

	private void RemoveFavorite(MColor c)
	{
		string hex = ColorMath.Hex(c);
		settings.Favorites.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
		SaveSettings();
		DetailText.Text = L.F("removed", Palette.NameOf(c));
		BuildPalette();
		RebuildRecent();
		RebuildQuickSwatches();
	}

	private void AddRecent(MColor c)
	{
		string hex = ColorMath.Hex(c);
		settings.Recent.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
		settings.Recent.Insert(0, hex);
		if (settings.Recent.Count > 12) settings.Recent.RemoveRange(12, settings.Recent.Count - 12);
		SaveSettings();
		RebuildRecent();
	}

	internal async Task ApplyColor(MColor c)
	{
		SetCustomFromColor(c, updateHex: true);
		if (!ble.Ready || !ble.Supports(5))
		{
			DetailText.Text = ble.Ready ? L.T("no_color_on_bulb") : L.T("connect_to_send");
			return;
		}
		activeSceneId = null;
		if (await Send(5, Protocol.Rgb(c.R, c.G, c.B)))
		{
			currentColor = c;
			colorKnown = true;
			lastWasWhite = false;
			liveLast.Clear();
			AddRecent(c);
			UpdateBulbVisual();
			UpdateControls();
			RebuildScenes();
		}
	}

	// ---- tekerlek / hex / doygunluk

	private void InitWheel()
	{
		Wheel.Changing += (h, s) =>
		{
			SetCustomFromHueSat(h, s);
			if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: false);
		};
		Wheel.Committed += (h, s) =>
		{
			SetCustomFromHueSat(h, s);
			if (ble.Ready && ble.Supports(5)) LiveColorSet(customColor, final: true);
		};
		SatSlider.PropertyChanged += (_, e) =>
		{
			if (e.Property != RangeBase.ValueProperty) return;
			SatLabel.Text = L.Pct(SatSlider.Value);
			if (updatingCustom) return;
			SetCustomFromHueSat(Wheel.Hue, SatSlider.Value / 100.0);
		};
		OnRelease(SatSlider, () => { if (ble.Ready && ble.Supports(5) && !updatingCustom) LiveColorSet(customColor, final: true); });
		SetCustomFromColor(customColor, updateHex: true);
	}

	private void SetCustomFromColor(MColor c, bool updateHex)
	{
		updatingCustom = true;
		try
		{
			customColor = c;
			(double h, double s, _) = ColorMath.ToHsv(c);
			Wheel.SetHueSat(h, s);
			SatSlider.Value = Math.Round(s * 100);
			SatLabel.Text = L.Pct(SatSlider.Value);
			CustomPreview.Fill = new SolidColorBrush(A(c));
			if (updateHex) HexBox.Text = ColorMath.Hex(c);
		}
		finally
		{
			updatingCustom = false;
		}
	}

	private void SetCustomFromHueSat(double hue, double sat)
	{
		MColor c = ColorMath.FromHsv(hue, sat, 1);
		updatingCustom = true;
		try
		{
			customColor = c;
			Wheel.SetHueSat(hue, sat);
			SatSlider.Value = Math.Round(sat * 100);
			SatLabel.Text = L.Pct(SatSlider.Value);
			CustomPreview.Fill = new SolidColorBrush(A(c));
			HexBox.Text = ColorMath.Hex(c);
		}
		finally
		{
			updatingCustom = false;
		}
	}

	private void HexBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			CommitHex(apply: true);
			e.Handled = true;
		}
	}

	private void HexBox_LostFocus(object? sender, RoutedEventArgs e) => CommitHex(apply: false);

	private async void CommitHex(bool apply)
	{
		if (ColorMath.TryParseHex(HexBox.Text, out MColor c))
		{
			SetCustomFromColor(c, updateHex: true);
			if (apply) await ApplyColor(c);
		}
		else
		{
			HexBox.Text = ColorMath.Hex(customColor);
		}
	}

	private void AddFavorite_Click(object? sender, RoutedEventArgs e) => AddFavorite(customColor);

	private async void ApplyCustom_Click(object? sender, RoutedEventArgs e) => await ApplyColor(customColor);
}
