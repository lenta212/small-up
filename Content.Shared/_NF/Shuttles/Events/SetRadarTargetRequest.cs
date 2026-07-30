// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shuttles.Events;

[Serializable, NetSerializable]
public sealed class SetRadarTargetRequest : BoundUserInterfaceMessage
{
    public Vector2 Position;
    public NetEntity TargetEntity = NetEntity.Invalid;
}

[Serializable, NetSerializable]
public sealed class SetRadarTargetVisibilityRequest : BoundUserInterfaceMessage
{
    public bool Hidden;
}
