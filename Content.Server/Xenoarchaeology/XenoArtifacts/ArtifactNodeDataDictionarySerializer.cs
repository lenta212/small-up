using System.Collections.Generic;
using System.Globalization;
using Content.Shared.FixedPoint;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server.Xenoarchaeology.XenoArtifacts;

/// <summary>
/// Compatibility reader for <see cref="ArtifactComponent.NodeData"/>.
///
/// The field is a <c>Dictionary&lt;string, object&gt;</c>, so the generic YAML
/// serializer tags every value with its runtime type. Historical saves used
/// primitive tags such as <c>!type:Int32</c> and <c>!type:List`1</c> that the
/// current serializer no longer resolves; the list tag even resolves to an open
/// generic type and aborts the whole restore. The three runtime value shapes
/// used by the artifact effects are read explicitly here while everything else
/// still falls back to the serializer.
/// </summary>
public sealed class ArtifactNodeDataDictionarySerializer :
    ITypeReader<Dictionary<string, object>, MappingDataNode>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        // Node data stores runtime values rather than prototype references, so
        // it does not participate in prototype validation.
        return new ValidatedValueNode(node);
    }

    public Dictionary<string, object> Read(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Dictionary<string, object>>? instanceProvider = null)
    {
        var dictionary = instanceProvider != null ? instanceProvider() : new Dictionary<string, object>();

        foreach (var (key, value) in node.Children)
        {
            dictionary[key] = ReadValue(serializationManager, value, hookCtx, context);
        }

        return dictionary;
    }

    private static object ReadValue(
        ISerializationManager serializationManager,
        Robust.Shared.Serialization.Markdown.DataNode value,
        SerializationHookContext hookCtx,
        ISerializationContext? context)
    {
        switch (value)
        {
            case SequenceDataNode sequence:
            {
                // The only list-valued node data is the chemical list. The old
                // `!type:List`1` tag cannot be bound by the serializer, so read
                // the elements directly as strings.
                var list = new List<string>(sequence.Count);
                foreach (var item in sequence)
                {
                    if (item is ValueDataNode scalar)
                        list.Add(scalar.Value);
                    else
                        list.Add(item.ToString());
                }

                return list;
            }
            case ValueDataNode scalar:
                return ReadScalar(serializationManager, scalar, hookCtx, context);
            default:
                // Custom serialized types such as DnaData still go through the
                // ordinary serializer, which can resolve their type tags.
                return serializationManager.Read<object>(value, hookCtx, context, notNullableOverride: true);
        }
    }

    private static object ReadScalar(
        ISerializationManager serializationManager,
        ValueDataNode scalar,
        SerializationHookContext hookCtx,
        ISerializationContext? context)
    {
        var tag = scalar.Tag?.StartsWith("!type:", System.StringComparison.Ordinal) == true
            ? scalar.Tag[6..]
            : null;

        switch (tag)
        {
            // The volume counter is stored as a FixedPoint2. Reading it through
            // Read<object> would resolve the tag to whichever FixedPoint2 type
            // the reflection manager finds first and then fail to box the value
            // type into an object return, so construct it directly with the
            // original serialized semantics.
            case "FixedPoint2":
                return FixedPoint2.New(
                    double.Parse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture));

            // Historical tags that no longer resolve must still keep their
            // original runtime type so the typed node-data getters can cast.
            case "Int32":
                return int.Parse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "Int64":
                return long.Parse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "Boolean":
                return bool.Parse(scalar.Value);
            case "Single":
            case "Double":
                return double.Parse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            case "String":
            case null:
                return ParseUntaggedScalar(scalar.Value);
            default:
                return serializationManager.Read<object>(scalar, hookCtx, context, notNullableOverride: true);
        }
    }

    private static object ParseUntaggedScalar(string value)
    {
        if (bool.TryParse(value, out var boolean))
            return boolean;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        return value;
    }
}
