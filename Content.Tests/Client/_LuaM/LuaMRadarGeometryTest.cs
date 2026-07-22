using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Client.UserInterface.Controls;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMRadarGeometryTest
{
    private static readonly Box2 View = new(0f, 0f, 10f, 10f);

    [Test]
    public void SegmentViewportIntersectionHandlesCrossingTangentAndZeroLengthLines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ShuttleNavControl.SegmentIntersectsBox(View, new Vector2(-1f, 5f), new Vector2(11f, 5f)),
                Is.True,
                "A line crossing the viewport must be drawn even when both endpoints are outside.");
            Assert.That(
                ShuttleNavControl.SegmentIntersectsBox(View, new Vector2(-2f, 12f), new Vector2(-1f, 20f)),
                Is.False,
                "A line wholly outside on the same side must be culled.");
            Assert.That(
                ShuttleNavControl.SegmentIntersectsBox(View, new Vector2(-1f, 0f), new Vector2(11f, 0f)),
                Is.True,
                "A line tangent to the viewport edge must remain visible.");
            Assert.That(
                ShuttleNavControl.SegmentIntersectsBox(View, new Vector2(5f, 5f), new Vector2(5f, 5f)),
                Is.True,
                "A zero-length line inside the viewport must remain visible.");
            Assert.That(
                ShuttleNavControl.SegmentIntersectsBox(View, new Vector2(-1f, -1f), new Vector2(-1f, -1f)),
                Is.False,
                "A zero-length line outside the viewport must be culled.");
        });
    }

    [Test]
    public void SegmentClippingStopsLinesAtRadarEdges()
    {
        var horizontalVisible = ShuttleNavControl.TryClipSegmentToBox(
            View,
            new Vector2(-5f, 5f),
            new Vector2(15f, 5f),
            out var horizontalStart,
            out var horizontalEnd);
        var verticalVisible = ShuttleNavControl.TryClipSegmentToBox(
            View,
            new Vector2(5f, -10f),
            new Vector2(5f, 20f),
            out var verticalStart,
            out var verticalEnd);
        var outsideVisible = ShuttleNavControl.TryClipSegmentToBox(
            View,
            new Vector2(12f, -10f),
            new Vector2(12f, 20f),
            out _,
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(horizontalVisible, Is.True);
            Assert.That(horizontalStart, Is.EqualTo(new Vector2(0f, 5f)));
            Assert.That(horizontalEnd, Is.EqualTo(new Vector2(10f, 5f)));
            Assert.That(verticalVisible, Is.True);
            Assert.That(verticalStart, Is.EqualTo(new Vector2(5f, 0f)));
            Assert.That(verticalEnd, Is.EqualTo(new Vector2(5f, 10f)));
            Assert.That(outsideVisible, Is.False);
        });
    }

    [Test]
    public void RadarGeometryTracksRectangularViewportAndKeepsSquareFallback()
    {
        var resized = MapGridControl.GetViewportGeometry(new Vector2(300f, 500f), 1f);
        var fallback = MapGridControl.GetViewportGeometry(Vector2.Zero, 1.25f);

        Assert.Multiple(() =>
        {
            Assert.That(resized.Midpoint, Is.EqualTo(new Vector2(150f, 250f)));
            Assert.That(resized.Radius, Is.EqualTo(146f));
            Assert.That(fallback.Midpoint, Is.EqualTo(new Vector2(405f, 405f)));
            Assert.That(fallback.Radius, Is.EqualTo(400f));
        });
    }
}
