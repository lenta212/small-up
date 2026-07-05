#nullable enable

using Content.Client._LuaM.Sector;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMCopyIdFormattingTest
{
    [TestCase("Alpha-1234", "AL-1234")]
    [TestCase("x9", "XX-90000")]
    [TestCase("Route-98765", "RO-98765")]
    [TestCase("node7", "NO-70000")]
    public void BuildShortIdUsesTwoLettersAndFourToFiveDigits(string input, string expected)
    {
        var shortId = LuaMShortIdFormatter.BuildShortId(input);

        Assert.That(shortId, Is.EqualTo(expected));
        Assert.That(shortId, Does.Match("^[A-Z]{2}-\\d{4,5}$"));
    }
}
