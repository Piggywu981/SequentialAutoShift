using System;

namespace SequentialAutoShift;

public enum TransmissionKind
{
    Unknown,
    Sequential,
    SemiAutomatic,
    Manual,
    Automatic
}

public enum ShiftCommand
{
    None,
    Up,
    Down
}

/// <summary>Why the scheduler did nothing this tick. The plug-in turns it into localised prose.</summary>
public enum ShiftHold
{
    None,
    EngineOff,
    NoForwardGears,
    RpmUnavailable,
    ManualTransmission,
    InhibitedAfterFailures,
    CoolingDown,
    ManualTakeover,
    ShiftInProgress,
    BrakeHeld,
    BelowMinimumSpeed,
    EngineBraking,
    TurnHeld,
    TopGear,
    BottomGear,
    LandingTooLow,
    LandingTooHigh,
    PostDownshiftDwell,
    Neutral,
    Reverse,
    Stopped
}

/// <summary>Everything the shift law reads. Plain values so a simulator can synthesise them.</summary>
public readonly record struct ShiftInputs(
    int Gear,
    int GearCount,
    float Rpm,
    float RpmMax,
    float SpeedMps,
    float Throttle,
    float Brake,
    float Steer,
    bool MotorBrake,
    int RetarderStep,
    float UserClutch,
    float GameClutch,
    double MassKg,
    double PitchTurns,
    bool EngineRunning,
    TransmissionKind Transmission)
{
    /// <summary>Forward gear ratios, index 0 is first gear. Null when telemetry has not filled them in yet.</summary>
    public float[]? Ratios { get; init; }
}

public readonly record struct ShiftDecision(
    ShiftCommand Command,
    int Steps,
    ShiftHold Hold)
{
    /// <summary>The shift line that was in effect, so the status page and the simulator read the same value.</summary>
    public double Line { get; init; }

    /// <summary>The engine speed that justified the decision: measured, or the projected landing.</summary>
    public double Rpm { get; init; }

    /// <summary>The gear the box settled on, when a request was confirmed this tick.</summary>
    public int ConfirmedGear { get; init; }

    /// <summary>This command re-sends a request the game never acted on.</summary>
    public bool IsRetry { get; init; }

    /// <summary>Set when all retries were exhausted and shifting should be inhibited.</summary>
    public bool RequestFailed { get; init; }

    public static ShiftDecision Idle(ShiftHold hold = ShiftHold.None, double line = 0, double rpm = 0) =>
        new(ShiftCommand.None, 0, hold) { Line = line, Rpm = rpm };

    public static ShiftDecision Failed() => new(ShiftCommand.None, 0, ShiftHold.None) { RequestFailed = true };

    public static ShiftDecision Shift(ShiftCommand command, int steps, double line, double rpm) =>
        new(command, steps, ShiftHold.None) { Line = line, Rpm = rpm };
}

/// <summary>Mutable controller state, kept out of the plug-in so a settings reload cannot corrupt it.</summary>
public sealed class ShiftRuntime
{
    public int LastObservedGear;
    public DateTime LastSampleUtc = DateTime.MinValue;
    public DateTime NextShiftAllowedUtc = DateTime.MinValue;
    public DateTime ShiftInhibitedUntilUtc = DateTime.MinValue;
    public DateTime ManualPauseUntilUtc = DateTime.MinValue;
    public DateTime UpshiftPauseUntilUtc = DateTime.MinValue;
    public DateTime PostDownshiftDwellUntilUtc = DateTime.MinValue;
    public DateTime StoppedSinceUtc = DateTime.MinValue;

    public float LastSpeed;
    public float LastThrottle;
    public double ThrottleRate;
    public double Resistance;
    public double Grade;
    public int GradeSign;
    public bool GradeSignKnown;
    public double SmoothedAccel;

    public ScalarKalman AccelFilter = new(0.02f, 0.35f, 1f);

    // Closed-loop shift bookkeeping

    public ShiftCommand Pending;
    public int PendingFromGear;
    public int PendingTargetGear;
    public int PendingSteps;

    /// <summary>Gear the request started from, preserved across the per-step rebases a skip causes.</summary>
    public int PendingOriginGear;
    public int PendingAttempts;
    public DateTime PendingSentUtc = DateTime.MinValue;

    /// <summary>
    ///  Mirror of <see cref="PostDownshiftDwellUntilUtc"/>. An upshift drops the engine speed far below the
    ///  upshift line, so any "am I below the line yet" test re-arms a downshift on the very next tick and the
    ///  box ping-pongs. Giving the new gear a settling window is both what a driver does and what the
    ///  transient actually needs.
    /// </summary>
    public DateTime PostUpshiftDwellUntilUtc = DateTime.MinValue;

