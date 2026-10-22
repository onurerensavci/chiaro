#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HueStudio;

/// <summary>Küçük animasyon yardımcıları (yumuşak aç/kapa, solma, anahtar düğmesi).</summary>
internal static class Anim
{
	public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
	public static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

	/// <summary>"Animasyonlar" ayarı. Kapalıyken tüm geçişler anında olur.</summary>
	public static bool Enabled { get; set; } = true;

	private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(Enabled ? ms : 0);

	public static DoubleAnimation To(double to, int ms, IEasingFunction? ease = null)
	{
		DoubleAnimation a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease ?? Ease };
		Timeline.SetDesiredFrameRate(a, 60);
		return a;
	}

	/// <summary>Düğmelerde hafif üzerine gelme / basma ölçeği (CPU dostu, yalnızca etkileşimde çalışır).</summary>
	public static void InstallMicroInteractions()
	{
		EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent, new System.Windows.Input.MouseEventHandler((s, _) => Press(s, 1.02)));
		EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent, new System.Windows.Input.MouseEventHandler((s, _) => Press(s, 1.0)));
		EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, _) => Press(s, 0.96)), true);
		EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler((s, _) => Press(s, ((UIElement)s).IsMouseOver ? 1.02 : 1.0)), true);
	}

	private static void Press(object sender, double scale)
	{
		// Düğme stilleri kendi basma animasyonuna sahip; burada mod kutucukları ve günler.
		if (!Enabled || sender is not ToggleButton b || !b.IsEnabled) return;
		if (b is not RadioButton && b.Tag is not "chip") return;
		if (b.TemplatedParent is ComboBox || b.Tag is "noscale") return;
		if (b.RenderTransform is not ScaleTransform st || st.IsFrozen)
		{
			if (b.RenderTransform != null && b.RenderTransform != Transform.Identity && b.RenderTransform is not ScaleTransform) return;
			st = new ScaleTransform(1, 1);
			b.RenderTransform = st;
			b.RenderTransformOrigin = new Point(0.5, 0.5);
		}
		int ms = scale < 1 ? 90 : 160;
		st.BeginAnimation(ScaleTransform.ScaleXProperty, To(scale, ms));
		st.BeginAnimation(ScaleTransform.ScaleYProperty, To(scale, ms));
	}

	public static void Double(DependencyObject target, DependencyProperty property, double to, int ms, IEasingFunction? ease = null)
	{
		if (target is IAnimatable a)
		{
			a.BeginAnimation(property, To(to, ms, ease));
		}
	}

	public static void Color(SolidColorBrush brush, System.Windows.Media.Color to, int ms)
	{
		brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Ms(ms)) { EasingFunction = Ease });
	}

	public static void Color(GradientStop stop, System.Windows.Media.Color to, int ms)
	{
		stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, Ms(ms)) { EasingFunction = Ease });
	}

	/// <summary>Bölümü dikey ölçek + saydamlık ile açar/kapatır.</summary>
	public static void Collapse(FrameworkElement content, bool expand, bool animate = true)
	{
		if (content.LayoutTransform is not ScaleTransform scale || scale.IsFrozen)
		{
			scale = new ScaleTransform(1, 1);
			content.LayoutTransform = scale;
		}
		content.Tag = expand;
		if (!animate)
		{
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			content.BeginAnimation(UIElement.OpacityProperty, null);
			scale.ScaleY = expand ? 1 : 0;
			content.Opacity = expand ? 1 : 0;
			content.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
			return;
		}
		if (expand)
		{
			content.Visibility = Visibility.Visible;
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, To(1, 320));
			content.BeginAnimation(UIElement.OpacityProperty, To(1, 380));
		}
		else
		{
			DoubleAnimation shrink = To(0, 260);
			shrink.Completed += (_, _) =>
			{
				if (content.Tag is false)
				{
					content.Visibility = Visibility.Collapsed;
				}
			};
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
			content.BeginAnimation(UIElement.OpacityProperty, To(0, 200));
		}
	}

	public static void FadeIn(UIElement element)
	{
		element.Visibility = Visibility.Visible;
		element.Opacity = 0;
		element.BeginAnimation(UIElement.OpacityProperty, To(1, 280));
		if (element is FrameworkElement fe)
		{
			TranslateTransform t = new TranslateTransform(0, 10);
			fe.RenderTransform = t;
			t.BeginAnimation(TranslateTransform.YProperty, To(0, 320));
		}
	}

	/// <summary>"Switch" stilindeki ToggleButton için kaydırmalı animasyon.</summary>
	public static void AttachSwitch(ToggleButton toggle)
	{
		void Sync(bool animate)
		{
			toggle.ApplyTemplate();
			if (toggle.Template?.FindName("Knob", toggle) is not FrameworkElement knob) return;
			if (toggle.Template.FindName("OnFill", toggle) is not FrameworkElement fill) return;
			if (knob.RenderTransform is not TranslateTransform t || t.IsFrozen)
			{
				t = new TranslateTransform();
				knob.RenderTransform = t;
			}
			bool on = toggle.IsChecked == true;
			double travel = Math.Max(0, toggle.ActualWidth > 0 ? toggle.ActualWidth - knob.Width - 6 : 22);
			double x = on ? travel : 0;
			if (animate)
			{
				t.BeginAnimation(TranslateTransform.XProperty, To(x, 260, Spring));
				fill.BeginAnimation(UIElement.OpacityProperty, To(on ? 1 : 0, 220));
			}
			else
			{
				t.BeginAnimation(TranslateTransform.XProperty, null);
				fill.BeginAnimation(UIElement.OpacityProperty, null);
				t.X = x;
				fill.Opacity = on ? 1 : 0;
			}
		}
		toggle.Loaded += (_, _) => Sync(false);
		toggle.SizeChanged += (_, _) => Sync(false);
		toggle.Checked += (_, _) => Sync(toggle.IsLoaded);
		toggle.Unchecked += (_, _) => Sync(toggle.IsLoaded);
		toggle.Indeterminate += (_, _) => Sync(toggle.IsLoaded);
	}
}
