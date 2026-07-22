using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Shuttles.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShuttleDockBindingRuntimeTest
{
    [Test]
    public async Task ConsoleCanOnlyUndockItsExactReciprocalPortPair()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var docking = entities.System<DockingSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var testMap = await pair.CreateTestMap();

        try
        {
            await server.WaitAssertion(() =>
            {
                entities.DeleteEntity(testMap.Grid);

                var controlledGrid = CreateGrid(Vector2.Zero);
                var targetGrid = CreateGrid(new Vector2(10f, 0f));
                var unrelatedGridA = CreateGrid(new Vector2(20f, 0f));
                var unrelatedGridB = CreateGrid(new Vector2(30f, 0f));

                var controlledDock = CreateDock(controlledGrid);
                var targetDock = CreateDock(targetGrid);
                var unrelatedDockA = CreateDock(unrelatedGridA);
                var unrelatedDockB = CreateDock(unrelatedGridB);
                var controlledDockComp = entities.GetComponent<DockingComponent>(controlledDock);
                var targetDockComp = entities.GetComponent<DockingComponent>(targetDock);
                var unrelatedDockAComp = entities.GetComponent<DockingComponent>(unrelatedDockA);
                var unrelatedDockBComp = entities.GetComponent<DockingComponent>(unrelatedDockB);

                docking.Dock((controlledDock, controlledDockComp), (targetDock, targetDockComp), true);
                docking.Dock((unrelatedDockA, unrelatedDockAComp), (unrelatedDockB, unrelatedDockBComp), true);

                var console = entities.SpawnEntity(null, new EntityCoordinates(controlledGrid, new Vector2(0.5f, 0.5f)));
                entities.AddComponent<ShuttleConsoleComponent>(console);

                var states = entities.System<ShuttleConsoleSystem>().GetAllDocks();
                var controlledState = states[entities.GetNetEntity(controlledGrid)]
                    .Single(state => state.Entity == entities.GetNetEntity(controlledDock));
                Assert.That(controlledState.DockedWith, Is.EqualTo(entities.GetNetEntity(targetDock)));

                RaiseUndock(console, unrelatedDockA, unrelatedDockB);
                Assert.That(unrelatedDockAComp.DockedWith, Is.EqualTo(unrelatedDockB),
                    "A console must not undock a pair on unrelated grids.");

                RaiseUndock(console, controlledDock, unrelatedDockB);
                Assert.That(controlledDockComp.DockedWith, Is.EqualTo(targetDock),
                    "A stale or substituted target must not undock the selected shuttle port.");

                RaiseUndock(console, controlledDock, targetDock);
                Assert.Multiple(() =>
                {
                    Assert.That(controlledDockComp.Docked, Is.False);
                    Assert.That(targetDockComp.Docked, Is.False);
                    Assert.That(unrelatedDockAComp.DockedWith, Is.EqualTo(unrelatedDockB));
                });

                docking.Dock((controlledDock, controlledDockComp), (targetDock, targetDockComp), true);
                entities.EventBus.RaiseLocalEvent(console, new UndockAllRequestMessage());
                Assert.Multiple(() =>
                {
                    Assert.That(controlledDockComp.Docked, Is.False,
                        "Undock-all must release the controlled shuttle.");
                    Assert.That(unrelatedDockAComp.DockedWith, Is.EqualTo(unrelatedDockB),
                        "Undock-all must not trust client-supplied ports or touch unrelated pairs.");
                });

                EntityUid CreateGrid(Vector2 position)
                {
                    var grid = mapManager.CreateGridEntity(testMap.MapId);
                    maps.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                    transform.SetLocalPosition(grid.Owner, position);
                    return grid.Owner;
                }

                EntityUid CreateDock(EntityUid grid)
                {
                    return entities.SpawnEntity(
                        "AirlockShuttle",
                        new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                }

                void RaiseUndock(EntityUid consoleUid, EntityUid ownDock, EntityUid target)
                {
                    entities.EventBus.RaiseLocalEvent(consoleUid, new UndockRequestMessage
                    {
                        DockEntity = entities.GetNetEntity(ownDock),
                        TargetDockEntity = entities.GetNetEntity(target),
                    });
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }
}