    public int UpshiftCount;
    public int DownshiftCount;
    public int VetoCount;
    public int FailedShiftCount;
    public int ManualShiftCount;
    public int SkippedShiftCount;

    private readonly double[] latencies = new double[8];
    private int latencyIndex;
    private int latencyCount;

    public void Reset(int gear)
    {
        LastObservedGear = gear;
        LastSampleUtc = DateTime.MinValue;
        NextShiftAllowedUtc = DateTime.MinValue;
        ShiftInhibitedUntilUtc = DateTime.MinValue;
        ManualPauseUntilUtc = DateTime.MinValue;
        UpshiftPauseUntilUtc = DateTime.MinValue;
        PostDownshiftDwellUntilUtc = DateTime.MinValue;
        StoppedSinceUtc = DateTime.MinValue;
        LastSpeed = 0;
        LastThrottle = 0;
        ThrottleRate = 0;
        Resistance = 0;
        Grade = 0;
        GradeSign = 0;
        GradeSignKnown = false;
        SmoothedAccel = 0;
        Pending = ShiftCommand.None;
        PendingFromGear = 0;
        PendingTargetGear = 0;
        PendingSteps = 0;
        PendingOriginGear = 0;
        PendingAttempts = 0;
        PendingSentUtc = DateTime.MinValue;
        PostUpshiftDwellUntilUtc = DateTime.MinValue;
        AccelFilter = new ScalarKalman(0.02f, 0.35f, 1f);
        latencyIndex = 0;
        latencyCount = 0;
        Array.Clear(latencies);
    }

    public void RecordLatency(double milliseconds)
    {
        if (milliseconds <= 0 || milliseconds > 5000)
            return;

        latencies[latencyIndex] = milliseconds;
        latencyIndex = (latencyIndex + 1) % latencies.Length;
        if (latencyCount < latencies.Length)
            latencyCount++;
    }

    /// <summary>
    ///  Timeout for the "did the gear change" check. A fixed value either gives up on a slow clutch
    ///  setting or stalls on a fast one, so it follows what this truck has actually been doing.
    /// </summary>
    public double VerifyTimeoutMs(double fallbackMs)
    {
        if (latencyCount < 3)
            return fallbackMs;

        double worst = 0;
        for (int i = 0; i < latencyCount; i++)
            worst = Math.Max(worst, latencies[i]);

        return Math.Clamp(worst * 1.8, 700, 3000);
    }

    public void ClearPending()
    {
        Pending = ShiftCommand.None;
        PendingTargetGear = 0;
        PendingSteps = 0;
        PendingOriginGear = 0;
        PendingAttempts = 0;
    }
}

public struct ScalarKalman
{
    private readonly float _q;
    private readonly float _r;
    private float _p;
    private float _x;
    private bool _initialised;

    public ScalarKalman(float q, float r, float p)
    {
        _q = q;
        _r = r;
        _p = p;
        _x = 0;
        _initialised = false;
    }

    public float Update(float measurement)
    {
        if (!_initialised)
        {
            _x = measurement;
            _initialised = true;
            return _x;
        }

        _p += _q;
        float k = _p / (_p + _r);
        _x += k * (measurement - _x);
        _p *= 1f - k;
        return _x;
    }
}

/// <summary>
///  The shift law. No ETS2LA types, no IO, no clock: everything arrives as arguments so the
///  same code runs in game and under the simulator in scratch/shiftsim.
/// </summary>
public static class ShiftScheduler
{
    /// <summary>Nothing is asked to upshift above this fraction of the redline; the hard limit is the redline.</summary>
    public const double RevLimitUpshiftFraction = 0.97;

    /// <summary>A downshift must not land above this fraction of the redline.</summary>
    public const double OverrevLandingFraction = 0.94;

    /// <summary>The lug floor may not climb past this fraction of the redline, or first gear could never upshift.</summary>
    private const double LugFloorCeilingFraction = 0.90;

    private const double FreeRollAccelMps2 = 0.5;
    private const double ResistanceDemandFloor = 0.3;
    private const double ResistanceUpshiftBonusRpm = 400;
    private const double ResistanceDownshiftBonusRpm = 250;
    private const double FullLoadTonnes = 40;
    private const double MassResistanceWeight = 0.35;

    /// <summary>A stopped gear reset walks this many gears per burst; a sequential box queues the presses.</summary>
    private const int ResetBurstMaxSteps = 3;

    /// <summary>First gear is a launch gear; above a crawl it would only ping-pong with second.</summary>
    private const double FirstGearExtraKph = 5;

    private const double VerifyTimeoutFloorMs = 900;
    private const double ShiftInProgressTimeoutMs = 2500;
    private const int MaxShiftAttempts = 3;

