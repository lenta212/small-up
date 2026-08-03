using System.Collections.Generic;

// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Goobstation.Common.Temperature;

[ByRefEvent]
public record struct GetTemperatureThresholdsEvent(
    float HeatDamageThreshold = 0f,
    float ColdDamageThreshold = 0f,
    Dictionary<float, float>? SpeedThresholds = null);

[ByRefEvent]
public record struct GetCurrentTemperatureEvent(
    float? CurrentTemperature = null);
