using System;
using System.Collections.Generic;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Notifications;
using ETS2LA.Settings;
using ETS2LA.Shared;
using ETS2LA.State;

namespace SequentialAutoShift;

[Serializable]
public sealed class SequentialAutoShiftSettings
{
    public int SettingsVersion { get; set; } = 9;

    public bool Enabled { get; set; } = true;
    public bool DownshiftEnabled { get; set; } = true;
    public bool CoastDownshiftEnabled { get; set; } = true;
    public bool StartEngineWhenAccActive { get; set; } = true;
    public bool ResetGearWhenStopped { get; set; } = true;
    public bool AdaptiveShiftPoints { get; set; } = true;

    public int StartGear { get; set; } = 1;

    public double UpshiftRpm { get; set; } = 1500;
    public double UpshiftRpmFullThrottle { get; set; } = 2100;
    public double DownshiftRpm { get; set; } = 1000;
    public double KickdownRpm { get; set; } = 1500;

    public double ShiftHysteresisRpm { get; set; } = 200;
    public double ShiftCooldownMs { get; set; } = 650;
    public double EngineStartRetryMs { get; set; } = 2500;
    public double ManualPauseMs { get; set; } = 1500;
    public double MinimumSpeedKph { get; set; } = 3;
    public double StoppedSpeedKph { get; set; } = 5;
    public double StopDwellMs { get; set; } = 1200;
    public double BrakeThreshold { get; set; } = 0.08;
    public double ClutchThreshold { get; set; } = 0.08;
}

public enum AutoShiftDirection
{
    None,
    Up,
    Down
}

public sealed class SequentialAutoShiftPlugin : Plugin
{
    public static SequentialAutoShiftPlugin? Instance { get; private set; }

    public SequentialAutoShiftPlugin()
    {
        Instance = this;
    }

    private const string PluginId = "local.sequentialautoshift";
    private const string SettingsFileName = "SequentialAutoShift.json";
    private const string ControlChannelId = "SequentialAutoShift.Shifter";
    private const string EngineStartChannelId = "SequentialAutoShift.EngineStart";

    // A shift must not be held past the rev limiter, and a downshift must not land close
    // enough to the limiter to trip it on engagement.
    private const float RevLimitUpshiftFraction = 0.97f;
    private const float OverrevLandingFraction = 0.94f;

    // Resistance is estimated from "throttle demanded but the truck does not accelerate",
    // which covers gradient and load without a road-slope signal.
    private const float FreeRollAccelMps2 = 0.5f;
    private const float ResistanceDemandFloor = 0.3f;
    private const double ResistanceUpshiftBonusRpm = 400;
    private const double ResistanceDownshiftBonusRpm = 250;
    private const double FullLoadTonnes = 40;
    private const double MassResistanceWeight = 0.35;

    // Reaching 1st is a launch action; above a low speed it would ping-pong with 2nd.
    private const double FirstGearExtraKph = 5;

    private const double ShiftVerifyMs = 900;
    private const int MaxShiftAttempts = 3;
    private const double ShiftFailureInhibitMs = 20000;
    private const int MaxEngineStartAttempts = 5;
    private const double EngineStartLockoutMs = 30000;
    private const double IgnitionToStartMs = 700;

    private readonly object sync = new();
    private readonly ControlChannelDefinition shiftChannel = new()
    {
        Id = ControlChannelId,
        Timeout = 0.3f
    };
    private readonly ControlChannelDefinition engineStartChannel = new()
    {
        Id = EngineStartChannelId,
        Timeout = 0.3f
    };
    private readonly KalmanFilter accelFilter = new(0.02f, 0.25f, 1f);

    private SettingsHandler? settingsHandler;
    private SequentialAutoShiftSettings settings = new();
    private GameTelemetryData telemetry = new();

    private DateTime nextShiftAllowedUtc = DateTime.MinValue;
    private DateTime shiftInhibitedUntilUtc = DateTime.MinValue;
    private DateTime manualPauseUntilUtc = DateTime.MinValue;
    private DateTime upshiftPauseUntilUtc = DateTime.MinValue;
    private DateTime nextEngineStartAllowedUtc = DateTime.MinValue;
    private DateTime pendingEngineStartUtc = DateTime.MinValue;
    private DateTime engineStartLockedUntilUtc = DateTime.MinValue;
    private DateTime stoppedSinceUtc = DateTime.MinValue;
    private DateTime lastSampleUtc = DateTime.MinValue;
    private DateTime pendingSentUtc = DateTime.MinValue;

    private AutoShiftDirection pendingDirection = AutoShiftDirection.None;
    private int pendingFromGear;
    private int shiftAttempts;

    private int lastObservedGear;
    private int engineStartAttempts;
    private float lastSpeed;
    private float smoothedAccel;
    private double resistance;
    private string status = "Waiting for telemetry";
    private string lastVeto = "none";

    public int UpshiftCount { get; private set; }
    public int DownshiftCount { get; private set; }
    public int VetoCount { get; private set; }
    public int FailedShiftCount { get; private set; }
    public int ManualShiftCount { get; private set; }