    /// <summary>
    ///  Fraction of the redline the engine climbs per second while a shift is in progress. Diesels pull
    ///  roughly 1000 rpm/s from the top of the band, so 0.5 covers even a fast truck with margin.
    /// </summary>
    private const double RevClimbFraction = 0.5;    /// <summary>
    ///  A skip is about mass, not about the instantaneous resistance estimate: the resistance proxy rises
    ///  on any transient (a bump, a throttle stab) and would switch skipping off exactly when the driver
    ///  is accelerating hard and wants it. Truck mass does not lie.
    /// </summary>
    private static bool IsLightlyLoaded(ShiftInputs inputs, SequentialAutoShiftSettings cfg) =>
        inputs.MassKg < cfg.LoadedMassKg;

    public static double MaxEngineRpm(float reported) => reported > 500 ? reported : 2000;

    public static double UpshiftLine(SequentialAutoShiftSettings cfg, float throttle, double maxRpm, double resistance)
    {
        double line = Lerp(cfg.UpshiftRpm, Math.Max(cfg.UpshiftRpmFullThrottle, cfg.UpshiftRpm), throttle);
        if (cfg.AdaptiveShiftPoints)
            line += resistance * ResistanceUpshiftBonusRpm;

        return Math.Min(line, maxRpm * RevLimitUpshiftFraction);
    }

    public static double DownshiftLine(
        SequentialAutoShiftSettings cfg,
        float throttle,
        double resistance,
        bool braking,
        bool engineBraking,
        bool descending)
    {
        double line = Lerp(cfg.DownshiftRpm, Math.Max(cfg.KickdownRpm, cfg.DownshiftRpm), throttle);
        if (cfg.AdaptiveShiftPoints)
            line += resistance * ResistanceDownshiftBonusRpm;

        // Braking wants a gear in hand for the corner exit, not the idle line.
        if (braking && cfg.DownshiftEnabled)
            line = Math.Max(line, cfg.BrakeDownshiftRpm);

        // Exhaust brake and retarder only make torque near their rated speed, so pull down into the band.
        if (cfg.HoldGearForEngineBrake && (engineBraking || descending))
            line = Math.Max(line, cfg.EngineBrakeBandRpm);

        return line;
    }

    /// <summary>Kickdown reaches for the boost only on a stab, not on a slow squeeze.</summary>
    public static double KickdownLine(SequentialAutoShiftSettings cfg, float throttle, double throttleRate, double resistance)
    {
        double line = Lerp(cfg.DownshiftRpm, Math.Max(cfg.KickdownRpm, cfg.DownshiftRpm), throttle);
        if (cfg.AdaptiveShiftPoints)
            line += resistance * ResistanceDownshiftBonusRpm;

        if (throttleRate >= cfg.KickdownThrottleRate)
            line += cfg.KickdownBoostRpm;

        return line;
    }

    /// <summary>Lowest engine speed the next gear may be entered at. Throttle independent on purpose.</summary>
    public static double LugFloor(SequentialAutoShiftSettings cfg, double maxRpm, double resistance)
    {
        double floor = cfg.DownshiftRpm + cfg.ShiftHysteresisRpm;
        if (cfg.AdaptiveShiftPoints)
            floor += resistance * ResistanceDownshiftBonusRpm;

        // The configured value is the one the settings page shows; the derived one only wins when it is
        // higher, so raising the downshift line never silently lowers the floor.
        floor = Math.Max(floor, cfg.LugFloorRpm);

        return Math.Min(floor, maxRpm * LugFloorCeilingFraction);
    }

    public static bool TryProjectRpm(ShiftInputs inputs, int currentGear, int targetGear, float currentRpm, out float projected)
    {
        projected = currentRpm;
        float[]? ratios = inputs.Ratios;
        if (ratios == null || currentGear <= 0 || targetGear <= 0)
            return false;

        int currentIndex = currentGear - 1;
        int targetIndex = targetGear - 1;
        if (currentIndex >= ratios.Length || targetIndex >= ratios.Length)
            return false;

        float currentRatio = Math.Abs(ratios[currentIndex]);
        float targetRatio = Math.Abs(ratios[targetIndex]);
        if (currentRatio <= 0.001f || targetRatio <= 0.001f)
            return false;

        projected = currentRpm * targetRatio / currentRatio;
        return projected > 0;
    }

    /// <summary>Lowest gear the plug-in may downshift into, or reset to when stopped.</summary>
    public static int EffectiveStartGear(SequentialAutoShiftSettings cfg, ShiftInputs inputs)
    {
        int gears = Math.Max(1, inputs.GearCount);
        int gear = Math.Clamp(cfg.StartGear, 1, gears);

        // An empty truck pulls away in second; the same gear under a load is a clutch killer.
        bool empty = inputs.MassKg < cfg.LoadedMassKg;
        if (cfg.AdaptiveStartGear && gear == 1 && gears >= 2 && empty)
            gear = 2;

        return Math.Clamp(gear, 1, gears);
    }

