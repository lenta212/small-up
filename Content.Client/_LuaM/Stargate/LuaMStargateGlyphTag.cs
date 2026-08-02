using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._LuaM.Stargate;

/// <summary>
/// Renders Stargate address markup on papers with the recovered glyph font.
/// </summary>
public sealed class LuaMStargateGlyphTag : IMarkupTagHandler
{
    public static readonly ProtoId<FontPrototype> GlyphFont = "StargateGlyphs";

    [Dependency] private readonly IResourceCache _resourceCache = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public string Name => "stargate";

    public void PushDrawContext(MarkupNode node, MarkupDrawingContext context)
    {
        var font = FontTag.CreateFont(context.Font, node, _resourceCache, _prototypeManager, GlyphFont);
        context.Font.Push(font);
    }

    public void PopDrawContext(MarkupNode node, MarkupDrawingContext context)
    {
        context.Font.Pop();
    }
}
