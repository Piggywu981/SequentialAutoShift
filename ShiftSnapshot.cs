using System;
using ETS2LA.Game.Telemetry;

namespace SequentialAutoShift;

/// <summary>
///  An immutable copy of the telemetry fields the plug-in reads.
///
///  The host hands out one mutable <see cref="GameTelemetryData"/> instance and rewrites it in place at
///  60 Hz, so reading several fields from it across a tick can mix two frames (an rpm from one and a speed
///  from the next, which makes the acceleration estimate jump). Copying on the telemetry event costs one
///  small allocation per frame and removes the entire class of problem.
/// </summary>
public readonly struct ShiftSnapshot
{
    public bool SdkActive { get; init; }
    public bool Paused { get; init; }
    public int Gear { get; init; }
    public int GearCount { get; init; }
    public float Rpm { get; init; }
    public float RpmMax { get; init; }
    public float Speed { get; init; }
    public float Throttle { get; init; }
    public float Brake { get; init; }
    public float Steer { get; init; }
    public float PedalThrottle { get; init; }
    public float UserClutch { get; init; }
    public float GameClutch { get; init; }
    public bool MotorBrake { get; init; }
    public int RetarderStep { get; init; }
    public bool EngineRunning { get; init; }
    public double MassKg { get; init; }
    public double PitchTurns { get; init; }
    public string ShifterType { get; init; }
    public float[]? Ratios { get; init; }

    public TransmissionKind Transmission { get; init; }

    public static ShiftSnapshot From(GameTelemetryData data)
    {
        if (data == null)
            return default;

        return new ShiftSnapshot
        {
            SdkActive = data.sdkActive,
            Paused = data.paused,
            Gear = data.truckInt.gear,
            GearCount = data.configUI.gears,
            Rpm = data.truckFloat.engineRpm,
            RpmMax = data.configFloat.engineRpmMax,
            Speed = data.truckFloat.speed,
            Throttle = Clamp01(Math.Max(data.truckFloat.userThrottle, data.truckFloat.gameThrottle)),
            Brake = Clamp01(Math.Max(data.truckFloat.userBrake, data.truckFloat.gameBrake)),
            Steer = Math.Clamp(Math.Abs(data.truckFloat.gameSteer) > Math.Abs(data.truckFloat.userSteer)
                ? data.truckFloat.gameSteer
                : data.truckFloat.userSteer, -1f, 1f),
            PedalThrottle = Clamp01(data.truckFloat.userThrottle),
            UserClutch = Clamp01(data.truckFloat.userClutch),
            GameClutch = Clamp01(data.truckFloat.gameClutch),
            MotorBrake = data.truckBool.motorBrake,
            RetarderStep = data.truckUI.retarderBrake,
            EngineRunning = data.truckBool.engineEnabled,
            MassKg = data.configFloat.cargoMass + data.configFloat.unitMass,
            PitchTurns = data.truckPlacement.rotation.Y,
            ShifterType = data.configString.shifterType ?? string.Empty,
            Ratios = data.configFloat.gearRatiosForward.Length > 0 ? data.configFloat.gearRatiosForward : null,
            Transmission = ParseTransmission(data.configString.shifterType)
        };
    }

    /// <summary>
    ///  The game reports shifter type as "automatic", "manual", "hshifter" or "sequential". Only sequential
    ///  and semi-automatic boxes respond to gear up/down pulses, so everything else is treated as manual and
    ///  the plug-in stays idle instead of firing pulses the game ignores.
    /// </summary>
    public static TransmissionKind ParseTransmission(string? shifterType)
    {
        if (string.IsNullOrWhiteSpace(shifterType))
            return TransmissionKind.Unknown;

        string mode = shifterType.Trim().ToLowerInvariant();
        return mode switch
        {
            "sequential" => TransmissionKind.Sequential,
            "semi-automatic" or "semiautomatic" or "semi_automatic" or "semi" => TransmissionKind.SemiAutomatic,
            "automatic" => TransmissionKind.Automatic,
            "manual" or "hshifter" or "h-shifter" or "h_shifter" => TransmissionKind.Manual,
            _ => TransmissionKind.Unknown
        };
    }

    public ShiftInputs ToInputs(SequentialAutoShiftSettings cfg)
    {
        return new ShiftInputs(
            Gear,
            GearCount,
            Rpm,
            RpmMax,
            Speed,
            Throttle,
            Brake,
            Steer,
            MotorBrake,
            RetarderStep,
            UserClutch,
            GameClutch,
            MassKg,
            PitchTurns,
            EngineRunning,
            Transmission)
        {
            Ratios = Ratios
        };
    }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
}