    public static ShiftDecision Step(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg, DateTime now)
    {
        Sample(rt, inputs, cfg, now);

        if (!inputs.EngineRunning)
            return ShiftDecision.Idle(ShiftHold.EngineOff);

        if (inputs.Transmission == TransmissionKind.Manual)
            return ShiftDecision.Idle(ShiftHold.ManualTransmission);

        if (inputs.GearCount <= 1)
            return ShiftDecision.Idle(ShiftHold.NoForwardGears);

        if (inputs.Rpm <= 0)
            return ShiftDecision.Idle(ShiftHold.RpmUnavailable);

        if (now < rt.ShiftInhibitedUntilUtc)
            return ShiftDecision.Idle(ShiftHold.InhibitedAfterFailures, 0, (rt.ShiftInhibitedUntilUtc - now).TotalSeconds);

        if (rt.Pending != ShiftCommand.None)
            return AdvancePending(rt, inputs, cfg, now);

        int gear = inputs.Gear;
        if (gear < 0)
            return ShiftDecision.Idle(ShiftHold.Reverse);

        // A neutral truck with the driver asking to move is dead weight for the assist; pick a launch gear.
        if (gear == 0)
            return TryNeutralRecovery(rt, inputs, cfg, now);

        return TryResetWhenStopped(rt, inputs, cfg, now)
               ?? Evaluate(rt, inputs, cfg, now, gear);
    }

    // MARK: Sampling

    private static void Sample(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg, DateTime now)
    {
        double dt = rt.LastSampleUtc == DateTime.MinValue ? 0 : (now - rt.LastSampleUtc).TotalSeconds;
        rt.LastSampleUtc = now;

        float speed = inputs.SpeedMps;

        if (dt > 0.005 && dt < 0.5)
        {
            rt.ThrottleRate = (inputs.Throttle - rt.LastThrottle) / dt;
            rt.SmoothedAccel = rt.AccelFilter.Update((float)((speed - rt.LastSpeed) / dt));
        }

        rt.LastSpeed = speed;
        rt.LastThrottle = inputs.Throttle;

        // A gear we did not ask for means the driver or the game took over.
        if (inputs.Gear != rt.LastObservedGear)
        {
            if (rt.Pending == ShiftCommand.None)
            {
                rt.ManualShiftCount++;
                rt.ManualPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
                rt.UpshiftPauseUntilUtc = rt.ManualPauseUntilUtc;
            }

            rt.LastObservedGear = inputs.Gear;
        }

        // Only the pedal is a takeover signal. The game applies its own clutch for every shift it makes
        // on our behalf, so reading gameClutch here would pause the plug-in after each of its own shifts.
        if (inputs.UserClutch > cfg.ClutchThreshold)
        {
            rt.ManualPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
            rt.UpshiftPauseUntilUtc = rt.ManualPauseUntilUtc;
        }
        else if (inputs.Brake > cfg.BrakeThreshold)
        {
            // Braking only removes upshifts; downshifts stay available so the engine keeps braking.
            rt.UpshiftPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
        }

        bool stopped = Math.Abs(inputs.SpeedMps) <= cfg.StoppedSpeedKph / 3.6;
        if (stopped)
        {
            if (rt.StoppedSinceUtc == DateTime.MinValue)
                rt.StoppedSinceUtc = now;
        }
        else
        {
            rt.StoppedSinceUtc = DateTime.MinValue;
        }

        UpdateLoadEstimate(rt, inputs, cfg);
    }

    private static void UpdateLoadEstimate(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg)
    {
        double target = 0;
        if (cfg.AdaptiveShiftPoints)
        {
            double demand = inputs.Throttle - inputs.Brake;
            if (demand > ResistanceDemandFloor && Math.Abs(inputs.SpeedMps) > 1f)
            {
                double scale = Math.Clamp((demand - ResistanceDemandFloor) / (1 - ResistanceDemandFloor), 0, 1);
                double shortfall = Math.Clamp((FreeRollAccelMps2 - rt.SmoothedAccel) / FreeRollAccelMps2, 0, 1);
                double massFactor = Math.Clamp(inputs.MassKg / 1000.0 / FullLoadTonnes, 0, 1) * MassResistanceWeight;
                target = Math.Clamp(scale * shortfall + massFactor, 0, 1);
            }
        }

        // Smoothed so one bad sample cannot move both shift lines at once.
        rt.Resistance = rt.Resistance * 0.7 + target * 0.3;

        UpdateGrade(rt, inputs, cfg);

        // A slope is a hard load signal: the same throttle buys less acceleration climbing, and the pitch
        // says how much of the shortfall is the hill rather than the trailer.
        if (cfg.UseGradeSignal && cfg.AdaptiveShiftPoints && rt.Grade > 0)
        {
            double slope = Math.Clamp(rt.Grade / cfg.GradeFullSlope, 0, 1);
            rt.Resistance = Math.Max(rt.Resistance, slope * 0.8);
        }
    }

