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
using static ETS2LA.Translations.T;

namespace SequentialAutoShift;

/// <summary>
///  IO shell around <see cref="ShiftScheduler"/>. This class owns the telemetry snapshot, the settings
///  handler, the output pulses and the status text; every decision about which gear to select lives in
///  the scheduler so it can be exercised without the game (see scratch/shiftsim).
/// </summary>
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

    private const double ShiftFailureInhibitMs = 20000;

    private readonly object sync = new();
    private readonly ControlChannelDefinition shiftChannel = new()
    {
        Id = ControlChannelId,
        Timeout = 0.3f
    };

    private readonly ShiftRuntime runtime = new();

    private SettingsHandler? settingsHandler;
    private SequentialAutoShiftSettings settings = new();
    private ShiftSnapshot snapshot;
    private DateTime lastManualGearboxNoticeUtc = DateTime.MinValue;
    private string status = _("Waiting for telemetry");

    public override PluginInformation Info { get; } = new()
    {
        Id = PluginId,
        Name = "Sequential Auto Shift",
        Description = "Sequential gearbox auto shifting from a throttle-interpolated schedule, checked against the gear ratios before a pulse is sent.",
        Version = "0.4.0",
        AppVersionSupport = new PluginVersionSupport { LowerBound = "", UpperBound = "" },
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
            snapshot = ShiftSnapshot.From(GameTelemetry.Current.GetCurrentData());
            runtime.Reset(snapshot.Gear);
        }

        base.OnEnable();
        Notify($"{PluginId}.Enabled", "Sequential Auto Shift", _("Automatic sequential shifting is ready."),
            NotificationLevel.Information, 4f);
    }

    public override void Tick()
    {
        lock (sync)
        {
            ShiftSnapshot data = snapshot;
            SequentialAutoShiftSettings cfg = settings;

            if (!cfg.Enabled)
            {
                status = _("Disabled");
                return;
            }

            if (!data.SdkActive)
            {
                status = _("SDK inactive");
                return;
            }

            if (data.Paused)
            {
                status = _("Game paused");
                return;
            }

            ShiftDecision decision = ShiftScheduler.Step(runtime, data.ToInputs(cfg), cfg, DateTime.UtcNow);
            Apply(decision, data, cfg);
        }
    }

    private void Apply(ShiftDecision decision, ShiftSnapshot data, SequentialAutoShiftSettings cfg)
    {
        if (decision.Hold == ShiftHold.ManualTransmission)
            NoticeManualGearbox(data);

        if (decision.RequestFailed)
        {
            Logger.Warn($"[SequentialAutoShift] Shift requests from gear {data.Gear} were ignored; inhibiting for {ShiftFailureInhibitMs / 1000:0}s.");
            Notify(
                $"{PluginId}.ShiftIgnored",
                "Sequential Auto Shift",
                _("Shift requests were ignored by the game. Check that the transmission is sequential or semi-automatic and that gear up/down inputs are bound. Shifting is inhibited for {0}s.",
                    ShiftFailureInhibitMs / 1000),
                NotificationLevel.Warning,
                10f);
            status = _("Shift ignored by the game - inhibited temporarily");
            return;
        }

        if (decision.Command != ShiftCommand.None)
        {
            Pulse(decision.Command, decision.Steps);
            int target = decision.Command == ShiftCommand.Up
                ? data.Gear + decision.Steps
                : data.Gear - decision.Steps;
            status = decision.Command == ShiftCommand.Up
                ? _("Upshift to {0} at {1} rpm", FormatGear(target), decision.Rpm)
                : _("Downshift to {0} at {1} rpm", FormatGear(target), decision.Rpm);
            return;
        }

        if (decision.IsRetry)
        {
            Logger.Warn($"[SequentialAutoShift] {decision.Command} shift from gear {data.Gear} had no effect, retrying.");
            status = _("Retrying shift");
            return;
        }

        if (decision.ConfirmedGear != 0)
        {
            status = _("Gear {0} confirmed", FormatGear(decision.ConfirmedGear));
            return;
        }

        status = Describe(decision, data, cfg);
    }

    private static string Describe(ShiftDecision decision, ShiftSnapshot data, SequentialAutoShiftSettings cfg)
    {
        return decision.Hold switch
        {
            ShiftHold.EngineOff => _("Engine off"),
            ShiftHold.NoForwardGears => _("No forward gears available"),
            ShiftHold.RpmUnavailable => _("RPM unavailable"),
            ShiftHold.ManualTransmission => _("Manual gearbox - shifting disabled"),
            ShiftHold.InhibitedAfterFailures => _("Inhibited after ignored shifts ({0}s)", Math.Ceiling(decision.Rpm)),
            ShiftHold.CoolingDown => _("Cooling down"),
            ShiftHold.ManualTakeover => _("Paused after manual input"),
            ShiftHold.ShiftInProgress => _("Shift in progress"),
            ShiftHold.BrakeHeld => _("Brake applied - upshift held"),
            ShiftHold.BelowMinimumSpeed => _("Below minimum upshift speed"),
            ShiftHold.EngineBraking => _("Holding gear for engine braking"),
            ShiftHold.TurnHeld => _("Upshift held in a turn"),
            ShiftHold.TopGear => _("Already in top gear {0}", FormatGear(data.Gear)),
            ShiftHold.BottomGear => _("Already in the lowest gear"),
            ShiftHold.LandingTooLow => _("Upshift held: {0} would lug at {1} rpm", FormatGear(data.Gear + 1), decision.Rpm),
            ShiftHold.LandingTooHigh => _("Downshift held: would overrev to {0} rpm", decision.Rpm),
            ShiftHold.PostDownshiftDwell => _("Holding after a downshift"),
            ShiftHold.Neutral => _("Neutral - waiting for throttle"),
            ShiftHold.Reverse => _("Reverse - shifting disabled"),
            ShiftHold.Stopped => _("Stopped - resetting the gear"),
            _ => _("Holding {0} @ {1} rpm", FormatGear(data.Gear), data.Rpm)
        };
    }

    private void NoticeManualGearbox(ShiftSnapshot data)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - lastManualGearboxNoticeUtc).TotalSeconds < 60)
            return;

        lastManualGearboxNoticeUtc = now;
        Notify(
            $"{PluginId}.ManualGearbox",
            "Sequential Auto Shift",
            _("The game reports a manual gearbox ({0}). Gear up/down pulses have no effect, so the plugin stays idle.", data.ShifterType),
            NotificationLevel.Warning,
            10f);
    }

    // MARK: Telemetry

    private void OnTelemetry(GameTelemetryData data)
    {
        ShiftSnapshot next = ShiftSnapshot.From(data);
        lock (sync)
        {
            snapshot = next;
        }
    }

    public override void OnDisable()
    {
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        settingsHandler?.UnregisterListener<SequentialAutoShiftSettings>(SettingsFileName, OnSettingsChanged);
        settingsHandler?.Dispose();
        settingsHandler = null;
        ReleaseShiftChannel();
        base.OnDisable();
    }

    public override void Shutdown()
    {
        OnDisable();
    }

    // MARK: Output

    private void Pulse(ShiftCommand command, int steps)
    {
        bool up = command == ShiftCommand.Up;

        // A sequential box queues presses, so a multi-gear request is the same pulse repeated.
        for (int i = 0; i < Math.Max(1, steps); i++)
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
    }

    /// <summary>
    ///  The output layer removes a channel when the event payload is null. Publishing an empty
    ///  <see cref="ControlVariables"/> never did that, because the object is never null, so the release
    ///  branch in <c>GameOutput.OnControlEvent</c> was unreachable.
    /// </summary>
    private void ReleaseShiftChannel()
    {
        Events.Current.Publish<ControlEvent?>(GameOutput.Current.EventString, null);
    }

    // MARK: Status

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

    public Dictionary<string, string> StatusRows
    {
        get
        {
            lock (sync)
            {
                ShiftSnapshot data = snapshot;
                SequentialAutoShiftSettings cfg = settings;
                ShiftInputs inputs = data.ToInputs(cfg);

                double maxRpm = ShiftScheduler.MaxEngineRpm(data.RpmMax);
                double resistance = cfg.AdaptiveShiftPoints ? runtime.Resistance : 0;
                bool braking = data.Brake > cfg.BrakeThreshold;
                bool engineBraking = cfg.HoldGearForEngineBrake && (data.MotorBrake || data.RetarderStep > 0);
                bool descending = runtime.Grade < -cfg.GradeFullSlope * 0.5;

                double upLine = ShiftScheduler.UpshiftLine(cfg, data.Throttle, maxRpm, resistance);
                double downLine = ShiftScheduler.DownshiftLine(cfg, data.Throttle, resistance, braking, engineBraking, descending);
                double lugFloor = ShiftScheduler.LugFloor(cfg, maxRpm, resistance);

                int nextGear = Math.Min(data.Gear + 1, data.GearCount);
                float landing = 0;
                bool projected = data.Gear > 0 && data.Gear < data.GearCount &&
                                 ShiftScheduler.TryProjectRpm(inputs, data.Gear, nextGear, data.Rpm, out landing);

                var rows = new Dictionary<string, string>
                {
                    { _("State"), status },
                    { _("Gear"), FormatGear(data.Gear) },
                    { _("RPM"), $"{data.Rpm:0} / {maxRpm:0}" },
                    { _("Speed"), $"{UnitConversions.FromScientificUnits(UnitType.Speed, data.Speed, ApplicationState.Current.DisplayUnits):0} {SpeedUnitLabel}" },
                    { _("Throttle"), $"{data.Throttle * 100:0}% (pedal {data.PedalThrottle * 100:0}%)" },
                    { _("Brake"), $"{data.Brake * 100:0}%" },
                    { _("Resistance"), $"{runtime.Resistance * 100:0}%" },
                    { _("Grade"), $"{runtime.Grade * 100:+0.0;-0.0;0.0}%" },
                    { _("Shift lines"), data.Gear > 0 ? $"{upLine:0} up / {downLine:0} down" : "-" },
                    { _("Lug floor"), $"{lugFloor:0}" },
                    { _("Engine"), data.EngineRunning ? _("On") : _("Off") },
                    { _("Transmission"), data.Transmission.ToString() },
                    { _("Start gear"), ShiftScheduler.EffectiveStartGear(cfg, inputs).ToString() },
                    { _("Shifts"), $"+{runtime.UpshiftCount} / -{runtime.DownshiftCount}" },
                    { _("Skipped gears"), runtime.SkippedShiftCount.ToString() },
                    { _("Vetoes"), runtime.VetoCount.ToString() },
                    { _("Ignored by game"), runtime.FailedShiftCount.ToString() },
                    { _("Manual shifts"), runtime.ManualShiftCount.ToString() },
                    { _("Verify window"), $"{runtime.VerifyTimeoutMs(900):0} ms" }
                };

                if (projected)
                    rows[_("Next gear landing")] = $"{FormatGear(nextGear)} @ {landing:0} rpm";

                if (!string.IsNullOrEmpty(data.ShifterType))
                    rows[_("Transmission report")] = data.ShifterType;

                return rows;
            }
        }
    }

    // MARK: Settings

    /// <summary>
    ///  Highest engine speed the schedule can ask for. The scheduler never requests an upshift above 97% of
    ///  the redline, so a slider that offered more would silently do nothing.
    /// </summary>
    public float UpshiftRpmSliderMax
    {
        get
        {
            ShiftSnapshot data;
            lock (sync)
            {
                data = snapshot;
            }

            double maxRpm = ShiftScheduler.MaxEngineRpm(data.RpmMax);
            return (float)Math.Floor(maxRpm * ShiftScheduler.RevLimitUpshiftFraction);
        }
    }

    public float UpshiftRpmSliderValue => SliderValue(settings.UpshiftRpm);
    public float UpshiftFullThrottleSliderValue => SliderValue(settings.UpshiftRpmFullThrottle);

    private float SliderValue(double rpm) => (float)Math.Min(rpm, UpshiftRpmSliderMax);

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
                case "resetGearWhenStopped":
                    settings.ResetGearWhenStopped = value is bool resetGear && resetGear;
                    break;
                case "adaptiveShiftPoints":
                    settings.AdaptiveShiftPoints = value is bool adaptive && adaptive;
                    break;
                case "allowSkipShift":
                    settings.AllowSkipShift = value is bool skip && skip;
                    break;
                case "adaptiveStartGear":
                    settings.AdaptiveStartGear = value is bool adaptiveStart && adaptiveStart;
                    break;
                case "avoidShiftingInTurns":
                    settings.AvoidShiftingInTurns = value is bool avoid && avoid;
                    break;
                case "useGradeSignal":
                    settings.UseGradeSignal = value is bool grade && grade;
                    break;
                case "holdGearForEngineBrake":
                    settings.HoldGearForEngineBrake = value is bool hold && hold;
                    break;
                case "startGear":
                    settings.StartGear = (int)Math.Round(ClampDouble(value, 1, 6, settings.StartGear));
                    break;
                case "maxSkipGears":
                    settings.MaxSkipGears = (int)Math.Round(ClampDouble(value, 1, 3, settings.MaxSkipGears));
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
                case "engineBrakeBandRpm":
                    settings.EngineBrakeBandRpm = ClampDouble(value, 900, 3000, settings.EngineBrakeBandRpm);
                    break;
                case "brakeDownshiftRpm":
                    settings.BrakeDownshiftRpm = ClampDouble(value, 500, 3000, settings.BrakeDownshiftRpm);
                    break;
                case "kickdownBoostRpm":
                    settings.KickdownBoostRpm = ClampDouble(value, 0, 800, settings.KickdownBoostRpm);
                    break;
                case "lugFloorRpm":
                    settings.LugFloorRpm = ClampDouble(value, 700, 2000, settings.LugFloorRpm);
                    break;
                case "shiftHysteresisRpm":
                    settings.ShiftHysteresisRpm = ClampDouble(value, 100, 800, settings.ShiftHysteresisRpm);
                    break;
                case "shiftCooldownMs":
                    settings.ShiftCooldownMs = ClampDouble(value, 250, 2000, settings.ShiftCooldownMs);
                    break;
                case "postDownshiftDwellMs":
                    settings.PostDownshiftDwellMs = ClampDouble(value, 0, 3000, settings.PostDownshiftDwellMs);
                    break;
                case "manualPauseMs":
                    settings.ManualPauseMs = ClampDouble(value, 0, 5000, settings.ManualPauseMs);
                    break;
                case "minimumSpeedKph":
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
                case "turnSteerThreshold":
                    settings.TurnSteerThreshold = ClampDouble(value, 0.05, 1, settings.TurnSteerThreshold);
                    break;
                case "turnMaxSpeedKph":
                    settings.TurnMaxSpeedKph = Math.Clamp(
                        KmhFromSpeed((float)ClampDouble(value, 0, 120, SpeedFromKmh(settings.TurnMaxSpeedKph))), 0, 90);
                    break;
                case "gradeFullSlope":
                    settings.GradeFullSlope = ClampDouble(value, 0.01, 0.2, settings.GradeFullSlope);
                    break;
                case "loadedMassKg":
                    settings.LoadedMassKg = ClampDouble(value, 1000, 60000, settings.LoadedMassKg);
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
        }
    }

    private void NormalizeSettings(bool saveIfChanged)
    {
        bool changed = false;

        // A migration only replaces a value the user never touched, so tuned numbers survive a plugin update.
        if (settings.SettingsVersion < 10)
        {
            if (settings.LugFloorRpm < settings.DownshiftRpm + settings.ShiftHysteresisRpm)
                settings.LugFloorRpm = settings.DownshiftRpm + settings.ShiftHysteresisRpm;
            changed = true;
        }

        settings.SettingsVersion = 10;

        double originalUpshift = settings.UpshiftRpm;
        double originalUpshiftFull = settings.UpshiftRpmFullThrottle;
        double originalDownshift = settings.DownshiftRpm;
        double originalKickdown = settings.KickdownRpm;
        double originalHysteresis = settings.ShiftHysteresisRpm;
        double originalLugFloor = settings.LugFloorRpm;
        int originalStartGear = settings.StartGear;

        settings.UpshiftRpm = Math.Clamp(settings.UpshiftRpm, 900, 3500);
        settings.UpshiftRpmFullThrottle = Math.Clamp(settings.UpshiftRpmFullThrottle, settings.UpshiftRpm, 3500);
        settings.DownshiftRpm = Math.Clamp(settings.DownshiftRpm, 500, settings.UpshiftRpm - 100);
        settings.KickdownRpm = Math.Clamp(settings.KickdownRpm, settings.DownshiftRpm, settings.UpshiftRpmFullThrottle);
        settings.BrakeDownshiftRpm = Math.Clamp(settings.BrakeDownshiftRpm, settings.DownshiftRpm, settings.UpshiftRpm);
        settings.EngineBrakeBandRpm = Math.Clamp(settings.EngineBrakeBandRpm, settings.DownshiftRpm, settings.UpshiftRpm);
        settings.LugFloorRpm = Math.Clamp(settings.LugFloorRpm, 700, 2000);
        settings.ShiftHysteresisRpm = Math.Clamp(settings.ShiftHysteresisRpm, 100, 800);
        settings.ShiftCooldownMs = Math.Clamp(settings.ShiftCooldownMs, 250, 2000);
        settings.PostDownshiftDwellMs = Math.Clamp(settings.PostDownshiftDwellMs, 0, 3000);
        settings.ManualPauseMs = Math.Clamp(settings.ManualPauseMs, 0, 5000);
        settings.MinimumSpeedKph = Math.Clamp(settings.MinimumSpeedKph, 0, 25);
        settings.StoppedSpeedKph = Math.Clamp(settings.StoppedSpeedKph, 0, 5);
        settings.StopDwellMs = Math.Clamp(settings.StopDwellMs, 0, 5000);
        settings.StartGear = Math.Clamp(settings.StartGear, 1, 6);
        settings.MaxSkipGears = Math.Clamp(settings.MaxSkipGears, 1, 3);
        settings.BrakeThreshold = Math.Clamp(settings.BrakeThreshold, 0.01, 1);
        settings.ClutchThreshold = Math.Clamp(settings.ClutchThreshold, 0.01, 1);
        settings.TurnSteerThreshold = Math.Clamp(settings.TurnSteerThreshold, 0.05, 1);
        settings.TurnMaxSpeedKph = Math.Clamp(settings.TurnMaxSpeedKph, 0, 90);
        settings.GradeFullSlope = Math.Clamp(settings.GradeFullSlope, 0.01, 0.2);
        settings.LoadedMassKg = Math.Clamp(settings.LoadedMassKg, 1000, 60000);

        changed |= Math.Abs(originalUpshift - settings.UpshiftRpm) > 0.01;
        changed |= Math.Abs(originalUpshiftFull - settings.UpshiftRpmFullThrottle) > 0.01;
        changed |= Math.Abs(originalDownshift - settings.DownshiftRpm) > 0.01;
        changed |= Math.Abs(originalKickdown - settings.KickdownRpm) > 0.01;
        changed |= Math.Abs(originalHysteresis - settings.ShiftHysteresisRpm) > 0.01;
        changed |= Math.Abs(originalLugFloor - settings.LugFloorRpm) > 0.01;
        changed |= originalStartGear != settings.StartGear;

        if (saveIfChanged && changed)
            settingsHandler?.Save(SettingsFileName, settings);
    }

    // MARK: Notifications

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

    // MARK: Display units

    public static string SpeedUnitLabel =>
        UnitConversions.GetUnitAbbreviation(UnitType.Speed, ApplicationState.Current.DisplayUnits);

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

    public static string FormatGear(int gear)
    {
        if (gear > 0)
            return gear.ToString();
        if (gear < 0)
            return $"R{Math.Abs(gear)}";
        return "N";
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
