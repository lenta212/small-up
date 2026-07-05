using Content.Client.UserInterface.Fragments;
using Content.Shared._LuaM.Sector;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._LuaM.Sector;

public sealed partial class LuaMSectorStatusUi : UIFragment
{
    private LuaMSectorStatusUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new LuaMSectorStatusUiFragment();
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is LuaMSectorStatusUiState sectorState)
            _fragment?.UpdateState(sectorState);
    }
}