    /// <summary>
    ///  Pitch reaches us as normalised turns. The sign convention (does positive mean uphill?) is not
    ///  documented, so it is learned from the first moment the truck is clearly climbing or descending.
    /// </summary>
    private static void UpdateGrade(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg)
    {
        if (!cfg.UseGradeSignal)
        {
            rt.Grade = 0;
            return;
        }

        double pitchRadians = inputs.PitchTurns * Math.PI * 2.0;
        double raw = Math.Tan(Math.Clamp(pitchRadians, -0.5, 0.5));

        if (!rt.GradeSignKnown && Math.Abs(raw) > 0.003)
        {
            double demand = inputs.Throttle - inputs.Brake;
            double accel = rt.SmoothedAccel;

            // Climbing: power on but still losing speed. Descending: coasting and gaining speed.
            bool climbing = demand > ResistanceDemandFloor && accel < -0.2;
            bool descending = demand < 0.1 && inputs.Brake < cfg.BrakeThreshold && accel > 0.2;

            if (climbing || descending)
            {
                int sign = Math.Sign(raw);
                if (sign != 0)
                {
                    rt.GradeSign = sign;
                    rt.GradeSignKnown = true;
                }
            }
        }

        rt.Grade = rt.GradeSignKnown ? raw * rt.GradeSign : 0;
    }

    // MARK: Closed loop shift verification

    private static ShiftDecision AdvancePending(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg, DateTime now)
    {
        int delta = inputs.Gear - rt.PendingFromGear;
        bool inDirection = rt.Pending == ShiftCommand.Up ? delta > 0 : delta < 0;

        if (inDirection)
        {
            bool reached = rt.Pending == ShiftCommand.Up
                ? inputs.Gear >= rt.PendingTargetGear
                : inputs.Gear <= rt.PendingTargetGear;

            if (!reached)
            {
                // The box is still walking through the queued steps. Rebase onto where it is now and
                // give it a fresh window instead of deciding the request was ignored.
                rt.PendingFromGear = inputs.Gear;
                rt.PendingSentUtc = now;
                rt.PendingAttempts = 0;
                rt.LastObservedGear = inputs.Gear;
                return ShiftDecision.Idle(ShiftHold.ShiftInProgress);
            }

            rt.RecordLatency((now - rt.PendingSentUtc).TotalMilliseconds);

            if (rt.Pending == ShiftCommand.Up)
                rt.UpshiftCount++;
            else
                rt.DownshiftCount++;

            // Measure against the gear the request started from, not the last rebase, so a multi-gear
            // request that walked through an intermediate gear still counts as a skip.
            int travelled = Math.Abs(inputs.Gear - rt.PendingOriginGear);
            if (travelled > 1)
                rt.SkippedShiftCount += travelled - 1;

            if (rt.Pending == ShiftCommand.Down)
                rt.PostDownshiftDwellUntilUtc = now.AddMilliseconds(cfg.PostDownshiftDwellMs);
            else
                rt.PostUpshiftDwellUntilUtc = now.AddMilliseconds(cfg.PostDownshiftDwellMs);

            rt.ClearPending();
            rt.LastObservedGear = inputs.Gear;
            // Minimum time in gear counts from the shift that actually completed.
            rt.NextShiftAllowedUtc = now.AddMilliseconds(cfg.ShiftCooldownMs);

            return new ShiftDecision(ShiftCommand.None, 0, ShiftHold.None) { ConfirmedGear = inputs.Gear };
        }

        // Left the box entirely (neutral or reverse): there is nothing left to confirm.
        if (inputs.Gear <= 0)
        {
            rt.ClearPending();
            rt.NextShiftAllowedUtc = now.AddMilliseconds(cfg.ShiftCooldownMs);
            return ShiftDecision.Idle(ShiftHold.ShiftInProgress);
        }

        double elapsed = (now - rt.PendingSentUtc).TotalMilliseconds;

        // The game is still travelling through the gate. Pushing again here is what turns one requested
        // gear into two, so the verify clock restarts instead.
        if (inputs.GameClutch > cfg.ShiftInProgressClutch && elapsed < ShiftInProgressTimeoutMs)
        {
            rt.PendingSentUtc = now;
            return ShiftDecision.Idle(ShiftHold.ShiftInProgress);
        }

        if (elapsed < rt.VerifyTimeoutMs(VerifyTimeoutFloorMs))
            return ShiftDecision.Idle(ShiftHold.ShiftInProgress);

        // Asking for an upshift and then braking is a change of mind, not a failed shift.
        bool withdrawn = rt.Pending == ShiftCommand.Up && inputs.Brake > cfg.BrakeThreshold;

        ShiftCommand abandoned = rt.Pending;
        int abandonedSteps = rt.PendingSteps;

        if (!withdrawn && rt.PendingAttempts < MaxShiftAttempts)
        {
            // The count only advances after a full verify window with no game clutch movement, so a slow
            // gearbox is never mistaken for an ignored one.
            rt.PendingAttempts++;
            rt.PendingSentUtc = now;

            return new ShiftDecision(abandoned, abandonedSteps, ShiftHold.None) { IsRetry = true };
        }

        rt.ClearPending();
        rt.NextShiftAllowedUtc = now.AddMilliseconds(cfg.ShiftCooldownMs);

        if (withdrawn)
            return ShiftDecision.Idle(ShiftHold.BrakeHeld);

        rt.FailedShiftCount++;
        return ShiftDecision.Failed();
    }

