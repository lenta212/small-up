using Content.Shared.Radio;
using Robust.Shared.Map;

namespace Content.Server.Radio;

public readonly record struct TelecomRadioLogEntry(
    DateTime Timestamp,
    MapId Map,
    string Channel,
    int Frequency,
    string Speaker,
    string Message);

public readonly record struct TelecomRadioLogFilter(
    MapId? Map = null,
    string? Channel = null,
    int? Frequency = null,
    string? Speaker = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Words = null);
