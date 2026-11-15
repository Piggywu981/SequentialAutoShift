using System;

namespace SequentialAutoShift;

/// <summary>
///  Everything the shift law is allowed to read. Kept separate from the plugin so the
///  scheduler can be compiled and exercised without ETS2LA loaded (see scratch/shiftsim).
/// </summary>
[Serializable]
public sealed class SequentialAutoShiftSettings
{
    public int SettingsVersion { get; set; } = 10;

    // Behaviour switches

    public bool Enabled { get; set; } = true;
    public bool DownshiftEnabled { get; set; } = true;
    public bool CoastDownshiftEnabled { get; set; } = true;
    public bool StartEngineWhenAccActive { get; set; } = true;
    public bool ResetGearWhenStopped { get; set; } = true;
    public bool AdaptiveShiftPoints { get; set; } = true;

    /// <summary>Skip an intermediate gear when the truck is lightly loaded.</summary>
    public bool AllowSkipShift { get; set; } = true;

    /// <summary>Start in a higher gear when the truck is empty, and in first when it is loaded.</summary>
    public bool AdaptiveStartGear { get; set; } = true;

    /// <summary>Total mass below which the truck counts as empty for the start-gear choice.</summary>
    public double LoadedMassKg { get; set; } = 20000;

    /// <summary>Hold the upshift while the steering wheel is turned at low speed.</summary>
    public bool AvoidShiftingInTurns { get; set; } = true;

    /// <summary>Use the truck pitch as a road gradient signal in addition to the acceleration proxy.</summary>
    public bool UseGradeSignal { get; set; } = true;

    /// <summary>Pull the gearbox down into the engine-brake band when the retarder, the engine brake or a descent asks for it.</summary>
    public bool HoldGearForEngineBrake { get; set; } = true;

    // Shift schedule

    public int StartGear { get; set; } = 1;
    public int MaxSkipGears { get; set; } = 2;

    public double UpshiftRpm { get; set; } = 1500;
    public double UpshiftRpmFullThrottle { get; set; } = 2100;
    public double DownshiftRpm { get; set; } = 1000;
    public double KickdownRpm { get; set; } = 1500;

    /// <summary>Engine speed the gearbox is pulled down to while engine braking, so the brake has revs to work with.</summary>
    public double EngineBrakeBandRpm { get; set; } = 1700;

    /// <summary>Downshift line while the service brake is applied. Higher than the coast line so a gear is always ready to pull out of the corner.</summary>
    public double BrakeDownshiftRpm { get; set; } = 1300;

    /// <summary>Extra engine speed demanded on a transient throttle stab, on top of the kickdown corner.</summary>
    public double KickdownBoostRpm { get; set; } = 200;

    /// <summary>Throttle travel per second that counts as a kickdown stab rather than a slow squeeze.</summary>
    public double KickdownThrottleRate { get; set; } = 1.8;

    // Timing

    public double ShiftHysteresisRpm { get; set; } = 200;

    /// <summary>
    ///  Lowest engine speed the gearbox may be entered at. This, not the upshift line, decides the shift
    ///  point of every low gear, so it is exposed under its own name instead of hiding inside a margin.
    /// </summary>
    public double LugFloorRpm { get; set; } = 1200;

    public double ShiftCooldownMs { get; set; } = 650;

    /// <summary>Time an upshift is held after a downshift, the classic cure for 1-2-1 ping-pong.</summary>
    public double PostDownshiftDwellMs { get; set; } = 1000;

    public double EngineStartRetryMs { get; set; } = 2500;
    public double ManualPauseMs { get; set; } = 1500;
    public double StoppedSpeedKph { get; set; } = 5;
    public double StopDwellMs { get; set; } = 1200;
    public double MinimumSpeedKph { get; set; } = 3;

    // Input thresholds

    public double BrakeThreshold { get; set; } = 0.08;

    /// <summary>Clutch pedal travel that counts as the driver taking the gearbox over.</summary>
    public double ClutchThreshold { get; set; } = 0.08;

    /// <summary>Clutch travel the game applies itself; that is a shift in progress, not a takeover.</summary>
    public double ShiftInProgressClutch { get; set; } = 0.05;

    // Turn suppression

    public double TurnSteerThreshold { get; set; } = 0.35;
    public double TurnMaxSpeedKph { get; set; } = 45;

    /// <summary>Grade that counts as a full slope when the rpm lines are raised.</summary>
    public double GradeFullSlope { get; set; } = 0.06;

    public SequentialAutoShiftSettings Clone()
    {
        return (SequentialAutoShiftSettings)MemberwiseClone();
    }
}
