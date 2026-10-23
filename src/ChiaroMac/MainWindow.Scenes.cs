#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HueStudio.Ui;
using MColor = System.Windows.Media.Color;

namespace HueStudio;

public partial class MainWindow
{
	private List<Scene> Scenes
	{
		get
		{
			if (settings.Scenes == null)
			{
				settings.Scenes = Scene.Defaults();
				settings.ScenesVersion = 2;
			}
			return settings.Scenes;
		}
	}

	internal IReadOnlyList<Scene> SceneList => Scenes;

	private void RebuildScenes()
	{
		ScenesHost.Children.Clear();
		foreach (Scene s in Scenes) ScenesHost.Children.Add(MakeSceneTile(s));
		RebuildTrayMenu();
	}

	internal static Control MakeSceneBadge(Scene s, double size)
	{
		MColor a = s.Accent;
		Grid dot = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
		dot.Children.Add(new Ellipse
		{
			Fill = new RadialGradientBrush
			{
				GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
				GradientStops = { new GradientStop(Color.FromArgb(150, a.R, a.G, a.B), 0), new GradientStop(Color.FromArgb(50, a.R, a.G, a.B), 1) },
			},
			Stroke = new SolidColorBrush(Color.FromArgb(110, a.R, a.G, a.B)),
			StrokeThickness = 1,
		});
		if (!string.IsNullOrWhiteSpace(s.Emoji))
		{
			dot.Children.Add(new TextBlock { Text = s.Emoji, FontSize = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
		}
		return dot;
	}

	private Button MakeSceneTile(Scene s)
	{
		bool selected = s.Id == activeSceneId;
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
		text.Children.Add(new TextBlock { Text = s.DisplayName, FontWeight = FontWeight.Bold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
		bool soon = s.Kind == "Mod" && !ModeAvailable(s.Mode);
		text.Children.Add(new TextBlock { Classes = { "sub" }, FontSize = 10, Text = soon ? s.Subtitle + " • " + L.T("mac_soon") : s.Subtitle, TextTrimming = TextTrimming.CharacterEllipsis });
		DockPanel content = new DockPanel();
		content.Children.Add(MakeSceneBadge(s, 32));
		content.Children.Add(text);
		if (s.IsModified)
		{
			Ellipse mark = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.Parse("#0A84FF")), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 0, 0) };
			ToolTip.SetTip(mark, L.T("scene_edited"));
			DockPanel.SetDock(mark, Dock.Right);
			content.Children.Insert(0, mark);
		}
		Button b = new Button { Classes = { "tile" }, Width = 164, Height = 56, Content = content };
		if (selected) b.Classes.Add("selected");
		if (soon) b.Classes.Add("soon");
		b.Click += async (_, _) => await ApplySceneAsync(s);
		ContextMenu menu = new ContextMenu();
		void Add(string header, Action act, bool enabled = true)
		{
			MenuItem mi = new MenuItem { Header = header, IsEnabled = enabled };
			mi.Click += (_, _) => act();
			menu.Items.Add(mi);
		}
		int index = Scenes.IndexOf(s);
		Add(L.T("apply"), () => _ = ApplySceneAsync(s));
		menu.Items.Add(new Separator());
		Add(L.T("rename_dots"), () => _ = RenameScene(s));
		Add(L.T("change_emoji"), () => _ = ChangeSceneEmoji(s));
		Add(L.T("update_from_current"), () => UpdateSceneFromCurrent(s), ble.Ready);
		if (s.IsModified) Add(L.T("reset_default"), () => ResetScene(s));
		menu.Items.Add(new Separator());
		Add(L.T("move_up"), () => MoveScene(s, -1), index > 0);
		Add(L.T("move_down"), () => MoveScene(s, +1), index < Scenes.Count - 1);
		menu.Items.Add(new Separator());
		Add(L.T("delete"), () => DeleteScene(s));
		b.ContextMenu = menu;
		return b;
	}

	internal async Task ApplySceneAsync(Scene s)
	{
		if (!ble.Ready && !await EnsureConnectedAsync())
		{
			DetailText.Text = L.T("connect_first");
			return;
		}
		activeSceneId = s.Id;
		if (s.Kind == "Kapat")
		{
			await SetPower(false);
		}
		else if (s.Kind == "Mod" && s.Mode != null)
		{
			ApplyModeOptions(s.Mode, s.ModeOptions);
			if (!ModeSupported(s.Mode))
			{
				DetailText.Text = ModeAvailable(s.Mode) ? L.T("connect_first") : L.Mode(s.Mode) + ": " + L.T("mac_soon_desc");
				RebuildScenes();
				return;
			}
			await StartMode(s.Mode, s.Brightness);
		}
		else
		{
			bool white = s.Kind == "Beyaz";
			MColor c = s.Accent;
			bool ok = await Run(s.DisplayName + "…", async ct =>
			{
				await ble.Write(2, Protocol.Power(on: true), ct);
				powerOn = true;
				if (ble.Supports(3)) await ble.Write(3, Protocol.Brightness(s.Brightness), ct);
				if (white && ble.Supports(4)) await ble.Write(4, Protocol.Temperature(s.Kelvin), ct);
				else if (ble.Supports(5)) await ble.Write(5, Protocol.Rgb(c.R, c.G, c.B), ct);
			});
			if (ok)
			{
				SetSilently(BrightnessSlider, Math.Clamp(s.Brightness, 1, 100));
				if (white) SetSilently(TemperatureSlider, Math.Clamp(s.Kelvin, 2200, 6500));
				lastWasWhite = white && ble.Supports(4);
				currentColor = c;
				colorKnown = true;
				liveLast.Clear();
				StatusText.Text = L.F("connected_to", connectedName);
				DetailText.Text = L.F("applied_x", s.DisplayName);
				StateText.Text = s.Subtitle;
				UpdateBulbVisual();
				UpdateControls();
			}
		}
		RebuildScenes();
	}

	internal Task ApplySceneById(string id)
	{
		Scene? s = Scenes.FirstOrDefault(x => x.Id == id);
		return s == null ? Task.CompletedTask : ApplySceneAsync(s);
	}

	private Scene CaptureScene(string name)
	{
		Scene s = new Scene { Name = name, Brightness = Math.Round(BrightnessSlider.Value) };
		if (modeName.Length > 0 && UserModes.Contains(modeName))
		{
			s.Kind = "Mod";
			s.Mode = modeName;
			s.ModeOptions = modeName switch
			{
				"Ekran" => JsonSerializer.Serialize(settings.Screen),
				"Ses" => JsonSerializer.Serialize(settings.Audio),
				"Spotify" => JsonSerializer.Serialize(settings.Spotify),
				"Gökkuşağı" => JsonSerializer.Serialize(settings.Rainbow),
				"Şömine" => JsonSerializer.Serialize(settings.Fire),
				_ => JsonSerializer.Serialize(settings.Daylight),
			};
		}
		else if (powerOn == false)
		{
			s.Kind = "Kapat";
		}
		else if (lastWasWhite)
		{
			s.Kind = "Beyaz";
			s.Kelvin = Math.Round(TemperatureSlider.Value);
		}
		else
		{
			s.Kind = "Renk";
			s.Color = ColorMath.Hex(colorKnown ? currentColor : customColor);
		}
		return s;
	}

	private async void SceneSave_Click(object? sender, RoutedEventArgs e)
	{
		var r = await Dialogs.AskNameEmoji(this, L.T("scene_name"), L.F("scene_n", Scenes.Count + 1), "✨");
		if (r == null || string.IsNullOrWhiteSpace(r.Value.Name)) return;
		string name = r.Value.Name.Trim();
		Scene s = CaptureScene(name.Length > 24 ? name[..24] : name);
		s.Emoji = string.IsNullOrWhiteSpace(r.Value.Emoji) ? null : r.Value.Emoji;
		Scenes.Add(s);
		activeSceneId = s.Id;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("saved_x", s.DisplayName);
	}

	private async Task RenameScene(Scene s)
	{
		var r = await Dialogs.AskNameEmoji(this, L.T("rename"), s.DisplayName, s.Emoji);
		if (r == null || string.IsNullOrWhiteSpace(r.Value.Name)) return;
		string n = r.Value.Name.Trim();
		if (n != s.DisplayName) s.Name = n.Length > 24 ? n[..24] : n;
		s.Emoji = string.IsNullOrWhiteSpace(r.Value.Emoji) ? null : r.Value.Emoji;
		SaveSettings();
		RebuildScenes();
	}

	private async Task ChangeSceneEmoji(Scene s)
	{
		string? e = await Dialogs.PickEmoji(this, L.F("emoji_for", s.DisplayName), s.Emoji);
		if (e == null) return;
		s.Emoji = e;
		SaveSettings();
		RebuildScenes();
	}

	private void UpdateSceneFromCurrent(Scene s)
	{
		Scene now = CaptureScene(s.Name);
		now.Id = s.Id;
		now.BuiltIn = s.BuiltIn;
		now.Emoji = s.Emoji;
		int i = Scenes.IndexOf(s);
		if (i >= 0) Scenes[i] = now;
		activeSceneId = now.Id;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("updated_x", s.DisplayName);
	}

	private void ResetScene(Scene s)
	{
		Scene? o = s.Original;
		if (o == null) return;
		int i = Scenes.IndexOf(s);
		if (i >= 0) Scenes[i] = o;
		SaveSettings();
		RebuildScenes();
		DetailText.Text = L.F("reset_x", o.DisplayName);
	}

	private void MoveScene(Scene s, int delta)
	{
		int i = Scenes.IndexOf(s);
		int j = Math.Clamp(i + delta, 0, Scenes.Count - 1);
		if (i < 0 || i == j) return;
		Scenes.RemoveAt(i);
		Scenes.Insert(j, s);
		SaveSettings();
		RebuildScenes();
	}

	private void DeleteScene(Scene s)
	{
		Scenes.Remove(s);
		foreach (ScheduleItem item in settings.Schedules.Where(x => x.SceneId == s.Id)) item.SceneId = null;
		SaveSettings();
		RebuildScenes();
		RebuildSchedules();
		DetailText.Text = L.F("deleted_x", s.DisplayName);
	}

	private async void ScenesMore_Click(object? sender, RoutedEventArgs e)
	{
		int choice = await Dialogs.Choose(this, L.T("reset_all_scenes"), L.T("reset_all_question"), L.T("only_builtin"), L.T("all"), L.T("cancel"));
		if (choice == 0)
		{
			List<Scene> defaults = Scene.Defaults();
			for (int i = 0; i < Scenes.Count; i++)
			{
				Scene? d = defaults.FirstOrDefault(x => x.Id == Scenes[i].Id && Scenes[i].BuiltIn);
				if (d != null) Scenes[i] = d;
			}
			foreach (Scene d in defaults.Where(d => Scenes.All(x => x.Id != d.Id))) Scenes.Add(d);
			DetailText.Text = L.T("builtin_reset_done");
		}
		else if (choice == 1)
		{
			settings.Scenes = Scene.Defaults();
			DetailText.Text = L.T("all_reset_done");
		}
		else
		{
			return;
		}
		SaveSettings();
		RebuildScenes();
		RebuildSchedules();
	}
}