    // MARK: Neutral and stopped handling

    private static ShiftDecision TryNeutralRecovery(ShiftRuntime rt, ShiftInputs inputs, SequentialAutoShiftSettings cfg, DateTime now)
    {
        if (now < rt.NextShiftAllowedUtc || now < rt.ManualPauseUntilUtc)
            return ShiftDecision.Idle(ShiftHold.Neutral);

        if (inputs.Throttle < 0.05f)
            return ShiftDecision.Idle(ShiftHold.Neutral);

        // Selecting a gear at road speed would overrev; a launch needs a crawl.
        if (Math.Abs(inputs.SpeedMps) > 3f)
            return ShiftDecision.Idle(ShiftHold.Neutral);

        BeginShift(rt, ShiftCommand.Up, 0, 1, now);
        return ShiftDecision.Shift(ShiftCommand.Up, 1, EffectiveStartGear(cfg, inputs), inputs.Rpm)
                with { Hold = ShiftHold.Neutral };
    }

    private static ShiftDecision? TryResetWhenStopped(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now)
    {
        if (!cfg.ResetGearWhenStopped || !cfg.DownshiftEnabled)
            return null;

        int target = EffectiveStartGear(cfg, inputs);
        int gear = inputs.Gear;
        if (gear <= target || rt.StoppedSinceUtc == DateTime.MinValue)
            return null;

        if ((now - rt.StoppedSinceUtc).TotalMilliseconds < cfg.StopDwellMs)
            return null;

        if (now < rt.NextShiftAllowedUtc || now < rt.ManualPauseUntilUtc)
            return ShiftDecision.Idle(ShiftHold.Stopped);

        // One gear per shift cycle takes ten seconds from twelfth, and the driver is already on the
        // throttle by then. A sequential box queues presses, so send a burst instead of waiting.
        int steps = Math.Min(ResetBurstMaxSteps, gear - target);

        BeginShift(rt, ShiftCommand.Down, gear, steps, now);
        return ShiftDecision.Shift(ShiftCommand.Down, steps, target, steps) with { Hold = ShiftHold.Stopped };
    }

    // MARK: Main evaluation

