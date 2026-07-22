using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Stores ship UUIDs in their canonical compact form. Robust's map serializer
/// has no built-in <see cref="Guid"/> data definition, so ship identity needs an
/// explicit scalar serializer to survive a grid round trip.
/// </summary>
public sealed class LuaMShipGuidSerializer : ITypeSerializer<Guid, ValueDataNode>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return Guid.TryParseExact(node.Value, "N", out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Failed parsing a canonical ship UUID.");
    }

    public Guid Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Guid>? instanceProvider = null)
    {
        return Guid.ParseExact(node.Value, "N");
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        Guid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value.ToString("N"));
    }
}
