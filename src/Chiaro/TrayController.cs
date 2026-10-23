#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace HueStudio;

/// <summary>
/// Saat yanındaki (sistem tepsisi) menü: tek aç/kapa düğmesi, renk örnekli alt menüler, Şömine modu.
/// </summary>
public sealed class TrayController : IDisposable
{
	private sealed class GlassRenderer : ToolStripProfessionalRenderer
	{
		private readonly bool dark;

		public GlassRenderer(bool dark = true)
			: base(new MenuColors(dark))
		{
			this.dark = dark;
			RoundedEdges = false;
		}

		private Color Back => dark ? Color.FromArgb(29, 35, 48) : Color.FromArgb(244, 246, 252);

		protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Rectangle bounds = new Rectangle(Point.Empty, item.Size);
			using (SolidBrush back = new SolidBrush(Back))
			{
				e.Graphics.FillRectangle(back, bounds);
			}
			if (item.Selected && item.Enabled)
			{
				Color sel = dark ? Color.FromArgb(56, 70, 92) : Color.FromArgb(214, 226, 248);
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using GraphicsPath path = RoundRect(new Rectangle(4, 1, bounds.Width - 8, bounds.Height - 2), 7);
				using SolidBrush brush = new SolidBrush(sel);
				e.Graphics.FillPath(brush, path);
			}
		}

		protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
		{
			using SolidBrush back = new SolidBrush(Back);
			e.Graphics.FillRectangle(back, e.AffectedBounds);
		}

		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			if (e.Item.Enabled)
			{
				e.TextColor = dark ? Color.FromArgb(237, 241, 248) : Color.FromArgb(32, 43, 67);
			}
			else
			{
				e.TextColor = dark ? Color.FromArgb(150, 160, 180) : Color.FromArgb(120, 130, 150);
			}
			base.OnRenderItemText(e);
		}

		protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
		{
			e.ArrowColor = dark ? Color.FromArgb(200, 208, 224) : Color.FromArgb(70, 80, 100);
			base.OnRenderArrow(e);
		}
	}

	private sealed class MenuColors : ProfessionalColorTable
	{
		private readonly bool dark;

		public MenuColors(bool dark)
		{
			this.dark = dark;
		}

		public override Color ToolStripDropDownBackground => dark ? Color.FromArgb(29, 35, 48) : Color.FromArgb(244, 246, 252);
		public override Color MenuBorder => dark ? Color.FromArgb(59, 69, 88) : Color.FromArgb(205, 212, 226);
		public override Color MenuItemSelected => dark ? Color.FromArgb(56, 65, 82) : Color.FromArgb(214, 226, 248);
		public override Color MenuItemBorder => MenuItemSelected;
		public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
		public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
		public override Color SeparatorDark => dark ? Color.FromArgb(60, 70, 89) : Color.FromArgb(214, 220, 232);
		public override Color SeparatorLight => SeparatorDark;
		public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
		public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
		public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
	}

	private static readonly (string Name, string Hex)[] MenuColorsList =
	{
		("Kırmızı", "#FF2D2D"), ("Mercan", "#FF654D"), ("Gül", "#FF3F7F"), ("Turuncu", "#FF8C1A"),
		("Kehribar", "#FFB000"), ("Altın", "#FFD166"), ("Limon", "#FFF44F"), ("Yeşil", "#2ECC40"),
		("Nane", "#65D28A"), ("Zümrüt", "#00A86B"), ("Turkuaz", "#40E0D0"), ("Camgöbeği", "#00E5FF"),
		("Okyanus", "#528DFF"), ("Mavi", "#1E5BFF"), ("Lacivert", "#1A2A8C"), ("Lavanta", "#BD77F4"),
		("Mor", "#8A2BE2"), ("Pembe", "#FF69B4"), ("Fuşya", "#FF00FF"), ("Mum ışığı", "#FFB46B"),
	};

	private static readonly (string Name, int Kelvin)[] TemperaturePresets =
	{
		("Mum ışığı", 2200), ("Sıcak", 2700), ("Yumuşak", 3500),
		("Doğal", 4000), ("Gün ışığı", 5000), ("Serin", 6500),
	};

	private readonly NotifyIcon icon;
	private readonly ContextMenuStrip menu;
	/// <summary>Sağ tık menüsü yalnızca "Çıkış"; tüm kontroller sol tık hızlı panelde.</summary>
	private readonly ContextMenuStrip rightMenu;
	private readonly Icon bulbIcon;
	private readonly Font menuFont = new Font("Segoe UI", 10f);
	private readonly Font headerFont = new Font("Segoe UI Semibold", 10f);
	private readonly List<Image> images = new List<Image>();
	private readonly Image bulbOn;
	private readonly Image bulbOff;

	private readonly ToolStripMenuItem status;
	private readonly ToolStripMenuItem detail;
	private readonly ToolStripMenuItem power;
	private readonly ToolStripMenuItem brightness;
	private readonly ToolStripMenuItem temperature;
	private readonly ToolStripMenuItem color;
	private readonly ToolStripMenuItem reconnect;
	private readonly ToolStripMenuItem modes;
	private readonly ToolStripMenuItem screenMode;
	private readonly ToolStripMenuItem audioMode;
	private readonly ToolStripMenuItem rainbowMode;
	private readonly ToolStripMenuItem fireMode;
	private readonly ToolStripMenuItem spotifyMode;
	private readonly ToolStripMenuItem daylightMode;
	private readonly ToolStripMenuItem stopMode;
	private readonly ToolStripMenuItem autostart;
	private ToolStripMenuItem? scenes;
	private ToolStripMenuItem? sleep;
	private Action? leftClick;
	private Action<string>? applyScene;
	private bool? lastPower;
	private string lastSwatchHex = "";
	private bool disposed;

	public TrayController(Action show, Action exit, Action connect, Action<int, byte[]> send, bool visible = true, Action<string>? changeMode = null, Action? togglePower = null, Action<WpfColor>? applyColor = null, Action? showColors = null, Func<bool>? getAutostart = null, Action<bool>? setAutostart = null)
	{
		TrayController self = this;
		menu = new ContextMenuStrip
		{
			Font = menuFont,
			ShowImageMargin = true,
			ShowCheckMargin = false,
			Padding = new Padding(6),
			BackColor = Color.FromArgb(29, 35, 48),
			ForeColor = Color.WhiteSmoke,
			Renderer = new GlassRenderer(),
			ImageScalingSize = new Size(16, 16),
		};
		bulbOn = Keep(BulbImage(true));
		bulbOff = Keep(BulbImage(false));

		status = Item(L.T("tray_status_offline"));
		status.Font = headerFont;
		status.Enabled = false;
		detail = Item(L.T("color_unknown"));
		detail.Enabled = false;

		// Tek aç/kapa düğmesi
		power = Item(L.T("light_turn_on"), () =>
		{
			if (togglePower != null)
			{
				togglePower();
			}
			else
			{
				send(2, Protocol.Power(on: self.lastPower != true));
			}
		});
		power.Image = bulbOff;
		power.Font = headerFont;

		brightness = Lz(Item(""), () => L.T("brightness"));
		foreach (int value in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
		{
			ToolStripMenuItem b = Lz(Item(""), () => L.Pct(value), () =>
			{
				send(3, Protocol.Brightness(value));
			});
			b.Image = Keep(BarImage(value));
			brightness.DropDownItems.Add(b);
		}

		color = Lz(Item(""), () => L.T("color"));
		foreach ((string name, string hex) in MenuColorsList)
		{
			Color c = ColorTranslator.FromHtml(hex);
			ToolStripMenuItem ci = Lz(Item(""), () => $"{L.Color(name)}   {hex}", () =>
			{
				if (applyColor != null)
				{
					applyColor(WpfColor.FromRgb(c.R, c.G, c.B));
				}
				else
				{
					send(5, Protocol.Rgb(c.R, c.G, c.B));
				}
			});
			ci.Image = Keep(SwatchImage(c));
			color.DropDownItems.Add(ci);
		}
		if (showColors != null)
		{
			color.DropDownItems.Add(new ToolStripSeparator());
			color.DropDownItems.Add(Lz(Item("", showColors), () => L.T("more_colors")));
		}

		temperature = Lz(Item(""), () => L.T("white_tone"));
		foreach ((string name, int kelvin) in TemperaturePresets)
		{
			WpfColor wc = ColorMath.FromKelvin(kelvin);
			ToolStripMenuItem ti = Lz(Item(""), () => $"{L.Color(name)} · {kelvin} K", () =>
			{
				send(4, Protocol.Temperature(kelvin));
			});
			ti.Image = Keep(SwatchImage(Color.FromArgb(wc.R, wc.G, wc.B)));
			temperature.DropDownItems.Add(ti);
		}

		reconnect = Lz(Item("", connect), () => L.T("reconnect"));
		modes = Item(L.T("modes"));
		screenMode = Item(L.Mode("Ekran"), () => changeMode?.Invoke("Ekran"));
		audioMode = Item(L.Mode("Ses"), () => changeMode?.Invoke("Ses"));
		rainbowMode = Item(L.Mode("Gökkuşağı"), () => changeMode?.Invoke("Gökkuşağı"));
		fireMode = Item(L.Mode("Şömine"), () => changeMode?.Invoke("Şömine"));
		stopMode = Lz(Item("", () => changeMode?.Invoke("Dur")), () => L.Mode("Manuel"));
		spotifyMode = Item("Spotify", () => changeMode?.Invoke("Spotify"));
		daylightMode = Item(L.Mode("Gün ışığı"), () => changeMode?.Invoke("Gün ışığı"));
		modes.DropDownItems.AddRange(new ToolStripItem[] { screenMode, audioMode, spotifyMode, rainbowMode, fireMode, daylightMode });
		spotifyMode.Image = Keep(SwatchImage(Color.FromArgb(30, 215, 96)));
		daylightMode.Image = Keep(SwatchImage(Color.FromArgb(255, 210, 122)));
		screenMode.Image = Keep(SwatchImage(Color.FromArgb(100, 210, 255)));
		audioMode.Image = Keep(SwatchImage(Color.FromArgb(48, 209, 88)));
		rainbowMode.Image = Keep(RainbowImage());
		fireMode.Image = Keep(FireImage());

		menu.Items.Add(status);
		menu.Items.Add(detail);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(brightness);
		menu.Items.Add(color);
		menu.Items.Add(temperature);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(modes);
		menu.Items.Add(stopMode);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(reconnect);
		menu.Items.Add(Lz(Item("", show), () => L.T("open_window")));
		autostart = Lz(Item("", () =>
		{
			bool next = !self.autostart!.Checked;
			setAutostart?.Invoke(next);
			self.autostart.Checked = getAutostart?.Invoke() ?? next;
		}), () => L.T("autostart"));
		autostart.Checked = getAutostart?.Invoke() ?? false;
		autostart.Visible = setAutostart != null;
		menu.Items.Add(autostart);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(power);
		menu.Items.Add(Lz(Item("", exit), () => L.T("exit")));
		menu.Opening += (object? _, System.ComponentModel.CancelEventArgs _) =>
		{
			if (getAutostart != null) self.autostart!.Checked = getAutostart();
		};
		StyleDropDowns(true);

		rightMenu = new ContextMenuStrip
		{
			Font = menuFont,
			ShowImageMargin = false,
			ShowCheckMargin = false,
			Padding = new Padding(6),
			BackColor = Color.FromArgb(29, 35, 48),
			ForeColor = Color.WhiteSmoke,
			Renderer = new GlassRenderer(),
		};
		ToolStripMenuItem quit = Lz(Item("", exit), () => L.T("exit"));
		quit.Padding = new Padding(18, 7, 30, 7);
		rightMenu.Items.Add(quit);

		bulbIcon = CreateIcon();
		icon = new NotifyIcon
		{
			Icon = bulbIcon,
			Text = L.T("tray_status_offline"),
			ContextMenuStrip = rightMenu,
			Visible = visible
		};
		icon.MouseClick += (object? _, MouseEventArgs e) =>
		{
			if (e.Button == MouseButtons.Left)
			{
				if (self.leftClick != null) self.leftClick();
				else self.menu?.Show(Cursor.Position);
			}
		};
		icon.BalloonTipClicked += (object? _, EventArgs _) =>
		{
			show();
		};
		Update(ready: false, busy: false, canConnect: false, hasBrightness: false, hasTemperature: false, hasColor: false, L.T("not_connected"));
	}

	/// <summary>Dil değişince metni yenilenecek öğeler.</summary>
	private readonly List<(ToolStripItem Item, Func<string> Text)> localized = new List<(ToolStripItem, Func<string>)>();

	private ToolStripMenuItem Lz(ToolStripMenuItem item, Func<string> text, Action? action = null)
	{
		item.Text = text();
		if (action != null) item.Click += (object? _, EventArgs _) => action();
		localized.Add((item, text));
		return item;
	}

	/// <summary>Arayüz dili değişti: sabit menü metinlerini yeniler (durum metinleri Update ile gelir).</summary>
	public void Relocalize()
	{
		if (disposed) return;
		foreach ((ToolStripItem item, Func<string> text) in localized) item.Text = text();
		if (sleep != null) sleep.Text = L.T("sleep_timer");
	}

	private static ToolStripMenuItem Item(string text, Action? action = null)
	{
		ToolStripMenuItem item = new ToolStripMenuItem(text)
		{
			Padding = new Padding(10, 7, 14, 7)
		};
		if (action != null)
		{
			item.Click += (object? _, EventArgs _) =>
			{
				action();
			};
		}
		return item;
	}

	private Image Keep(Image image)
	{
		images.Add(image);
		return image;
	}

	/// <summary>Sol tık hızlı panel, Sahneler / Uyku sayacı alt menüleri, Mini mod.</summary>
	public void Extend(Action leftClick, Action mini, Action<int> setSleep, Action<string> applyScene)
	{
		this.leftClick = leftClick;
		this.applyScene = applyScene;
		scenes = Lz(Item(""), () => L.T("scenes"));
		sleep = Item(L.T("sleep_timer"));
		foreach (int m in new[] { 15, 30, 45, 60, 90 })
		{
			int minutes = m;
			sleep.DropDownItems.Add(Lz(Item(""), () => L.F("min_short", minutes), () => setSleep(minutes)));
		}
		sleep.DropDownItems.Add(new ToolStripSeparator());
		sleep.DropDownItems.Add(Lz(Item(""), () => L.T("cancel"), () => setSleep(0)));
		int at = menu.Items.IndexOf(modes);
		menu.Items.Insert(at, scenes);
		menu.Items.Insert(menu.Items.IndexOf(stopMode) + 1, sleep);
		menu.Items.Insert(menu.Items.IndexOf(autostart), Lz(Item(""), () => L.T("mini_mode"), mini));
		StyleDropDowns(lastDark);
	}

	public void SetScenes(IEnumerable<Scene> list)
	{
		if (scenes == null || disposed) return;
		scenes.DropDownItems.Clear();
		foreach (Scene s in list)
		{
			string id = s.Id;
			System.Windows.Media.Color c = s.Accent;
			ToolStripMenuItem si = Item(s.DisplayName, () => applyScene?.Invoke(id));
			Bitmap? em = EmojiArt.Bitmap(s.Emoji, 32);
			si.Image = Keep(em ?? SwatchImage(Color.FromArgb(c.R, c.G, c.B)));
			scenes.DropDownItems.Add(si);
		}
		TrimImages();
	}

	/// <summary>Kalan süre (ör. "23:14") veya null.</summary>
	public void SetSleep(string? remaining)
	{
		if (sleep == null || disposed) return;
		string text = remaining == null ? L.T("sleep_timer") : L.F("sleep_timer_remaining", remaining);
		if (sleep.Text != text) sleep.Text = text;
		sleep.DropDownItems[sleep.DropDownItems.Count - 1].Enabled = remaining != null;
	}

	internal TrayIconRect.RECT? IconRect() => !disposed && TrayIconRect.TryGet(icon, out TrayIconRect.RECT r) ? r : null;

	private bool lastDark = true;

	private void StyleDropDowns(bool dark)
	{
		lastDark = dark;
		Color back = dark ? Color.FromArgb(29, 35, 48) : Color.FromArgb(244, 246, 252);
		Color fore = dark ? Color.FromArgb(237, 241, 248) : Color.FromArgb(32, 43, 67);
		foreach (ToolStripMenuItem? item in new[] { brightness, temperature, color, modes, scenes, sleep })
		{
			if (item == null) continue;
			item.DropDown.BackColor = back;
			item.DropDown.ForeColor = fore;
			item.DropDown.Font = menuFont;
			item.DropDown.Renderer = menu.Renderer;
			if (item.DropDown is ToolStripDropDownMenu dd)
			{
				dd.ShowImageMargin = true;
				dd.ShowCheckMargin = false;
				dd.ImageScalingSize = new Size(16, 16);
				dd.Padding = new Padding(4);
			}
		}
	}

	// ---- küçük simgeler ----

	private static GraphicsPath RoundRect(Rectangle r, int radius)
	{
		GraphicsPath p = new GraphicsPath();
		int d = radius * 2;
		p.AddArc(r.X, r.Y, d, d, 180, 90);
		p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
		p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
		p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
		p.CloseFigure();
		return p;
	}

	private static Bitmap Canvas32(out Graphics g)
	{
		Bitmap bmp = new Bitmap(32, 32);
		g = Graphics.FromImage(bmp);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Color.Transparent);
		return bmp;
	}

	internal static Bitmap SwatchImage(Color c)
	{
		Bitmap bmp = Canvas32(out Graphics g);
		using (g)
		{
			using SolidBrush fill = new SolidBrush(c);
			g.FillEllipse(fill, 3, 3, 26, 26);
			using LinearGradientBrush shine = new LinearGradientBrush(new Rectangle(6, 4, 20, 12), Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
			g.FillEllipse(shine, 8, 5, 16, 10);
			using Pen ring = new Pen(Color.FromArgb(110, 255, 255, 255), 1.5f);
			g.DrawEllipse(ring, 3, 3, 26, 26);
		}
		return bmp;
	}

	private static Bitmap BarImage(int percent)
	{
		Bitmap bmp = Canvas32(out Graphics g);
		using (g)
		{
			using SolidBrush track = new SolidBrush(Color.FromArgb(90, 150, 160, 180));
			g.FillRectangle(track, 4, 12, 24, 8);
			int w = Math.Max(2, (int)Math.Round(24 * percent / 100.0));
			using SolidBrush fill = new SolidBrush(Color.FromArgb(255, 210, 113));
			g.FillRectangle(fill, 4, 12, w, 8);
		}
		return bmp;
	}

	private static Bitmap RainbowImage()
	{
		Bitmap bmp = Canvas32(out Graphics g);
		using (g)
		{
			Color[] cs = { Color.FromArgb(255, 69, 58), Color.FromArgb(255, 214, 10), Color.FromArgb(48, 209, 88), Color.FromArgb(10, 132, 255), Color.FromArgb(191, 90, 242) };
			for (int i = 0; i < cs.Length; i++)
			{
				using Pen p = new Pen(cs[i], 2.6f);
				int inset = 3 + i * 3;
				g.DrawArc(p, inset, inset + 6, 32 - inset * 2, 32 - inset * 2, 180, 180);
			}
		}
		return bmp;
	}

	private static Bitmap FireImage()
	{
		Bitmap bmp = Canvas32(out Graphics g);
		using (g)
		{
			using GraphicsPath flame = new GraphicsPath();
			flame.AddBezier(16, 2, 20, 10, 27, 13, 26, 21);
			flame.AddBezier(26, 21, 25, 30, 7, 30, 6, 21);
			flame.AddBezier(6, 21, 6, 15, 11, 12, 12, 7);
			flame.AddBezier(12, 7, 14, 11, 15, 12, 16, 14);
			flame.AddBezier(16, 14, 18, 10, 17, 6, 16, 2);
			flame.CloseFigure();
			using LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 214, 10), Color.FromArgb(255, 59, 48), 90f);
			g.FillPath(b, flame);
		}
		return bmp;
	}

	private static Bitmap BulbImage(bool on)
	{
		Bitmap bmp = Canvas32(out Graphics g);
		using (g)
		{
			if (on)
			{
				using GraphicsPath glowPath = new GraphicsPath();
				glowPath.AddEllipse(0, -2, 32, 30);
				using PathGradientBrush glow = new PathGradientBrush(glowPath)
				{
					CenterColor = Color.FromArgb(170, 255, 214, 110),
					SurroundColors = new[] { Color.FromArgb(0, 255, 214, 110) },
				};
				g.FillPath(glow, glowPath);
			}
			Color glass = on ? Color.FromArgb(255, 220, 120) : Color.FromArgb(120, 128, 145);
			using SolidBrush gb = new SolidBrush(glass);
			g.FillEllipse(gb, 7, 3, 18, 18);
			g.FillRectangle(gb, 11, 17, 10, 5);
			using SolidBrush baseBrush = new SolidBrush(on ? Color.FromArgb(200, 205, 215) : Color.FromArgb(150, 155, 165));
			g.FillRectangle(baseBrush, 11, 23, 10, 3);
			g.FillRectangle(baseBrush, 12, 27, 8, 2);
		}
		return bmp;
	}

	// ---- durum ----

	public void Update(bool ready, bool busy, bool canConnect, bool hasBrightness, bool hasTemperature, bool hasColor, string description, bool modeActive = false, string modeName = "", bool? powerOn = null, WpfColor? current = null, double brightnessPercent = -1)
	{
		if (disposed)
		{
			return;
		}
		bool live = ready && !busy;
		rainbowMode.Enabled = live && hasColor;
		screenMode.Enabled = live && hasColor;
		fireMode.Enabled = live && hasColor;
		spotifyMode.Enabled = live && hasColor;
		daylightMode.Enabled = live && (hasTemperature || hasColor);
		spotifyMode.Text = (modeName == "Spotify" ? "● " : "") + L.Mode("Spotify");
		daylightMode.Text = (modeName == "Gün ışığı" ? "● " : "") + L.Mode("Gün ışığı");
		audioMode.Enabled = live && hasBrightness;
		screenMode.Text = (modeName == "Ekran" ? "● " : "") + L.Mode("Ekran");
		audioMode.Text = (modeName == "Ses" ? "● " : "") + L.Mode("Ses");
		rainbowMode.Text = (modeName == "Gökkuşağı" ? "● " : "") + L.Mode("Gökkuşağı");
		fireMode.Text = (modeName == "Şömine" ? "● " : "") + L.Mode("Şömine");
		stopMode.Enabled = modeActive;
		power.Enabled = live;
		lastPower = powerOn;
		power.Text = powerOn == true ? L.T("light_turn_off") : powerOn == false ? L.T("light_turn_on") : L.T("power_toggle");
		power.Image = powerOn == true ? bulbOn : bulbOff;
		brightness.Enabled = live && hasBrightness;
		temperature.Enabled = live && hasTemperature;
		color.Enabled = live && hasColor;
		reconnect.Enabled = !ready && !busy && canConnect;
		modes.Text = modeActive ? L.F("modes_active", L.Mode(modeName)) : L.T("modes");

		string text = busy ? L.T("busy") : (modeActive ? L.F("mode_live", L.Mode(modeName)) : description);
		status.Text = (text.Length > 42) ? (text.Substring(0, 39) + "…") : text;

		string state = powerOn == true ? L.T("on") : powerOn == false ? L.T("off") : L.T("unknown");
		string bri = brightnessPercent >= 0 && powerOn == true ? " • " + L.Pct(brightnessPercent) : "";
		if (current.HasValue)
		{
			WpfColor c = current.Value;
			string hex = ColorMath.Hex(c);
			string name = Palette.NameOf(c);
			detail.Text = $"{state}{bri} • {(name == hex ? hex : name + " " + hex)}";
			if (hex != lastSwatchHex)
			{
				lastSwatchHex = hex;
				detail.Image = Keep(SwatchImage(Color.FromArgb(c.R, c.G, c.B)));
				TrimImages();
			}
		}
		else
		{
			detail.Text = state + bri;
			detail.Image = null;
		}

		string tip = "Chiaro • " + text;
		icon.Text = (tip.Length > 63) ? (tip.Substring(0, 60) + "…") : tip;
	}

	private void TrimImages()
	{
		// Canlı modlarda renk sürekli değiştiği için durum simgeleri birikmesin.
		HashSet<Image> used = new HashSet<Image>();
		void Collect(ToolStripItemCollection items)
		{
			foreach (ToolStripItem i in items)
			{
				if (i.Image != null) used.Add(i.Image);
				if (i is ToolStripMenuItem mi) Collect(mi.DropDownItems);
			}
		}
		Collect(menu.Items);
		used.Add(bulbOn);
		used.Add(bulbOff);
		foreach (Image img in images.Where(i => !used.Contains(i)).ToList())
		{
			images.Remove(img);
			img.Dispose();
		}
	}

	public void Notify(string title, string detailText)
	{
		if (!disposed)
		{
			icon.ShowBalloonTip(5000, title, (detailText.Length > 220) ? (detailText.Substring(0, 217) + "…") : detailText, ToolTipIcon.Info);
		}
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			icon.Visible = false;
			icon.Dispose();
			menu.Dispose();
			rightMenu.Dispose();
			bulbIcon.Dispose();
			foreach (Image img in images) img.Dispose();
			images.Clear();
			menuFont.Dispose();
			headerFont.Dispose();
		}
	}

	public void SetTheme(bool dark)
	{
		if (disposed) return;
		menu.BackColor = dark ? Color.FromArgb(29, 35, 48) : Color.FromArgb(244, 246, 252);
		menu.ForeColor = dark ? Color.FromArgb(237, 241, 248) : Color.FromArgb(32, 43, 67);
		menu.Renderer = new GlassRenderer(dark);
		rightMenu.BackColor = menu.BackColor;
		rightMenu.ForeColor = menu.ForeColor;
		rightMenu.Renderer = menu.Renderer;
		StyleDropDowns(dark);
	}

	private static Icon CreateIcon()
	{
		using Stream stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Chiaro.ico")).Stream;
		// Bildirim alanı küçük simge boyutunu kullanır (100% = 16, 125% = 20, 150% = 24 px); .ico'da her boyut hazır.
		System.Drawing.Size size = SystemInformation.SmallIconSize;
		using Icon icon = new Icon(stream, size.Width > 0 ? size.Width : 16, size.Height > 0 ? size.Height : 16);
		return (Icon)icon.Clone();
	}

	/// <summary>Duman testi: örnek durumda menüyü açıp PNG olarak kaydeder.</summary>
	internal static void RenderMenuForTest(string path, bool dark)
	{
		using TrayController t = new TrayController(() => { }, () => { }, () => { }, (int _, byte[] _) => { }, visible: false, changeMode: _ => { }, togglePower: () => { }, applyColor: _ => { }, showColors: () => { }, getAutostart: () => true, setAutostart: _ => { });
		t.Extend(() => { }, () => { }, _ => { }, _ => { });
		t.SetScenes(Scene.Defaults());
		t.SetSleep("22:59");
		t.SetTheme(dark);
		t.Update(ready: true, busy: false, canConnect: true, hasBrightness: true, hasTemperature: true, hasColor: true, L.F("connected_to", "Hue Play"), modeActive: false, "", powerOn: true, current: WpfColor.FromRgb(255, 140, 26), brightnessPercent: 80);
		t.rightMenu.Show(new Point(40, 40));
		System.Windows.Forms.Application.DoEvents();
		Size size = t.rightMenu.Size;
		using Bitmap bmp = new Bitmap(size.Width, size.Height);
		t.rightMenu.DrawToBitmap(bmp, new Rectangle(0, 0, size.Width, size.Height));
		bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
		t.rightMenu.Close();
	}

	/// <summary>Duman testi: tüm menü öğelerinin metinleri (alt menüler dahil), dil değişiminden sonra.</summary>
	internal static List<string> MenuTextsForTest(Action<TrayController>? between = null)
	{
		using TrayController t = new TrayController(() => { }, () => { }, () => { }, (int _, byte[] _) => { }, visible: false, changeMode: _ => { }, togglePower: () => { }, applyColor: _ => { }, showColors: () => { }, getAutostart: () => true, setAutostart: _ => { });
		t.Extend(() => { }, () => { }, _ => { }, _ => { });
		between?.Invoke(t);
		t.Relocalize();
		t.SetScenes(Scene.Defaults());
		t.SetSleep("22:59");
		t.Update(ready: true, busy: false, canConnect: true, hasBrightness: true, hasTemperature: true, hasColor: true, L.F("connected_to", "Hue Play"), modeActive: true, "Şömine", powerOn: true, current: WpfColor.FromRgb(255, 140, 26), brightnessPercent: 80);
		List<string> texts = new List<string> { t.icon.Text };
		void Walk(ToolStripItemCollection items)
		{
			foreach (ToolStripItem i in items)
			{
				texts.Add(i.Text ?? "");
				if (i is ToolStripMenuItem m) Walk(m.DropDownItems);
			}
		}
		Walk(t.menu.Items);
		Walk(t.rightMenu.Items);
		return texts;
	}

	internal static string TestMenu()
	{
		List<(int Code, byte[] Data)> sent = new List<(int, byte[])>();
		using TrayController t = new TrayController(() => { }, () => { }, () => { }, (int code, byte[] data) =>
		{
			sent.Add((code, data));
		}, visible: false);
		string[] texts = (from ToolStripItem x in t.menu.Items select x.Text).ToArray();
		Require(t.icon.ContextMenuStrip == t.rightMenu && t.rightMenu.Items.Count == 1 && t.rightMenu.Items[0].Text == L.T("exit"), "right-click menu is only Exit");
		Require(texts[^2] == L.T("power_toggle") && texts.Last() == L.T("exit") && t.menu.Items[t.menu.Items.Count - 3] is ToolStripSeparator, "power toggle directly above Çıkış");
		Require(!t.screenMode.Enabled && !t.audioMode.Enabled && !t.fireMode.Enabled && !t.stopMode.Enabled, "offline modes disabled");
		t.Update(ready: true, busy: false, canConnect: false, hasBrightness: true, hasTemperature: true, hasColor: true, "Mode", modeActive: true, "Şömine", powerOn: true, current: WpfColor.FromRgb(255, 100, 20));
		Require(t.stopMode.Enabled && t.fireMode.Enabled && t.power.Enabled && t.power.Text == L.T("light_turn_off") && t.fireMode.Text?.StartsWith("●") == true, "active mode + power state");
		t.power.PerformClick();
		Require(sent.Count == 1 && sent[0].Code == 2 && sent[0].Data.SequenceEqual(new byte[] { 0 }), "toggle while on sends off");
		t.Update(ready: true, busy: false, canConnect: false, hasBrightness: true, hasTemperature: false, hasColor: false, "Test", powerOn: false);
		Require(t.power.Text == L.T("light_turn_on"), "power label when off");
		t.power.PerformClick();
		Require(sent.Count == 2 && sent[1].Data.SequenceEqual(new byte[] { 1 }), "toggle while off sends on");
		((ToolStripMenuItem)t.brightness.DropDownItems[4]).PerformClick();
		Require(sent.Count == 3 && sent[2].Code == 3 && sent[2].Data.SequenceEqual(new byte[] { 127 }), "50% brightness dispatch");
		Require(!t.temperature.Enabled && !t.color.Enabled, "unsupported controls disabled");
		t.Update(ready: false, busy: false, canConnect: true, hasBrightness: false, hasTemperature: false, hasColor: false, "Offline");
		Require(t.reconnect.Enabled && !t.power.Enabled, "reconnect availability");
		Require(t.color.DropDownItems.Count >= 20, "color submenu has swatches");
		t.Dispose();
		t.Dispose();
		return "Tray menu: single power toggle, swatch submenus, Şömine mode gating, brightness dispatch, reconnect, idempotent disposal PASS.";

		static void Require(bool value, string name)
		{
			if (!value)
			{
				throw new InvalidOperationException("Tray test: " + name);
			}
		}
	}
}
