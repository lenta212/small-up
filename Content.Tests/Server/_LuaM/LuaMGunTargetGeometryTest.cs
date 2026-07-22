#nullable enable

using System.Numerics;
using Content.Server.Weapons.Ranged.Systems;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server._LuaM;

[TestFixture]
[TestOf(typeof(GunSystem))]
public sealed class LuaMGunTargetGeometryTest
{
    private static readonly Box2 TargetBounds = new(4f, -0.5f, 5f, 0.5f);

    [Test]
    public void OnAxisTargetWithinAimSegmentIntersects()
    {
        Assert.That(
            GunSystem.DoesAimSegmentIntersect(TargetBounds, Vector2.Zero, new Vector2(10f, 0f), 0f),
            Is.True);
    }

    [Test]
    public void OffAxisTargetDoesNotIntersect()
    {
        Assert.That(
            GunSystem.DoesAimSegmentIntersect(
                TargetBounds,
                new Vector2(0f, 2f),
                new Vector2(10f, 2f),
                0f),
            Is.False);
    }

    [Test]
    public void TargetBeyondAimEndpointDoesNotIntersect()
    {
        Assert.That(
            GunSystem.DoesAimSegmentIntersect(TargetBounds, Vector2.Zero, new Vector2(3f, 0f), 0f),
            Is.False);
    }

    [Test]
    public void ToleranceBoundaryCountsAsIntersection()
    {
        var offsetTarget = new Box2(4f, 1f, 5f, 2f);

        Assert.That(
            GunSystem.DoesAimSegmentIntersect(offsetTarget, Vector2.Zero, new Vector2(10f, 0f), 1f),
            Is.True);
    }

    [Test]
    public void ZeroLengthAimUsesPointContainment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                GunSystem.DoesAimSegmentIntersect(
                    TargetBounds,
                    new Vector2(4.5f, 0f),
                    new Vector2(4.5f, 0f),
                    0f),
                Is.True,
                "A zero-length aim inside the target must intersect.");

            Assert.That(
                GunSystem.DoesAimSegmentIntersect(TargetBounds, Vector2.Zero, Vector2.Zero, 0f),
                Is.False,
                "A zero-length aim outside the target must not intersect.");
        });
    }
}
