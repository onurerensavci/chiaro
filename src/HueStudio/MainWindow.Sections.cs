#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HueStudio;

/// <summary>Kart "kulakçıkları" (aç/kapa), mod varsayılanları, sahne menüsü.</summary>
public partial class MainWindow
{
	private readonly Dictionary<string, (FrameworkElement Body, Button Chevron)> sections = new();
	private bool ModesVisible => IsExpanded("modes");

	private void InitSections()
	{
		settings.Sections ??= new Dictionary<string, bool>();
		void Reg(string key, FrameworkElement body, Button chev) => sections[key] = (body, chev);
		Reg("device", DeviceBody, DeviceChev);
		Reg("bulb", BulbBody, BulbChev);
		Reg("colors", ColorsBody, ColorsChev);
		Reg("general", GeneralBody, GeneralChev);
		Reg("scenes", ScenesBody, ScenesChev);
		Reg("modes", ModesBody, ModesChev);
		if (settings.Sections.TryGetValue("studio", out bool studio))
		{
			// Birleşik karttan ayrı kartlara.
			settings.Sections.Remove("studio");
			settings.Sections.TryAdd("scenes", studio);
			settings.Sections.TryAdd("modes", studio);
		}
		Reg("timer", TimerBody, TimerChev);
		Reg("sleep", SleepBody, SleepChev);
		Reg("wake", WakeBody, WakeChev);
		Reg("programs", ProgramsBody, ProgramsChev);
		Reg("notify", NotifyBody, NotifyChev);
		foreach (string key in sections.Keys.ToList())
		{
			SetSectionState(key, IsExpanded(key), animate: false);
		}
	}

	private bool IsExpanded(string key) => settings.Sections == null || !settings.Sections.TryGetValue(key, out bool v) || v;

	private void Section_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is string key && sections.ContainsKey(key))
		{
			SetSectionState(key, !IsExpanded(key), animate: true);
			SaveSettings();
		}
	}

	private void SetSectionState(string key, bool expanded, bool animate)
	{
		if (!sections.TryGetValue(key, out var sec)) return;
		settings.Sections ??= new Dictionary<string, bool>();
		settings.Sections[key] = expanded;
		Anim.Collapse(sec.Body, expanded, animate && Anim.Enabled);
		sec.Chevron.ApplyTemplate();
		if (sec.Chevron.Template?.FindName("Glyph", sec.Chevron) is Path glyph)
		{
			if (glyph.RenderTransform is not RotateTransform rot || rot.IsFrozen)
			{
				rot = new RotateTransform();
				glyph.RenderTransform = rot;
			}
			double angle = expanded ? 0 : -90;
			if (animate && Anim.Enabled)
			{
				rot.BeginAnimation(RotateTransform.AngleProperty, Anim.To(angle, 260));
			}
			else
			{
				rot.BeginAnimation(RotateTransform.AngleProperty, null);
				rot.Angle = angle;
			}
		}
		sec.Chevron.ToolTip = expanded ? "Daralt" : "Genişlet";
		if (key == "modes") UpdatePreviewTimer();
	}

	// ---- Mod ayarlarını varsayılana döndür ----

	private void ModeDefaults_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is not string mode) return;
		switch (mode)
		{
			case "Ekran": settings.Screen = new ScreenOptions(); break;
			case "Ses": settings.Audio = new AudioOptions(); break;
			case "Spotify": settings.Spotify = new SpotifyOptions(); break;
			case "Gökkuşağı": settings.Rainbow = new RainbowOptions(); break;
			case "Şömine": settings.Fire = new FireOptions(); break;
			case "Gün ışığı": settings.Daylight = new DaylightOptions(); break;
			default: return;
		}
		RebindModeOptions();
		SaveSettings();
		DetailText.Text = $"{mode} ayarları varsayılana döndü.";
	}

	// ---- Sahne menüsü (•••) ----

	private void SceneMore_Click(object sender, RoutedEventArgs e)
	{
		ContextMenu menu = new ContextMenu { PlacementTarget = SceneMoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
		MenuItem reset = new MenuItem { Header = "Tüm sahneleri sıfırla…" };
		reset.Click += (_, _) => ResetAllScenes();
		menu.Items.Add(reset);
		menu.IsOpen = true;
	}

	private void ResetAllScenes()
	{
		int? choice = ChoiceDialog.Ask(this, "Tüm sahneleri sıfırla",
			"Hazır sahneler ilk hâline döner. Kendi sahnelerin de silinsin mi?",
			"Yalnızca hazır sahneler", "Hepsi");
		if (choice == null) return;
		List<Scene> custom = choice == 0 ? Scenes.Where(x => !x.BuiltIn).ToList() : new List<Scene>();
		if (choice == 1)
		{
			foreach (ScheduleItem item in settings.Schedules.Where(x => x.SceneId != null && Scene.Defaults().All(d => d.Id != x.SceneId)))
			{
				item.SceneId = null;
			}
		}
		settings.Scenes = Scene.Defaults().Concat(custom).ToList();
		settings.ScenesVersion = 2;
		activeSceneId = null;
		SaveSettings();
		RebuildScenes();
		RebuildSchedules();
		DetailText.Text = choice == 0 ? "Hazır sahneler sıfırlandı." : "Tüm sahneler sıfırlandı.";
	}
}
