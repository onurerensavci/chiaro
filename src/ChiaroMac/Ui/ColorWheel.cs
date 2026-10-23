#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HueStudio.Ui;

/// <summary>Ton (açı) + doygunluk (yarıçap) tekerleği. Sürüklerken Changing, bırakınca Committed.</summary>
public sealed class ColorWheel : Control
{
	private WriteableBitmap? bitmap;
	private int bitmapSize;
	private bool dragging;

	public double Hue { get; private set; } = 28;
	public double Saturation { get; private set; } = 0.9;

	public event Action<double, double>? Changing;
	public event Action<double, double>? Committed;

	public void SetHueSat(double hue, double sat)
	{
		Hue = ((hue % 360) + 360) % 360;
		Saturation = Math.Clamp(sat, 0, 1);
		InvalidateVisual();
	}

	private void EnsureBitmap(int size)
	{
		if (bitmap != null && bitmapSize == size) return;
		bitmap?.Dispose();
		bitmapSize = size;
		bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
		using ILockedFramebuffer fb = bitmap.Lock();
		unsafe
		{
			double r = size / 2.0;
			for (int y = 0; y < size; y++)
			{
				uint* row = (uint*)((byte*)fb.Address + y * fb.RowBytes);
				for (int x = 0; x < size; x++)
				{
					double dx = x + 0.5 - r, dy = y + 0.5 - r;
					double d = Math.Sqrt(dx * dx + dy * dy) / r;
					if (d > 1.0)
					{
						row[x] = 0;
						continue;
					}
					double h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
					System.Windows.Media.Color c = ColorMath.FromHsv(h, d, 1);
					double a = Math.Clamp((1.0 - d) * r, 0, 1); // kenarda yumuşatma
					byte A = (byte)(a * 255);
					row[x] = (uint)(A << 24 | (byte)(c.R * a) << 16 | (byte)(c.G * a) << 8 | (byte)(c.B * a));
				}
			}
		}
	}

	public override void Render(DrawingContext context)
	{
		double size = Math.Min(Bounds.Width, Bounds.Height);
		if (size <= 2) return;
		double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
		EnsureBitmap(Math.Max(16, (int)(size * scale)));
		Rect rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
		context.DrawImage(bitmap!, new Rect(0, 0, bitmapSize, bitmapSize), rect);
		double rr = size / 2;
		double ang = Hue * Math.PI / 180;
		Point p = new Point(rect.Center.X + Math.Cos(ang) * Saturation * rr, rect.Center.Y + Math.Sin(ang) * Saturation * rr);
		context.DrawEllipse(null, new Pen(Brushes.Black, 3.2) { }, p, 7, 7);
		context.DrawEllipse(null, new Pen(Brushes.White, 2), p, 7, 7);
	}

	private void Pick(Point pos, bool final)
	{
		double size = Math.Min(Bounds.Width, Bounds.Height);
		double rr = size / 2;
		double dx = pos.X - Bounds.Width / 2, dy = pos.Y - Bounds.Height / 2;
		double d = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / rr);
		double h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
		SetHueSat(h, d);
		if (final) Committed?.Invoke(Hue, Saturation);
		else Changing?.Invoke(Hue, Saturation);
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		dragging = true;
		e.Pointer.Capture(this);
		Pick(e.GetPosition(this), false);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (dragging) Pick(e.GetPosition(this), false);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!dragging) return;
		dragging = false;
		e.Pointer.Capture(null);
		Pick(e.GetPosition(this), true);
	}
}