    private static ShiftDecision Evaluate(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now,
        int gear)
    {
        float rpm = inputs.Rpm;
        float throttle = inputs.Throttle;
        float brake = inputs.Brake;
        float speed = Math.Abs(inputs.SpeedMps);
        double maxRpm = MaxEngineRpm(inputs.RpmMax);
        double resistance = cfg.AdaptiveShiftPoints ? rt.Resistance : 0;

        bool braking = brake > cfg.BrakeThreshold;
        bool engineBraking = cfg.HoldGearForEngineBrake && (inputs.MotorBrake || inputs.RetarderStep > 0);
        bool descending = cfg.HoldGearForEngineBrake && rt.Grade < 0 &&
                          Math.Abs(rt.Grade) > cfg.GradeFullSlope * 0.5 &&
                          throttle < 0.05f && speed > 8f;

        double upLine = UpshiftLine(cfg, throttle, maxRpm, resistance);
        double downLine = braking || engineBraking || descending
            ? DownshiftLine(cfg, throttle, resistance, braking, engineBraking, descending)
            : KickdownLine(cfg, throttle, rt.ThrottleRate, resistance);
        double lugFloor = LugFloor(cfg, maxRpm, resistance);

        // A shift is not instantaneous: while the box travels through the gate the engine keeps climbing, so
        // the request has to go out a little early or the engine passes the redline during the change. The
        // lead only ever *lowers* the trigger, never the line the landing is judged against, and it can never
        // push the trigger under the lug floor (that would deadlock first gear).
        double lead = maxRpm * rt.VerifyTimeoutMs(VerifyTimeoutFloorMs) / 1000.0 * RevClimbFraction;
        double trigger = Math.Max(lugFloor, upLine - lead);
        trigger = Math.Min(trigger, upLine);

        int ratioCount = inputs.Ratios?.Length ?? 0;
        int topGear = Math.Min(inputs.GearCount, ratioCount > 0 ? ratioCount : inputs.GearCount);

        bool steering = cfg.AvoidShiftingInTurns &&
                        Math.Abs(inputs.Steer) > cfg.TurnSteerThreshold &&
                        speed < cfg.TurnMaxSpeedKph / 3.6;

        // Over the limiter the gear has to go, but only an upshift can save the engine and the box may be
        // busy. This is deliberately the *only* rule that outranks the holds below.
        if (rpm >= maxRpm && gear < topGear && now >= rt.NextShiftAllowedUtc && now >= rt.ManualPauseUntilUtc)
            return BeginUpshift(rt, inputs, cfg, now, gear, topGear, upLine, lugFloor, resistance, force: true);

        ShiftDecision? up = TryUpshift(rt, inputs, cfg, now, gear, topGear, speed, brake,
            upLine, trigger, lugFloor, resistance, steering, engineBraking, descending);
        if (up.HasValue)
            return up.Value;

        ShiftDecision? down = TryDownshift(rt, inputs, cfg, now, gear, rpm, throttle, brake, speed,
            downLine, maxRpm, resistance, braking, engineBraking, descending);
        if (down.HasValue)
            return down.Value;

        return ShiftDecision.Idle(
            ChooseHold(rt, inputs, cfg, now, gear, topGear, speed, brake, upLine, steering, engineBraking, descending),
            upLine,
            downLine);
    }

    private static ShiftDecision BeginUpshift(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now,
        int gear,
        int topGear,
        double upLine,
        double lugFloor,
        double resistance,
        bool force)
    {
        int steps = 1;
        if (!force && cfg.AllowSkipShift && IsLightlyLoaded(inputs, cfg))
        {
            // A driver skips gears when the truck is light: there is no reason to spend an extra clutch cycle
            // on a gear that will be left immediately. Two conditions bound it, both physical:
            //   - the landing must stay above the lug floor, or the engine stalls down;
            //   - the landing must be below the upshift line, or the gear we just entered would ask to shift
            //     again on the next tick, which is the same ping-pong the single-step guard forbids.
            int maxSkip = Math.Clamp(cfg.MaxSkipGears, 1, 3);
            for (int k = 2; k <= Math.Min(maxSkip, topGear - gear); k++)
            {
                if (!TryProjectRpm(inputs, gear, gear + k, inputs.Rpm, out float landing))
                    break;

                if (landing < lugFloor)
                    break;

                if (landing >= upLine)
                    break;

                steps = k;
            }

        }

        BeginShift(rt, ShiftCommand.Up, gear, steps, now);
        return ShiftDecision.Shift(ShiftCommand.Up, steps, upLine, inputs.Rpm);
    }

    private static ShiftDecision? TryUpshift(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now,
        int gear,
        int topGear,
        float speed,
        float brake,
        double upLine,
        double trigger,
        double lugFloor,
        double resistance,
        bool steering,
        bool engineBraking,
        bool descending)
    {
        if (gear >= topGear)
            return null;

        if (now < rt.NextShiftAllowedUtc || now < rt.ManualPauseUntilUtc)
            return null;

        if (steering)
            return null;

        if (engineBraking || descending)
            return null;

        if (now < rt.UpshiftPauseUntilUtc || brake > cfg.BrakeThreshold)
            return null;

        if (now < rt.PostDownshiftDwellUntilUtc)
            return null;

        if (speed < cfg.MinimumSpeedKph / 3.6)
            return null;

        if (inputs.Rpm < trigger)
            return null;

        bool projected = TryProjectRpm(inputs, gear, gear + 1, inputs.Rpm, out float landingRpm);

        // Without ratios the schedule is all we have; shift and let the closed loop catch a lug.
        if (!projected)
            return BeginUpshift(rt, inputs, cfg, now, gear, topGear, upLine, lugFloor, resistance, force: false);

        if (landingRpm < lugFloor)
        {
            rt.VetoCount++;
            return ShiftDecision.Idle(ShiftHold.LandingTooLow, landingRpm, lugFloor);
        }

        return BeginUpshift(rt, inputs, cfg, now, gear, topGear, upLine, lugFloor, resistance, force: false);
    }

