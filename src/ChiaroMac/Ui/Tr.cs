#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace HueStudio.Ui;

/// <summary>
/// AXAML'de yerelleştirme: ui:Tr.Key="device" (Text/Content/Header), ui:Tr.Tip="…" (ipucu), ui:Tr.Upper="True".
/// Dil değişince (L.Changed) kayıtlı tüm denetimler yenilenir.
/// </summary>
public sealed class Tr : AvaloniaObject
{
	public static readonly AttachedProperty<string?> KeyProperty = AvaloniaProperty.RegisterAttached<Tr, Control, string?>("Key");
	public static readonly AttachedProperty<string?> TipProperty = AvaloniaProperty.RegisterAttached<Tr, Control, string?>("Tip");
	public static readonly AttachedProperty<bool> UpperProperty = AvaloniaProperty.RegisterAttached<Tr, Control, bool>("Upper");

	private static readonly List<WeakReference<Control>> Registered = new List<WeakReference<Control>>();

	static Tr()
	{
		KeyProperty.Changed.AddClassHandler<Control>((c, _) => Register(c));
		TipProperty.Changed.AddClassHandler<Control>((c, _) => Register(c));
		UpperProperty.Changed.AddClassHandler<Control>((c, _) => Apply(c));
		L.Changed += RefreshAll;
	}

	public static string? GetKey(Control c) => c.GetValue(KeyProperty);
	public static void SetKey(Control c, string? v) => c.SetValue(KeyProperty, v);
	public static string? GetTip(Control c) => c.GetValue(TipProperty);
	public static void SetTip(Control c, string? v) => c.SetValue(TipProperty, v);
	public static bool GetUpper(Control c) => c.GetValue(UpperProperty);
	public static void SetUpper(Control c, bool v) => c.SetValue(UpperProperty, v);

	private static void Register(Control c)
	{
		lock (Registered) Registered.Add(new WeakReference<Control>(c));
		Apply(c);
	}

	public static void Apply(Control c)
	{
		string? key = GetKey(c);
		if (!string.IsNullOrEmpty(key))
		{
			string text = L.T(key);
			if (GetUpper(c)) text = L.Upper(text);
			switch (c)
			{
				case TextBlock t: t.Text = text; break;
				case TextBox tb: tb.Watermark = text; break;
				case HeaderedContentControl h: h.Header = text; break;
				case MenuItem m: m.Header = text; break;
				case ContentControl cc: cc.Content = text; break;
			}
		}
		string? tip = GetTip(c);
		if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(c, L.T(tip));
	}

	public static void RefreshAll()
	{
		lock (Registered)
		{
			Registered.RemoveAll(w => !w.TryGetTarget(out _));
			foreach (WeakReference<Control> w in Registered.ToArray())
			{
				if (w.TryGetTarget(out Control? c)) Apply(c);
			}
		}
	}
}
