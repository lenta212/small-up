#nullable enable

using System;
using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using System.Threading;
using Content.Server._LuaM.Sector;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMCharacterTtsValidationTest
{
    [Test]
    public void TextNormalizationRemovesMarkupControlsAndBoundsLength()
    {
        var normalized = LuaMCharacterTtsSystem.NormalizeTtsText(
            "  [bold]Привет[/bold]\r\n\u0001   сектор  ",
            12);

        Assert.That(normalized, Is.EqualTo("Привет секто"));
    }

    [Test]
    public void TextNormalizationDoesNotSplitUtf16SurrogatePairs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LuaMCharacterTtsSystem.NormalizeTtsText("A😀B", 2), Is.EqualTo("A"));
            Assert.That(LuaMCharacterTtsSystem.NormalizeTtsText("A😀B", 3), Is.EqualTo("A😀"));
            Assert.That(LuaMCharacterTtsSystem.NormalizeTtsText("text", 0), Is.Empty);
        });
    }

    [Test]
    public void MalformedMarkupFailsClosed()
    {
        Assert.That(LuaMCharacterTtsSystem.NormalizeTtsText("[bold broken", 100), Is.Empty);
    }

    [TestCase("ru_RU-irina-medium", true)]
    [TestCase("voice_42", true)]
    [TestCase("../voice", false)]
    [TestCase("voice name", false)]
    [TestCase("", false)]
    public void PiperVoiceNamesAreAllowlisted(string voice, bool expected)
    {
        Assert.That(LuaMCharacterTtsSystem.IsSafePiperVoiceName(voice), Is.EqualTo(expected));
    }

    [Test]
    public void GatewayUriCannotKeepAnInjectedPathOrQuery()
    {
        var uri = LuaMCharacterTtsSystem.BuildGatewayTtsUri("http://127.0.0.1:8099/ignored?token=leak");

        Assert.Multiple(() =>
        {
            Assert.That(uri.AbsolutePath, Is.EqualTo("/tts"));
            Assert.That(uri.Query, Is.Empty);
            Assert.That(uri.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(uri.Port, Is.EqualTo(8099));
        });
    }

    [Test]
    public void GatewayAudioRequiresAValidContainerHeader()
    {
        var wav = BuildMinimalWav();
        var ogg = BuildVorbisIdentificationPage();
        var fakeWav = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEdata");
        var fakeOgg = Encoding.ASCII.GetBytes("OggS\0\0\0\0");

        Assert.Multiple(() =>
        {
            Assert.That(TryDecode("wav", wav, wav.Length), Is.True);
            Assert.That(TryDecode("ogg", ogg, ogg.Length), Is.True);
            Assert.That(TryDecode("wav", ogg, ogg.Length), Is.False);
            Assert.That(TryDecode("ogg", wav, wav.Length), Is.False);
            Assert.That(TryDecode("mp3", wav, wav.Length), Is.False);
            Assert.That(TryDecode("wav", wav, wav.Length - 1), Is.False);
            Assert.That(TryDecode("wav", fakeWav, 1024), Is.False);
            Assert.That(TryDecode("ogg", fakeOgg, 1024), Is.False);
        });
    }

    [Test]
    public void GatewayAudioRejectsMalformedBase64()
    {
        Assert.That(
            LuaMCharacterTtsSystem.TryDecodeGatewayAudio("wav", "not base64", 1024, out _, out var audio),
            Is.False);
        Assert.That(audio, Is.Empty);
    }

    [Test]
    public void GatewayAudioRejectsOversizedBase64BeforeDecode()
    {
        var oversized = Convert.ToBase64String(new byte[13]);

        Assert.That(
            LuaMCharacterTtsSystem.TryDecodeGatewayAudio("wav", oversized, 12, out _, out var audio),
            Is.False);
        Assert.That(audio, Is.Empty);
    }

    [Test]
    public void GatewayRequestIdIsBoundedAndSafeForClientResourceNames()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LuaMCharacterTtsSystem.NormalizeGatewayRequestId("abc_123-def"), Is.EqualTo("abc_123-def"));
            Assert.That(LuaMCharacterTtsSystem.NormalizeGatewayRequestId("../unsafe"), Does.Match("^[0-9A-Fa-f]{32}$"));
            Assert.That(LuaMCharacterTtsSystem.NormalizeGatewayRequestId(new string('x', 65)), Does.Match("^[0-9A-Fa-f]{32}$"));
        });
    }

    [Test]
    public void GatewayHttpBodyIsBoundedBeforeJsonDeserialization()
    {
        using var oversized = new ByteArrayContent(new byte[16_401]);

        Assert.That(
            async () => await LuaMCharacterTtsSystem.ReadGatewayTtsResponseAsync(
                oversized,
                12,
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
    }

    private static bool TryDecode(string format, byte[] bytes, int maxBytes)
    {
        return LuaMCharacterTtsSystem.TryDecodeGatewayAudio(
            format,
            Convert.ToBase64String(bytes),
            maxBytes,
            out _,
            out _);
    }

    private static byte[] BuildMinimalWav()
    {
        var wav = new byte[46];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), (uint) wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), 2);
        return wav;
    }

    private static byte[] BuildVorbisIdentificationPage()
    {
        var ogg = new byte[58];
        "OggS"u8.CopyTo(ogg);
        ogg[5] = 0x02;
        ogg[26] = 1;
        ogg[27] = 30;
        var packet = ogg.AsSpan(28);
        packet[0] = 1;
        "vorbis"u8.CopyTo(packet[1..]);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(7, 4), 0);
        packet[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 16_000);
        packet[28] = 0xB8;
        packet[29] = 1;
        return ogg;
    }
}