    private static ShiftDecision? TryDownshift(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now,
        int gear,
        float rpm,
        float throttle,
        float brake,
        float speed,
        double downLine,
        double maxRpm,
        double resistance,
        bool braking,
        bool engineBraking,
        bool descending)
    {
        if (!cfg.DownshiftEnabled)
            return null;

        int minGear = Math.Max(1, EffectiveStartGear(cfg, inputs));
        if (gear <= minGear)
            return null;

        if (now < rt.NextShiftAllowedUtc || now < rt.ManualPauseUntilUtc)
            return null;

        bool coasting = brake > cfg.BrakeThreshold || throttle < 0.05f;
        if (coasting && !cfg.CoastDownshiftEnabled)
            return null;

        // An upshift drops the engine far below its line, so the settling window is what keeps the new gear
        // from being abandoned on the next tick.
        if (now < rt.PostUpshiftDwellUntilUtc)
            return null;

        if (gear - 1 == 1 && speed > (cfg.MinimumSpeedKph + FirstGearExtraKph) / 3.6)
            return null;

        if (rpm > downLine)
            return null;

        double overrev = maxRpm * OverrevLandingFraction;
        double lugFloor = LugFloor(cfg, maxRpm, resistance);

        // A skip is only justified when going one gear would immediately ask for another shift. Under
        // braking or on the retarder that is how a driver reaches the braking band in one move; a plain
        // coasting downshift stays sequential. The deepest legal landing is bounded by the overrev cap
        // on one side and the lug floor on the other, so no extra rule is needed.
        int steps = 1;
        bool allowSkip = cfg.AllowSkipShift && (braking || engineBraking || descending) && speed > 2f;

        if (allowSkip)
        {
            int maxSkip = Math.Clamp(cfg.MaxSkipGears, 1, 3);
            for (int k = 1; k <= Math.Min(maxSkip, gear - minGear); k++)
            {
                if (!TryProjectRpm(inputs, gear, gear - k, rpm, out float landing))
                    break;

                if (landing > overrev || landing < lugFloor)
                    break;

                steps = k;
            }
        }

        if (TryProjectRpm(inputs, gear, gear - steps, rpm, out float finalLanding))
        {
            if (finalLanding > overrev)
            {
                rt.VetoCount++;
                return ShiftDecision.Idle(ShiftHold.LandingTooHigh, finalLanding, overrev);
            }

            if (finalLanding < lugFloor)
            {
                // A skip would have landed too low to pull cleanly; fall back to the single step.
                steps = 1;
            }
        }

        BeginShift(rt, ShiftCommand.Down, gear, steps, now);
        return ShiftDecision.Shift(ShiftCommand.Down, steps, downLine, rpm);
    }

    private static void BeginShift(ShiftRuntime rt, ShiftCommand direction, int fromGear, int steps, DateTime now)
    {
        rt.Pending = direction;
        rt.PendingFromGear = fromGear;
        rt.PendingOriginGear = fromGear;
        rt.PendingTargetGear = direction == ShiftCommand.Up ? fromGear + steps : Math.Max(0, fromGear - steps);
        rt.PendingSteps = steps;
        rt.PendingAttempts = 0;
        rt.PendingSentUtc = now;
    }

    private static ShiftHold ChooseHold(
        ShiftRuntime rt,
        ShiftInputs inputs,
        SequentialAutoShiftSettings cfg,
        DateTime now,
        int gear,
        int topGear,
        float speed,
        float brake,
        double upLine,
        bool steering,
        bool engineBraking,
        bool descending)
    {
        if (now < rt.ManualPauseUntilUtc)
            return ShiftHold.ManualTakeover;

        if (now < rt.NextShiftAllowedUtc)
            return ShiftHold.CoolingDown;

        if (steering)
            return ShiftHold.TurnHeld;

        if ((engineBraking || descending) && inputs.Rpm >= upLine)
            return ShiftHold.EngineBraking;

        if (now < rt.UpshiftPauseUntilUtc || brake > cfg.BrakeThreshold)
            return ShiftHold.BrakeHeld;

        if (now < rt.PostDownshiftDwellUntilUtc)
            return ShiftHold.PostDownshiftDwell;

        if (speed < cfg.MinimumSpeedKph / 3.6)
            return ShiftHold.BelowMinimumSpeed;

        if (inputs.GameClutch > cfg.ShiftInProgressClutch)
            return ShiftHold.ShiftInProgress;

        if (gear >= topGear)
            return ShiftHold.TopGear;

        return ShiftHold.None;
    }

    private static double Lerp(double from, double to, float t) => from + (to - from) * Math.Clamp(t, 0f, 1f);
}