    public override PluginInformation Info { get; } = new()
    {
        Id = PluginId,
        Name = "Sequential Auto Shift",
        Description = "Sequential gearbox auto shifting from a throttle-interpolated shift schedule with gear-ratio landing checks and closed-loop shift verification.",
        Version = "0.3.0",
        SupportedETS2LA = "*",
        AuthorName = "piggywu981",
        Tags = new[] { "Driving", "Transmission", "Utility" }
    };

    public override float TickRate => 20f;

    public override void OnEnable()
    {
        settingsHandler = new SettingsHandler();
        settings = settingsHandler.Load<SequentialAutoShiftSettings>(SettingsFileName);
        NormalizeSettings(saveIfChanged: true);
        settingsHandler.RegisterListener<SequentialAutoShiftSettings>(SettingsFileName, OnSettingsChanged);

        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);

        lock (sync)
        {
            telemetry = GameTelemetry.Current.GetCurrentData();
            ResetRuntimeState(telemetry.truckInt.gear);
        }

        base.OnEnable();
        Notify($"{PluginId}.Enabled", "Sequential Auto Shift", "Automatic sequential shifting is ready.",
            NotificationLevel.Information, 4f);
    }

    public override void Tick()
    {
        lock (sync)
        {
            GameTelemetryData data = telemetry;
            SequentialAutoShiftSettings cfg = settings;

            SampleInputs(data, cfg);

            if (!cfg.Enabled)
            {
                status = "Disabled";
                return;
            }

            if (!data.sdkActive)
            {
                status = "SDK inactive";
                return;
            }

            if (data.paused)
            {
                status = "Game paused";
                return;
            }

            if (TryStartEngineForAcc(data, cfg))
                return;

            VerifyPendingShift(data, cfg);
            if (pendingDirection != AutoShiftDirection.None)
                return;

            if (!data.truckBool.engineEnabled)
            {
                status = "Engine off";
                return;
            }

            if (data.configUI.gears <= 1)
            {
                status = "No forward gears available";
                return;
            }

            if (data.truckFloat.engineRpm <= 0)
            {
                status = "RPM unavailable";
                return;
            }

            int gear = data.truckInt.gear;
            if (gear <= 0)
            {
                status = gear == 0 ? "Neutral - nothing to shift" : "Reverse - shifting disabled";
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < shiftInhibitedUntilUtc)
            {
                status = $"Inhibited after ignored shifts ({Math.Ceiling((shiftInhibitedUntilUtc - now).TotalSeconds):0}s)";
                return;
            }

            if (TryResetGearWhenStopped(data, cfg, gear, now))
                return;

            float rpm = data.truckFloat.engineRpm;
            float speed = Math.Abs(data.truckFloat.speed);
            float throttle = EffectiveThrottle(data);
            float brake = EffectiveBrake(data);
            double maxRpm = MaxEngineRpm(data);

            bool wantUp = EvaluateUpshift(data, cfg, gear, rpm, speed, throttle, brake, maxRpm, now, out string? holdUp);
            bool wantDown = EvaluateDownshift(data, cfg, gear, rpm, speed, throttle, brake, maxRpm, now, out string? holdDown);

            if (wantUp)
            {
                RequestShift(AutoShiftDirection.Up, gear, now, $"{rpm:0} rpm");
                return;
            }

            if (wantDown)
            {
                RequestShift(AutoShiftDirection.Down, gear, now, $"{rpm:0} rpm");
                return;
            }

            status = holdUp ?? holdDown ??
                     $"Holding {FormatGear(gear)} @ {rpm:0} rpm " +
                     $"(upshift {UpshiftLine(cfg, throttle, maxRpm):0} / downshift {DownshiftLine(cfg, throttle):0})";
        }
    }

    public override void OnDisable()
    {
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        settingsHandler?.UnregisterListener<SequentialAutoShiftSettings>(SettingsFileName, OnSettingsChanged);
        settingsHandler?.Dispose();
        settingsHandler = null;
        ReleaseShiftChannel();
        ReleaseEngineStartChannel();
        base.OnDisable();
    }

    public override void Shutdown()
    {
        OnDisable();
    }

    // MARK: Snapshot for the settings page

    public SequentialAutoShiftSettings Settings
    {
        get
        {
            lock (sync)
            {
                return settings;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (sync)
            {
                return status;
            }
        }
    }

    public GameTelemetryData LatestTelemetry
    {
        get
        {
            lock (sync)
            {
                return telemetry;
            }
        }
    }

    public float UpshiftRpmSliderMax
    {
        get
        {
            GameTelemetryData data = LatestTelemetry;
            if (data.configFloat.engineRpmMax <= 500)
                return 3500;

            // The schedule can never ask for more than 97% of the redline, so the slider must not
            // offer numbers the plugin would ignore.
            return (float)Math.Floor(MaxEngineRpm(data) * RevLimitUpshiftFraction);
        }
    }

    public float UpshiftRpmSliderValue
    {
        get
        {
            lock (sync)
            {
                return SliderUpshiftRpm(settings.UpshiftRpm);
            }
        }
    }

    public float UpshiftFullThrottleSliderValue
    {
        get
        {
            lock (sync)
            {
                return SliderUpshiftRpm(settings.UpshiftRpmFullThrottle);
            }
        }
    }

    private float SliderUpshiftRpm(double rpm)
    {
        return (float)Math.Min(rpm, UpshiftRpmSliderMax);
    }

    // MARK: Display units

    public static string SpeedUnitLabel
    {
        get
        {
            return UnitConversions.GetUnitAbbreviation(UnitType.Speed, ApplicationState.Current.DisplayUnits);
        }
    }

    public static float SpeedFromKmh(double kmh)
    {
        float metersPerSecond = UnitConversions.ToScientificUnits(UnitType.Speed, (float)kmh, Units.Metric);
        return UnitConversions.FromScientificUnits(UnitType.Speed, metersPerSecond, ApplicationState.Current.DisplayUnits);
    }

    public static double KmhFromSpeed(float displayed)
    {
        float metersPerSecond = UnitConversions.ToScientificUnits(UnitType.Speed, displayed, ApplicationState.Current.DisplayUnits);
        return UnitConversions.FromScientificUnits(UnitType.Speed, metersPerSecond, Units.Metric);
    }

    // MARK: Sampling

    private void OnTelemetry(GameTelemetryData data)
    {
        lock (sync)
        {
            telemetry = data;
        }
    }

    private void SampleInputs(GameTelemetryData data, SequentialAutoShiftSettings cfg)
    {
        DateTime now = DateTime.UtcNow;
        double dt = lastSampleUtc == DateTime.MinValue ? 0 : (now - lastSampleUtc).TotalSeconds;
        lastSampleUtc = now;

        float speed = Math.Abs(data.truckFloat.speed);
        if (dt > 0.005 && dt < 0.5)
            smoothedAccel = accelFilter.Update((float)((speed - lastSpeed) / dt));
        lastSpeed = speed;

        int gear = data.truckInt.gear;
        if (gear != lastObservedGear)
        {
            // A gear change we did not ask for means the driver or the game took over.
            if (pendingDirection == AutoShiftDirection.None)
            {
                ManualShiftCount++;
                manualPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
                upshiftPauseUntilUtc = manualPauseUntilUtc;
            }
            lastObservedGear = gear;
        }

        if (EffectiveClutch(data) > cfg.ClutchThreshold)
        {
            manualPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
            upshiftPauseUntilUtc = manualPauseUntilUtc;
        }
        else if (EffectiveBrake(data) > cfg.BrakeThreshold)
        {
            // Braking only removes upshifts; downshifts stay available so the engine keeps braking.
            upshiftPauseUntilUtc = now.AddMilliseconds(cfg.ManualPauseMs);
        }

        if (speed <= ToMetersPerSecond(cfg.StoppedSpeedKph))
        {
            if (stoppedSinceUtc == DateTime.MinValue)
                stoppedSinceUtc = now;
        }
        else
        {
            stoppedSinceUtc = DateTime.MinValue;
        }

        UpdateResistance(data, cfg);
    }

    private void UpdateResistance(GameTelemetryData data, SequentialAutoShiftSettings cfg)
    {
        if (!cfg.AdaptiveShiftPoints)
        {
            resistance = 0;
            return;
        }

        double target = 0;
        double demand = EffectiveThrottle(data) - EffectiveBrake(data);
        if (demand > ResistanceDemandFloor && Math.Abs(data.truckFloat.speed) > 1f)
        {
            double scale = Math.Clamp((demand - ResistanceDemandFloor) / (1 - ResistanceDemandFloor), 0, 1);
            double shortfall = Math.Clamp((FreeRollAccelMps2 - smoothedAccel) / FreeRollAccelMps2, 0, 1);
            double tonnes = (data.configFloat.cargoMass + data.configFloat.unitMass) / 1000.0;
            double massFactor = Math.Clamp(tonnes / FullLoadTonnes, 0, 1) * MassResistanceWeight;
            target = Math.Clamp(scale * shortfall + massFactor, 0, 1);
        }

        // Smoothed so one bad sample cannot move both shift lines at once.
        resistance = resistance * 0.7 + target * 0.3;
    }

    // MARK: Shift schedule

    private static double MaxEngineRpm(GameTelemetryData data)
    {
        return data.configFloat.engineRpmMax > 500 ? data.configFloat.engineRpmMax : 2000;
    }

    private double UpshiftLine(SequentialAutoShiftSettings cfg, float throttle, double maxRpm)
    {
        double line = Lerp(cfg.UpshiftRpm, Math.Max(cfg.UpshiftRpmFullThrottle, cfg.UpshiftRpm), throttle);
        if (cfg.AdaptiveShiftPoints)
            line += resistance * ResistanceUpshiftBonusRpm;

        return Math.Min(line, maxRpm * RevLimitUpshiftFraction);
    }

    private double DownshiftLine(SequentialAutoShiftSettings cfg, float throttle)
    {
        double line = Lerp(cfg.DownshiftRpm, Math.Max(cfg.KickdownRpm, cfg.DownshiftRpm), throttle);
        if (cfg.AdaptiveShiftPoints)
            line += resistance * ResistanceDownshiftBonusRpm;

        return line;
    }

    private double LandingFloor(SequentialAutoShiftSettings cfg)
    {
        // Throttle independent on purpose: a kickdown downshift right after an upshift is a
        // deliberate request, while this floor only exists to stop automatic hunting.
        double floor = cfg.DownshiftRpm + cfg.ShiftHysteresisRpm;
        if (cfg.AdaptiveShiftPoints)
            floor += resistance * ResistanceDownshiftBonusRpm;

        return floor;
    }

    private bool EvaluateUpshift(
        GameTelemetryData data,
        SequentialAutoShiftSettings cfg,
        int gear,
        float rpm,
        float speed,
        float throttle,
        float brake,
        double maxRpm,
        DateTime now,
        out string? hold)
    {
        hold = null;

        if (gear >= data.configUI.gears)
        {
            hold = $"Already in top gear {FormatGear(gear)}";
            return false;
        }

        if (now < nextShiftAllowedUtc)
        {
            hold = "Cooling down";
            return false;
        }

        if (now < manualPauseUntilUtc)
        {
            hold = "Paused after manual input";
            return false;
        }

        if (now < upshiftPauseUntilUtc || brake > cfg.BrakeThreshold)
        {
            hold = "Brake applied - upshift held";
            return false;
        }

        if (speed < ToMetersPerSecond(cfg.MinimumSpeedKph))
        {
            hold = "Below minimum upshift speed";
            return false;
        }

        // Retarder and engine brake want the revs kept inside the braking band.
        if (data.truckBool.motorBrake || data.truckUI.retarderBrake > 0)
        {
            hold = "Holding gear for engine braking";
            return false;
        }

        bool atRevLimiter = rpm >= maxRpm * RevLimitUpshiftFraction;
        if (!atRevLimiter && rpm < UpshiftLine(cfg, throttle, maxRpm))
            return false;

        bool projected = TryProjectShiftRpm(data, gear, gear + 1, rpm, out float landingRpm);

        // Nothing may be held past the limiter, whatever the next gear would do to the revs.
        if (atRevLimiter)
            return true;

        if (!projected)
            return true;

        double floor = LandingFloor(cfg);
        if (landingRpm < floor)
        {
            VetoCount++;
            lastVeto = $"up {gear}->{gear + 1} would land at {landingRpm:0} rpm, floor {floor:0}";
            hold = $"Upshift held: {FormatGear(gear + 1)} would lug at {landingRpm:0} rpm";
            return false;
        }

        return true;
    }

    private bool EvaluateDownshift(
        GameTelemetryData data,
        SequentialAutoShiftSettings cfg,
        int gear,
        float rpm,
        float speed,
        float throttle,
        float brake,
        double maxRpm,
        DateTime now,
        out string? hold)
    {
        hold = null;

        if (!cfg.DownshiftEnabled)
            return false;

        int minGear = Math.Max(1, cfg.StartGear);
        if (gear <= minGear)
            return false;

        if (now < nextShiftAllowedUtc || now < manualPauseUntilUtc)
            return false;

        bool coasting = brake > cfg.BrakeThreshold || throttle < 0.05f;
        if (coasting && !cfg.CoastDownshiftEnabled)
        {
            hold = "Coast downshift disabled";
            return false;
        }

        if (gear - 1 == 1 && speed > ToMetersPerSecond(cfg.MinimumSpeedKph + FirstGearExtraKph))
        {
            hold = "1st gear reserved for launching";
            return false;
        }

        if (rpm > DownshiftLine(cfg, throttle))
            return false;

        if (!TryProjectShiftRpm(data, gear, gear - 1, rpm, out float landingRpm))
            return true;

        double limit = maxRpm * OverrevLandingFraction;
        if (landingRpm > limit)
        {
            VetoCount++;
            lastVeto = $"down {gear}->{gear - 1} would overrev to {landingRpm:0} rpm, limit {limit:0}";
            hold = $"Downshift held: {FormatGear(gear - 1)} would overrev to {landingRpm:0} rpm";
            return false;
        }

        return true;
    }

    // MARK: Shift request and closed-loop verification

    private void RequestShift(AutoShiftDirection direction, int fromGear, DateTime now, string reason)
    {
        pendingDirection = direction;
        pendingFromGear = fromGear;
        pendingSentUtc = now;
        PulseShift(direction == AutoShiftDirection.Up);
        status = $"{Name(direction)}shift requested {reason}";
    }

    private void VerifyPendingShift(GameTelemetryData data, SequentialAutoShiftSettings cfg)
    {
        if (pendingDirection == AutoShiftDirection.None)
            return;

        AutoShiftDirection direction = pendingDirection;
        int gear = data.truckInt.gear;
        DateTime now = DateTime.UtcNow;

        if (gear != pendingFromGear)
        {
            if (gear <= 0)
            {
                // The gearbox was left (N or R), there is nothing to confirm.
                pendingDirection = AutoShiftDirection.None;
                shiftAttempts = 0;
                return;
            }

            if (direction == AutoShiftDirection.Up)
                UpshiftCount++;
            else
                DownshiftCount++;

            pendingDirection = AutoShiftDirection.None;
            shiftAttempts = 0;
            // Minimum time in gear counts from the shift that actually worked.
            nextShiftAllowedUtc = now.AddMilliseconds(cfg.ShiftCooldownMs);
            status = $"Gear {FormatGear(gear)} confirmed";
            return;
        }

        if ((now - pendingSentUtc).TotalMilliseconds < ShiftVerifyMs)
            return;

        shiftAttempts++;
        FailedShiftCount++;

        bool noLongerWanted = direction == AutoShiftDirection.Up && EffectiveBrake(data) > cfg.BrakeThreshold;

        if (!noLongerWanted && shiftAttempts < MaxShiftAttempts)
        {
            Logger.Warn($"[SequentialAutoShift] {direction} shift from gear {pendingFromGear} had no effect, retrying.");
            pendingSentUtc = now;
            PulseShift(direction == AutoShiftDirection.Up);
            status = $"Retrying {Name(direction)}shift (attempt {shiftAttempts + 1}/{MaxShiftAttempts})";
            return;
        }

        pendingDirection = AutoShiftDirection.None;
        shiftAttempts = 0;
        nextShiftAllowedUtc = now.AddMilliseconds(cfg.ShiftCooldownMs);

        if (noLongerWanted)
            return;

        shiftInhibitedUntilUtc = now.AddMilliseconds(ShiftFailureInhibitMs);
        Logger.Warn($"[SequentialAutoShift] {MaxShiftAttempts} {direction} shift requests from gear {pendingFromGear} had no effect, shifting inhibited for {ShiftFailureInhibitMs / 1000:0}s.");
        Notify(
            $"{PluginId}.ShiftIgnored",
            "Sequential Auto Shift",
            $"{MaxShiftAttempts} {Name(direction)}shift requests in gear {FormatGear(pendingFromGear)} were ignored by the game. " +
            "Check that the transmission is sequential or semi-automatic and that gear up/down inputs are bound. " +
            $"Shifting inhibited for {ShiftFailureInhibitMs / 1000:0}s.",
            NotificationLevel.Warning,
            10f);
        status = "Shift ignored by the game - inhibited temporarily";
    }

    private bool TryResetGearWhenStopped(GameTelemetryData data, SequentialAutoShiftSettings cfg, int gear, DateTime now)
    {
        if (!cfg.ResetGearWhenStopped || !cfg.DownshiftEnabled)
            return false;

        int targetGear = Math.Clamp(cfg.StartGear, 1, Math.Max(1, data.configUI.gears));
        if (gear <= targetGear || stoppedSinceUtc == DateTime.MinValue)
            return false;

        if ((now - stoppedSinceUtc).TotalMilliseconds < cfg.StopDwellMs)
            return false;

        if (now < nextShiftAllowedUtc || now < manualPauseUntilUtc)
            return false;

        RequestShift(AutoShiftDirection.Down, gear, now, $"stopped, resetting to gear {targetGear}");
        return true;
    }

    private void PulseShift(bool up)
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = shiftChannel,
            Properties = new ControlProperties
            {
                BooleanType = ControlBooleanType.TrueToToggle,
                Weight = 1.0f
            },
            Variables = new ControlVariables
            {
                gearup = up,
                geardown = !up
            }
        });
    }

    private void ReleaseShiftChannel()
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = shiftChannel,
            Properties = new ControlProperties(),
            Variables = new ControlVariables()
        });
    }

    // MARK: Engine start

    private bool TryStartEngineForAcc(GameTelemetryData data, SequentialAutoShiftSettings cfg)
    {
        if (!cfg.StartEngineWhenAccActive)
            return false;

        if (!data.sdkActive || data.paused || !IsAccActive())
        {
            pendingEngineStartUtc = DateTime.MinValue;
            return false;
        }

        if (data.truckBool.engineEnabled)
        {
            pendingEngineStartUtc = DateTime.MinValue;
            engineStartAttempts = 0;
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (now < engineStartLockedUntilUtc)
        {
            status = "Engine start locked after repeated failures";
            return true;
        }

        if (now < nextEngineStartAllowedUtc)
            return false;

        if (engineStartAttempts >= MaxEngineStartAttempts)
        {
            engineStartAttempts = 0;
            engineStartLockedUntilUtc = now.AddMilliseconds(EngineStartLockoutMs);
            nextEngineStartAllowedUtc = engineStartLockedUntilUtc;
            pendingEngineStartUtc = DateTime.MinValue;
            Logger.Warn("[SequentialAutoShift] Engine did not start after repeated attempts, retrying later.");
            Notify(
                $"{PluginId}.EngineStartFailed",
                "Sequential Auto Shift",
                $"The engine did not start after {MaxEngineStartAttempts} attempts. Retrying in {EngineStartLockoutMs / 1000:0}s.",
                NotificationLevel.Warning,
                10f);
            return true;
        }

        engineStartAttempts++;

        // The game needs ignition before crank, so the crank half of the sequence is armed by
        // pendingEngineStartUtc and takes an attempt of its own.
        if (pendingEngineStartUtc > DateTime.MinValue && now >= pendingEngineStartUtc)
        {
            PulseEngineStart(ignitionOn: false, startEngine: true);
            pendingEngineStartUtc = DateTime.MinValue;
            nextEngineStartAllowedUtc = now.AddMilliseconds(cfg.EngineStartRetryMs);
            status = "ACC active, cranking the engine";
            return true;
        }

        if (data.truckBool.electricEnabled)
        {
            PulseEngineStart(ignitionOn: false, startEngine: true);
            nextEngineStartAllowedUtc = now.AddMilliseconds(cfg.EngineStartRetryMs);
            status = "ACC active, cranking the engine";
            return true;
        }

        PulseEngineStart(ignitionOn: true, startEngine: false);
        pendingEngineStartUtc = now.AddMilliseconds(IgnitionToStartMs);
        nextEngineStartAllowedUtc = pendingEngineStartUtc;
        status = "ACC active, ignition requested";
        return true;
    }

    public static bool IsAccActive()
    {
        ApplicationState state = ApplicationState.Current;
        // Longitudinal assist (ACC) is active whenever assists are enabled and
        // the user hasn't selected lane-assist-only mode.
        return state.EnableAssists && state.DrivingMode != DrivingMode.LaneAssistOnly;
    }

    private void PulseEngineStart(bool ignitionOn, bool startEngine)
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = engineStartChannel,
            Properties = new ControlProperties
            {
                BooleanType = ControlBooleanType.TrueToToggle,
                Weight = 1.0f
            },
            Variables = new ControlVariables
            {
                ignitionon = ignitionOn,
                ignitionstrt = startEngine
            }
        });
    }

    private void ReleaseEngineStartChannel()
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = engineStartChannel,
            Properties = new ControlProperties(),
            Variables = new ControlVariables
            {
                ignitionon = false,
                ignitionstrt = false
            }
        });
    }

    // MARK: Settings

    public void HandleAction(string actionId, object? value)
    {
        lock (sync)
        {
            switch (actionId)
            {
                case "enabled":
                    settings.Enabled = value is bool enabled && enabled;
                    break;
                case "downshiftEnabled":
                    settings.DownshiftEnabled = value is bool downshift && downshift;
                    break;
                case "coastDownshiftEnabled":
                    settings.CoastDownshiftEnabled = value is bool coast && coast;
                    break;
                case "startEngineWhenAccActive":
                    settings.StartEngineWhenAccActive = value is bool startEngine && startEngine;
                    break;
                case "resetGearWhenStopped":
                    settings.ResetGearWhenStopped = value is bool resetGear && resetGear;
                    break;
                case "adaptiveShiftPoints":
                    settings.AdaptiveShiftPoints = value is bool adaptive && adaptive;
                    break;
                case "startGear":
                    settings.StartGear = (int)Math.Round(ClampDouble(value, 1, 6, settings.StartGear));
                    break;
                case "upshiftRpm":
                    settings.UpshiftRpm = ClampDouble(value, 900, 3500, settings.UpshiftRpm);
                    break;
                case "upshiftRpmFullThrottle":
                    settings.UpshiftRpmFullThrottle = ClampDouble(value, 900, 3500, settings.UpshiftRpmFullThrottle);
                    break;
                case "downshiftRpm":
                    settings.DownshiftRpm = ClampDouble(value, 500, 3000, settings.DownshiftRpm);
                    break;
                case "kickdownRpm":
                    settings.KickdownRpm = ClampDouble(value, 500, 3500, settings.KickdownRpm);
                    break;
                case "shiftHysteresisRpm":
                    settings.ShiftHysteresisRpm = ClampDouble(value, 100, 800, settings.ShiftHysteresisRpm);
                    break;
                case "shiftCooldownMs":
                    settings.ShiftCooldownMs = ClampDouble(value, 250, 2000, settings.ShiftCooldownMs);
                    break;
                case "engineStartRetryMs":
                    settings.EngineStartRetryMs = ClampDouble(value, 1000, 10000, settings.EngineStartRetryMs);
                    break;
                case "manualPauseMs":
                    settings.ManualPauseMs = ClampDouble(value, 0, 5000, settings.ManualPauseMs);
                    break;
                case "minimumSpeedKph":
                    // The slider works in display units, the schedule stores km/h.
                    settings.MinimumSpeedKph = Math.Clamp(
                        KmhFromSpeed((float)ClampDouble(value, 0, 60, SpeedFromKmh(settings.MinimumSpeedKph))), 0, 25);
                    break;
                case "stoppedSpeedKph":
                    settings.StoppedSpeedKph = Math.Clamp(
                        KmhFromSpeed((float)ClampDouble(value, 0, 60, SpeedFromKmh(settings.StoppedSpeedKph))), 0, 5);
                    break;
                case "stopDwellMs":
                    settings.StopDwellMs = ClampDouble(value, 0, 5000, settings.StopDwellMs);
                    break;
                case "brakeThreshold":
                    settings.BrakeThreshold = ClampDouble(value, 0.01, 1, settings.BrakeThreshold);
                    break;
                case "clutchThreshold":
                    settings.ClutchThreshold = ClampDouble(value, 0.01, 1, settings.ClutchThreshold);
                    break;
            }

            NormalizeSettings(saveIfChanged: false);
            settingsHandler?.Save(SettingsFileName, settings);
        }
    }

    private void OnSettingsChanged(SequentialAutoShiftSettings newSettings)
    {
        lock (sync)
        {
            settings = newSettings ?? new SequentialAutoShiftSettings();
            NormalizeSettings(saveIfChanged: false);
            lastObservedGear = telemetry.truckInt.gear;
        }
    }

    private void ResetRuntimeState(int gear)
    {
        pendingDirection = AutoShiftDirection.None;
        pendingFromGear = 0;
        shiftAttempts = 0;
        lastObservedGear = gear;
        stoppedSinceUtc = DateTime.MinValue;
        nextShiftAllowedUtc = DateTime.MinValue;
        shiftInhibitedUntilUtc = DateTime.MinValue;
        manualPauseUntilUtc = DateTime.MinValue;
        upshiftPauseUntilUtc = DateTime.MinValue;
        nextEngineStartAllowedUtc = DateTime.MinValue;
        pendingEngineStartUtc = DateTime.MinValue;
        engineStartAttempts = 0;
        engineStartLockedUntilUtc = DateTime.MinValue;
        resistance = 0;
        smoothedAccel = 0;
        lastSpeed = 0;
        lastSampleUtc = DateTime.MinValue;
        pendingSentUtc = DateTime.MinValue;
        accelFilter.Reset();
    }

    private void NormalizeSettings(bool saveIfChanged)
    {
        bool changed = false;

        // A migration only replaces a value the user never touched, so tuned numbers survive
        // a plugin update.
        if (settings.SettingsVersion < 2)
        {
            settings.DownshiftRpm = Migrate(settings.DownshiftRpm, 1000, 600);
            settings.KickdownRpm = Migrate(settings.KickdownRpm, 1000, 600);
            settings.UpshiftRpm = Migrate(settings.UpshiftRpm, 1800, 2000);
            changed = true;
        }

        if (settings.SettingsVersion < 3)
        {
            settings.DownshiftRpm = Migrate(settings.DownshiftRpm, 600, 800);
            settings.KickdownRpm = Migrate(settings.KickdownRpm, 600, 800);
            changed = true;
        }

        if (settings.SettingsVersion < 4)
        {
            settings.DownshiftRpm = Migrate(settings.DownshiftRpm, 800, 1000);
            settings.KickdownRpm = Migrate(settings.KickdownRpm, 800, 1000);
            changed = true;
        }

        if (settings.SettingsVersion < 5)
            settings.UpshiftRpm = Migrate(settings.UpshiftRpm, 2000, 1800);

        if (settings.SettingsVersion < 7)
        {
            settings.StartGear = (int)Migrate(settings.StartGear, 1, 2);
            settings.StoppedSpeedKph = Migrate(settings.StoppedSpeedKph, 5, 0.5);
            changed = true;
        }

        if (settings.SettingsVersion < 8)
        {
            settings.StartGear = (int)Migrate(settings.StartGear, 2, 1);
            settings.StoppedSpeedKph = Migrate(settings.StoppedSpeedKph, 0.5, 5);
            changed = true;
        }

        if (settings.SettingsVersion < 9)
        {
            // UpshiftRpm is now the zero-throttle corner of the schedule and KickdownRpm the
            // full-throttle downshift corner, so only untouched defaults move.
            settings.UpshiftRpm = Migrate(settings.UpshiftRpm, 1800, 1500);
            settings.KickdownRpm = Migrate(settings.KickdownRpm, 1000, 1500);
            changed = true;
        }

        settings.SettingsVersion = 9;

        double originalUpshift = settings.UpshiftRpm;
        double originalUpshiftFull = settings.UpshiftRpmFullThrottle;
        double originalDownshift = settings.DownshiftRpm;
        double originalKickdown = settings.KickdownRpm;
        double originalHysteresis = settings.ShiftHysteresisRpm;
        int originalStartGear = settings.StartGear;

        settings.UpshiftRpm = Math.Clamp(settings.UpshiftRpm, 900, 3500);
        settings.UpshiftRpmFullThrottle = Math.Clamp(settings.UpshiftRpmFullThrottle, settings.UpshiftRpm, 3500);
        settings.DownshiftRpm = Math.Clamp(settings.DownshiftRpm, 500, settings.UpshiftRpm - 100);
        settings.KickdownRpm = Math.Clamp(settings.KickdownRpm, settings.DownshiftRpm, settings.UpshiftRpmFullThrottle);
        settings.ShiftHysteresisRpm = Math.Clamp(settings.ShiftHysteresisRpm, 100, 800);
        settings.ShiftCooldownMs = Math.Clamp(settings.ShiftCooldownMs, 250, 2000);
        settings.EngineStartRetryMs = Math.Clamp(settings.EngineStartRetryMs, 1000, 10000);
        settings.ManualPauseMs = Math.Clamp(settings.ManualPauseMs, 0, 5000);
        settings.MinimumSpeedKph = Math.Clamp(settings.MinimumSpeedKph, 0, 25);
        settings.StoppedSpeedKph = Math.Clamp(settings.StoppedSpeedKph, 0, 5);
        settings.StopDwellMs = Math.Clamp(settings.StopDwellMs, 0, 5000);
        settings.StartGear = Math.Clamp(settings.StartGear, 1, 6);
        settings.BrakeThreshold = Math.Clamp(settings.BrakeThreshold, 0.01, 1);
        settings.ClutchThreshold = Math.Clamp(settings.ClutchThreshold, 0.01, 1);

        changed |= Math.Abs(originalUpshift - settings.UpshiftRpm) > 0.01;
        changed |= Math.Abs(originalUpshiftFull - settings.UpshiftRpmFullThrottle) > 0.01;
        changed |= Math.Abs(originalDownshift - settings.DownshiftRpm) > 0.01;
        changed |= Math.Abs(originalKickdown - settings.KickdownRpm) > 0.01;
        changed |= Math.Abs(originalHysteresis - settings.ShiftHysteresisRpm) > 0.01;
        changed |= originalStartGear != settings.StartGear;

        if (saveIfChanged && changed)
            settingsHandler?.Save(SettingsFileName, settings);
    }

    private static double Migrate(double value, double previousDefault, double newDefault)
    {
        return Math.Abs(value - previousDefault) < 1 ? newDefault : value;
    }

    // MARK: Telemetry helpers

    private static float EffectiveThrottle(GameTelemetryData data)
    {
        // Cruise control, assists and other plugins apply throttle through the game, so
        // userThrottle alone reads zero while the truck is being driven automatically.
        return Math.Clamp(Math.Max(data.truckFloat.userThrottle, data.truckFloat.gameThrottle), 0f, 1f);
    }

    private static float EffectiveBrake(GameTelemetryData data)
    {
        return Math.Clamp(Math.Max(data.truckFloat.userBrake, data.truckFloat.gameBrake), 0f, 1f);
    }

    private static float EffectiveClutch(GameTelemetryData data)
    {
        return Math.Clamp(Math.Max(data.truckFloat.userClutch, data.truckFloat.gameClutch), 0f, 1f);
    }

    private static bool TryProjectShiftRpm(
        GameTelemetryData data,
        int currentGear,
        int targetGear,
        float currentRpm,
        out float projectedRpm)
    {
        projectedRpm = currentRpm;
        if (currentGear <= 0 || targetGear <= 0)
            return false;

        int currentIndex = currentGear - 1;
        int targetIndex = targetGear - 1;
        if (currentIndex >= data.configFloat.gearRatiosForward.Length ||
            targetIndex >= data.configFloat.gearRatiosForward.Length)
        {
            return false;
        }

        float currentRatio = Math.Abs(data.configFloat.gearRatiosForward[currentIndex]);
        float targetRatio = Math.Abs(data.configFloat.gearRatiosForward[targetIndex]);
        if (currentRatio <= 0.001f || targetRatio <= 0.001f)
            return false;

        projectedRpm = currentRpm * targetRatio / currentRatio;
        return projectedRpm > 0;
    }

    private static string Name(AutoShiftDirection direction)
    {
        return direction == AutoShiftDirection.Up ? "Up" : "Down";
    }

    private static void Notify(string id, string title, string content, NotificationLevel level, float closeAfter)
    {
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = id,
            Title = title,
            Content = content,
            Level = level,
            CloseAfter = closeAfter
        });
    }

    // MARK: Settings page support

    public Dictionary<string, string> StatusRows
    {
        get
        {
            lock (sync)
            {
                GameTelemetryData data = telemetry;
                int gear = data.truckInt.gear;
                int nextGear = Math.Min(gear + 1, data.configUI.gears);
                float throttle = EffectiveThrottle(data);
                double maxRpm = MaxEngineRpm(data);
                float landingRpm = 0;
                bool projected = gear > 0 && gear < data.configUI.gears &&
                                 TryProjectShiftRpm(data, gear, nextGear, data.truckFloat.engineRpm, out landingRpm);

                var rows = new Dictionary<string, string>
                {
                    { "State", status },
                    { "Gear", FormatGear(gear) },
                    { "RPM", $"{data.truckFloat.engineRpm:0} / {maxRpm:0}" },
                    {
                        "Speed",
                        $"{UnitConversions.FromScientificUnits(UnitType.Speed, data.truckFloat.speed, ApplicationState.Current.DisplayUnits):0} {SpeedUnitLabel}"
                    },
                    {
                        "Throttle",
                        $"{throttle * 100:0}% (pedal {Math.Clamp(data.truckFloat.userThrottle, 0f, 1f) * 100:0}%)"
                    },
                    { "Brake", $"{EffectiveBrake(data) * 100:0}%" },
                    { "Resistance", $"{resistance * 100:0}%" },
                    {
                        "Shift lines",
                        gear > 0
                            ? $"{UpshiftLine(settings, throttle, maxRpm):0} up / {DownshiftLine(settings, throttle):0} down"
                            : "-"
                    },
                    { "ACC active", IsAccActive() ? "Yes" : "No" },
                    { "Engine", data.truckBool.engineEnabled ? "On" : "Off" },
                    { "Start gear", settings.StartGear.ToString() },
                    { "Shifts", $"+{UpshiftCount} / -{DownshiftCount}" },
                    { "Vetoes", $"{VetoCount} (last: {lastVeto})" },
                    { "Ignored by game", $"{FailedShiftCount}" },
                    { "Manual shifts", $"{ManualShiftCount}" }
                };

                if (projected)
                    rows["Next gear landing"] = $"{FormatGear(nextGear)} @ {landingRpm:0} rpm";

                if (!string.IsNullOrEmpty(data.configString.shifterType))
                    rows["Transmission"] = data.configString.shifterType;

                return rows;
            }
        }
    }

    public static string FormatGear(int gear)
    {
        if (gear > 0)
            return gear.ToString();
        if (gear < 0)
            return $"R{Math.Abs(gear)}";
        return "N";
    }

    private static float ToMetersPerSecond(double kph)
    {
        return (float)(kph / 3.6);
    }

    private static double Lerp(double from, double to, float t)
    {
        return from + (to - from) * Math.Clamp(t, 0f, 1f);
    }

    private static double ClampDouble(object? value, double min, double max, double fallback)
    {
        double parsed = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, out double d) => d,
            _ => fallback
        };

        return Math.Clamp(parsed, min, max);
    }
}
