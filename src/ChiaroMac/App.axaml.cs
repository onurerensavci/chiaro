#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace HueStudio;

public partial class App : Application
{
	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
		// Fluent şablonu Thumb kenarlığını 0'a sabitliyor; açık temada beyaz düğme kaybolmasın diye yerel değerle ince kenarlık veriyoruz.
		Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<Slider>((s, e) =>
		{
			var t = e.NameScope.Find<Avalonia.Controls.Primitives.Thumb>("thumb");
			if (t != null)
			{
				t.BorderThickness = new Thickness(1);
				if (t.TryFindResource("ThumbBorder", t.ActualThemeVariant, out var b) && b is Avalonia.Media.IBrush br) t.BorderBrush = br;
			}
		});
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			MainWindow w = new MainWindow();
			desktop.MainWindow = w;
			desktop.ShutdownRequested += (_, _) => w.PrepareForExit();
			try
			{
				if (this.TryGetFeature<IActivatableLifetime>() is IActivatableLifetime act)
				{
					// Dock simgesine tıklanınca gizli pencereyi geri getir.
					act.Activated += (_, e) =>
					{
						if (e.Kind == ActivationKind.Reopen) w.RestoreWindow();
					};
				}
			}
			catch (Exception ex)
			{
				AppLog.Write("Etkinleştirme olayı: " + ex.Message);
			}
			w.Show();
		}
		base.OnFrameworkInitializationCompleted();
	}
}
