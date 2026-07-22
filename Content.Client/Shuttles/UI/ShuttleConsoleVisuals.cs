using System.Numerics;
using Content.Shared.CCVar;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Client.Shuttles.UI;

public enum ShuttleConsoleIconKind : byte
{
    Navigation,
    World,
    Docking,
    Beacon,
    Ftl,
    Autopilot,
    Refresh,
    Target,
    Lock,
    Undock,
    Iff,
    Network,
}

public sealed class ShuttleConsoleIcon : Control
{
    private ShuttleConsoleIconKind _kind;

    public ShuttleConsoleIconKind Kind
    {
        get => _kind;
        set => _kind = value;
    }

    public ShuttleConsoleIcon()
    {
        MinSize = new Vector2(20f, 20f);
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var center = PixelSize / 2f;
        var radius = MathF.Max(4f, MathF.Min(PixelWidth, PixelHeight) * 0.36f);
        var color = GetColor(_kind);

        switch (_kind)
        {
            case ShuttleConsoleIconKind.Navigation:
                DrawNavigation(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.World:
                DrawWorld(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Docking:
                DrawDocking(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Beacon:
                DrawBeacon(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Ftl:
                DrawFtl(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Autopilot:
                DrawAutopilot(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Refresh:
                DrawRefresh(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Target:
                DrawTarget(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Lock:
                DrawLock(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Undock:
                DrawUndock(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Iff:
                DrawIff(handle, center, radius, color);
                break;
            case ShuttleConsoleIconKind.Network:
                DrawNetwork(handle, center, radius, color);
                break;
        }
    }

    private static Color GetColor(ShuttleConsoleIconKind kind)
    {
        return kind switch
        {
            ShuttleConsoleIconKind.Navigation => Color.FromHex("#7AD9C5"),
            ShuttleConsoleIconKind.World => Color.FromHex("#72BDE8"),
            ShuttleConsoleIconKind.Docking => Color.FromHex("#E4B866"),
            ShuttleConsoleIconKind.Ftl => Color.FromHex("#D58BB3"),
            ShuttleConsoleIconKind.Lock => Color.FromHex("#E17A73"),
            ShuttleConsoleIconKind.Undock => Color.FromHex("#E17A73"),
            _ => Color.FromHex("#8BCFC3"),
        };
    }

    private static void DrawNavigation(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var nose = center + new Vector2(0f, -radius);
        var left = center + new Vector2(-radius * 0.62f, radius * 0.82f);
        var right = center + new Vector2(radius * 0.62f, radius * 0.82f);
        handle.DrawLine(nose, left, color);
        handle.DrawLine(left, center, color.WithAlpha(0.72f));
        handle.DrawLine(center, right, color.WithAlpha(0.72f));
        handle.DrawLine(right, nose, color);
        handle.DrawLine(center, center + new Vector2(0f, radius * 0.92f), color.WithAlpha(0.5f));
    }

    private static void DrawWorld(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        handle.DrawCircle(center, radius, color, false);
        handle.DrawCircle(center, radius * 0.48f, color.WithAlpha(0.55f), false);
        handle.DrawLine(center - new Vector2(radius, 0f), center + new Vector2(radius, 0f), color.WithAlpha(0.72f));
        handle.DrawLine(center - new Vector2(0f, radius), center + new Vector2(0f, radius), color.WithAlpha(0.38f));
    }

    private static void DrawDocking(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var left = center.X - radius;
        var right = center.X + radius;
        var top = center.Y - radius * 0.72f;
        var bottom = center.Y + radius * 0.72f;
        handle.DrawLine(new Vector2(left, top), new Vector2(left, bottom), color);
        handle.DrawLine(new Vector2(left, top), new Vector2(left + radius * 0.42f, top), color);
        handle.DrawLine(new Vector2(left, bottom), new Vector2(left + radius * 0.42f, bottom), color);
        handle.DrawLine(new Vector2(right, top), new Vector2(right, bottom), color);
        handle.DrawLine(new Vector2(right - radius * 0.42f, top), new Vector2(right, top), color);
        handle.DrawLine(new Vector2(right - radius * 0.42f, bottom), new Vector2(right, bottom), color);
        handle.DrawCircle(center - new Vector2(radius * 0.24f, 0f), radius * 0.16f, color, false);
        handle.DrawCircle(center + new Vector2(radius * 0.24f, 0f), radius * 0.16f, color, false);
        handle.DrawLine(center - new Vector2(radius * 0.08f, 0f), center + new Vector2(radius * 0.08f, 0f), color);
    }

    private static void DrawBeacon(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        handle.DrawCircle(center, radius, color.WithAlpha(0.48f), false);
        handle.DrawCircle(center, radius * 0.58f, color.WithAlpha(0.72f), false);
        handle.DrawCircle(center, radius * 0.18f, color, true);
    }

    private static void DrawFtl(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        DrawChevron(handle, center - new Vector2(radius * 0.34f, 0f), radius, color.WithAlpha(0.55f));
        DrawChevron(handle, center + new Vector2(radius * 0.18f, 0f), radius, color);
    }

    private static void DrawChevron(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var left = center - new Vector2(radius * 0.34f, radius * 0.62f);
        var point = center + new Vector2(radius * 0.34f, 0f);
        handle.DrawLine(left, point, color);
        handle.DrawLine(point, left + new Vector2(0f, radius * 1.24f), color);
    }

    private static void DrawAutopilot(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var start = center + new Vector2(-radius * 0.72f, radius * 0.52f);
        var turn = center + new Vector2(-radius * 0.1f, -radius * 0.32f);
        var end = center + new Vector2(radius * 0.7f, -radius * 0.58f);
        handle.DrawCircle(start, radius * 0.18f, color, false);
        handle.DrawLine(start + new Vector2(radius * 0.14f, -radius * 0.14f), turn, color.WithAlpha(0.65f));
        handle.DrawLine(turn, end, color);
        handle.DrawLine(end, end + new Vector2(-radius * 0.35f, -radius * 0.04f), color);
        handle.DrawLine(end, end + new Vector2(-radius * 0.16f, radius * 0.3f), color);
    }

    private static void DrawRefresh(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        Vector2? previous = null;
        for (var i = 0; i <= 7; i++)
        {
            var angle = Angle.FromDegrees(-45f + i * 38f);
            var point = center + angle.ToVec() * radius;
            if (previous != null)
                handle.DrawLine(previous.Value, point, color);
            previous = point;
        }

        var tip = center + Angle.FromDegrees(-45f).ToVec() * radius;
        handle.DrawLine(tip, tip + new Vector2(-radius * 0.08f, radius * 0.42f), color);
        handle.DrawLine(tip, tip + new Vector2(-radius * 0.4f, radius * 0.02f), color);
    }

    private static void DrawTarget(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        handle.DrawCircle(center, radius * 0.62f, color, false);
        handle.DrawLine(center - new Vector2(radius, 0f), center - new Vector2(radius * 0.28f, 0f), color);
        handle.DrawLine(center + new Vector2(radius * 0.28f, 0f), center + new Vector2(radius, 0f), color);
        handle.DrawLine(center - new Vector2(0f, radius), center - new Vector2(0f, radius * 0.28f), color);
        handle.DrawLine(center + new Vector2(0f, radius * 0.28f), center + new Vector2(0f, radius), color);
        handle.DrawCircle(center, radius * 0.1f, color, true);
    }

    private static void DrawLock(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var box = new UIBox2(
            center.X - radius * 0.62f,
            center.Y - radius * 0.05f,
            center.X + radius * 0.62f,
            center.Y + radius * 0.82f);
        handle.DrawRect(box, color, false);
        handle.DrawCircle(center - new Vector2(0f, radius * 0.08f), radius * 0.42f, color, false);
        handle.DrawRect(new UIBox2(
            center.X - radius * 0.5f,
            center.Y - radius * 0.08f,
            center.X + radius * 0.5f,
            center.Y + radius * 0.12f), Color.FromHex("#11191C"));
    }

    private static void DrawUndock(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var left = center - new Vector2(radius * 0.34f, 0f);
        var right = center + new Vector2(radius * 0.34f, 0f);
        handle.DrawLine(left - new Vector2(radius * 0.42f, radius * 0.65f), left - new Vector2(radius * 0.42f, -radius * 0.65f), color);
        handle.DrawLine(right + new Vector2(radius * 0.42f, radius * 0.65f), right + new Vector2(radius * 0.42f, -radius * 0.65f), color);
        handle.DrawLine(left, left - new Vector2(radius * 0.48f, 0f), color);
        handle.DrawLine(right, right + new Vector2(radius * 0.48f, 0f), color);
        handle.DrawLine(left - new Vector2(radius * 0.48f, 0f), left - new Vector2(radius * 0.18f, radius * 0.25f), color);
        handle.DrawLine(left - new Vector2(radius * 0.48f, 0f), left - new Vector2(radius * 0.18f, -radius * 0.25f), color);
        handle.DrawLine(right + new Vector2(radius * 0.48f, 0f), right + new Vector2(radius * 0.18f, radius * 0.25f), color);
        handle.DrawLine(right + new Vector2(radius * 0.48f, 0f), right + new Vector2(radius * 0.18f, -radius * 0.25f), color);
    }

    private static void DrawIff(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        handle.DrawCircle(center, radius, color.WithAlpha(0.35f), false);
        handle.DrawCircle(center, radius * 0.58f, color.WithAlpha(0.55f), false);
        handle.DrawLine(center, center + Angle.FromDegrees(-35f).ToVec() * radius, color);
        handle.DrawCircle(center, radius * 0.12f, color, true);
    }

    private static void DrawNetwork(DrawingHandleScreen handle, Vector2 center, float radius, Color color)
    {
        var top = center - new Vector2(0f, radius * 0.72f);
        var left = center + new Vector2(-radius * 0.72f, radius * 0.58f);
        var right = center + new Vector2(radius * 0.72f, radius * 0.58f);
        handle.DrawLine(top, left, color.WithAlpha(0.7f));
        handle.DrawLine(top, right, color.WithAlpha(0.7f));
        handle.DrawLine(left, right, color.WithAlpha(0.7f));
        handle.DrawCircle(top, radius * 0.18f, color, true);
        handle.DrawCircle(left, radius * 0.18f, color, true);
        handle.DrawCircle(right, radius * 0.18f, color, true);
    }
}

public sealed class ShuttleConsoleButton : Button
{
    private static readonly StyleBoxFlat NormalStyle = CreateStyle("#111B1E", "#2A4246");
    private static readonly StyleBoxFlat HoverStyle = CreateStyle("#17272B", "#5EAAA2");
    private static readonly StyleBoxFlat PressedStyle = CreateStyle("#19312F", "#79CFB8");
    private static readonly StyleBoxFlat DisabledStyle = CreateStyle("#101517", "#283235");

    public ShuttleConsoleButton()
    {
        Label.FontColorOverride = Color.FromHex("#D7E5E3");
        StyleBoxOverride = NormalStyle;
    }

    protected override void DrawModeChanged()
    {
        base.DrawModeChanged();
        StyleBoxOverride = DrawMode switch
        {
            DrawModeEnum.Hover => HoverStyle,
            DrawModeEnum.Pressed => PressedStyle,
            DrawModeEnum.Disabled => DisabledStyle,
            _ => NormalStyle,
        };

        if (Label != null)
        {
            Label.FontColorOverride = DrawMode == DrawModeEnum.Disabled
                ? Color.FromHex("#687779")
                : Color.FromHex("#D7E5E3");
        }
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        // Toggle controls keep a persistent geometric marker so the selected
        // state remains visible without relying on the accent color alone.
        if (!ToggleMode || !Pressed || Disabled)
            return;

        handle.DrawRect(
            new UIBox2(2f, MathF.Max(0f, PixelHeight - 4f), MathF.Max(2f, PixelWidth - 2f), PixelHeight - 1f),
            Color.FromHex("#B8F2E7"));
    }

    private static StyleBoxFlat CreateStyle(string background, string border)
    {
        var style = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex(background),
            BorderColor = Color.FromHex(border),
            BorderThickness = new Thickness(1f),
        };
        style.SetContentMarginOverride(StyleBox.Margin.Horizontal, 8f);
        style.SetContentMarginOverride(StyleBox.Margin.Vertical, 5f);
        return style;
    }
}

public sealed class ShuttleConsoleSectionHeader : PanelContainer
{
    private readonly Label _label;

    public string? Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public ShuttleConsoleSectionHeader()
    {
        MinHeight = 32f;
        Margin = new Thickness(0f, 8f, 0f, 4f);

        var style = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#152327"),
            BorderColor = Color.FromHex("#315056"),
            BorderThickness = new Thickness(0f, 0f, 0f, 1f),
        };
        style.SetContentMarginOverride(StyleBox.Margin.Horizontal, 10f);
        style.SetContentMarginOverride(StyleBox.Margin.Vertical, 5f);
        PanelOverride = style;

        _label = new Label
        {
            FontColorOverride = Color.FromHex("#9ED6CE"),
            VerticalAlignment = VAlignment.Center,
        };
        AddChild(_label);
    }
}

public sealed class ShuttleConsoleCrtOverlay : Control
{
    public const float BootDurationSeconds = 0.9f;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _configurationManager = default!;

    private TimeSpan _bootStarted;

    public ShuttleConsoleCrtOverlay()
    {
        IoCManager.InjectDependencies(this);
        MouseFilter = MouseFilterMode.Ignore;
    }

    public void StartBoot()
    {
        _bootStarted = _timing.RealTime;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var reducedMotion = _configurationManager.GetCVar(CCVars.ReducedMotion);
        DrawPersistentCrt(handle, reducedMotion);

        // Keep the static CRT texture and vignette, but skip the boot reveal
        // and moving retrace when reduced motion is requested.
        if (reducedMotion)
            return;

        var elapsed = (float) (_timing.RealTime - _bootStarted).TotalSeconds;
        if (elapsed < 0f || elapsed >= BootDurationSeconds)
            return;

        var progress = elapsed / BootDurationSeconds;
        if (progress < 0.14f)
        {
            handle.DrawRect(PixelSizeBox, Color.Black);
            var lineProgress = EaseOut(progress / 0.14f);
            var halfWidth = PixelWidth * 0.5f * lineProgress;
            var centerY = PixelHeight * 0.5f;
            handle.DrawRect(
                new UIBox2(PixelWidth * 0.5f - halfWidth, centerY - 1f, PixelWidth * 0.5f + halfWidth, centerY + 1f),
                Color.FromHex("#C8FFF3"));
            return;
        }

        if (progress < 0.48f)
        {
            var reveal = EaseOut((progress - 0.14f) / 0.34f);
            var halfHeight = MathF.Max(1f, PixelHeight * 0.5f * reveal);
            var centerY = PixelHeight * 0.5f;
            handle.DrawRect(new UIBox2(0f, 0f, PixelWidth, centerY - halfHeight), Color.Black);
            handle.DrawRect(new UIBox2(0f, centerY + halfHeight, PixelWidth, PixelHeight), Color.Black);
            handle.DrawRect(
                new UIBox2(0f, centerY + halfHeight - 1f, PixelWidth, centerY + halfHeight + 1f),
                Color.FromHex("#7AD9C5").WithAlpha(0.72f));
            return;
        }

        var fade = 1f - (progress - 0.48f) / 0.52f;
        handle.DrawRect(PixelSizeBox, Color.Black.WithAlpha(0.2f * fade));
        var sweepY = PixelHeight * ((progress - 0.48f) / 0.52f);
        handle.DrawRect(
            new UIBox2(0f, sweepY - 1f, PixelWidth, sweepY + 1f),
            Color.FromHex("#A8E9DC").WithAlpha(0.22f * fade));
    }

    private void DrawPersistentCrt(DrawingHandleScreen handle, bool reducedMotion)
    {
        var phase = reducedMotion
            ? 0f
            : (float) (_timing.RealTime.TotalSeconds * 18d % 4d);
        for (var y = phase; y < PixelHeight; y += 4f)
        {
            handle.DrawRect(new UIBox2(0f, y, PixelWidth, y + 1f), Color.Black.WithAlpha(0.055f));
        }

        if (!reducedMotion)
        {
            var retrace = (float) (_timing.RealTime.TotalSeconds * 34d % Math.Max(1f, PixelHeight));
            handle.DrawRect(
                new UIBox2(0f, retrace, PixelWidth, MathF.Min(PixelHeight, retrace + 2f)),
                Color.FromHex("#A8E9DC").WithAlpha(0.025f));
        }

        const float edge = 10f;
        handle.DrawRect(new UIBox2(0f, 0f, edge, PixelHeight), Color.Black.WithAlpha(0.16f));
        handle.DrawRect(new UIBox2(PixelWidth - edge, 0f, PixelWidth, PixelHeight), Color.Black.WithAlpha(0.16f));
        handle.DrawRect(new UIBox2(0f, 0f, PixelWidth, edge), Color.Black.WithAlpha(0.1f));
        handle.DrawRect(new UIBox2(0f, PixelHeight - edge, PixelWidth, PixelHeight), Color.Black.WithAlpha(0.1f));
    }

    private static float EaseOut(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return 1f - (1f - value) * (1f - value);
    }
}
