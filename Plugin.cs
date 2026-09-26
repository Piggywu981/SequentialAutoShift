using System;
using System.Collections.Generic;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.Telemetry;
using ETS2LA.Notifications;
using ETS2LA.Settings;
using ETS2LA.Shared;
using ETS2LA.State;

namespace SequentialAutoShift;

[Serializable]
public sealed class SequentialAutoShiftSettings
{
    public int SettingsVersion { get; set; } = 8;
    public bool Enabled { get; set; } = true;
    public bool DownshiftEnabled { get; set; } = true;
    public bool StartEngineWhenAccActive { get; set; } = true;
    public bool ResetGearWhenStopped { get; set; } = true;
    public int StartGear { get; set; } = 1;
    public double UpshiftRpm { get; set; } = 1800;
    public double DownshiftRpm { get; set; } = 1000;
    public double KickdownRpm { get; set; } = 1000;
    public double ShiftCooldownMs { get; set; } = 650;
    public double EngineStartRetryMs { get; set; } = 2500;
    public double ManualPauseMs { get; set; } = 1500;
    public double MinimumSpeedKph { get; set; } = 3;
    public double StoppedSpeedKph { get; set; } = 5;
    public double BrakeThreshold { get; set; } = 0.08;
    public double ClutchThreshold { get; set; } = 0.08;
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

    private SettingsHandler? settingsHandler;
    private SequentialAutoShiftSettings settings = new();
    private GameTelemetryData telemetry = new();
    private DateTime nextShiftAllowedUtc = DateTime.MinValue;
    private DateTime nextEngineStartAllowedUtc = DateTime.MinValue;
    private DateTime pendingEngineStartUtc = DateTime.MinValue;
    private DateTime manualPauseUntilUtc = DateTime.MinValue;
    private int lastGear;
    private string status = "Waiting for telemetry";

    public override PluginInformation Info { get; } = new()
    {
        Id = PluginId,
        Name = "Sequential Auto Shift",
        Description = "Automatically shifts a sequential gearbox using telemetry RPM and ETS2LA gear input pulses.",
        Version = "0.2.0",
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
        telemetry = GameTelemetry.Current.GetCurrentData();
        lastGear = telemetry.truckInt.gear;

        base.OnEnable();
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = $"{PluginId}.Enabled",
            Title = "Sequential Auto Shift",
            Content = "Automatic sequential shifting is ready.",
            Level = NotificationLevel.Information,
            CloseAfter = 4
        });
    }

    public override void Tick()
    {
        if (!settings.Enabled)
        {
            status = "Disabled";
            return;
        }

        GameTelemetryData data;
        lock (sync)
        {
            data = telemetry;
        }

        if (TryStartEngineForAcc(data))
            return;

        DateTime now = DateTime.UtcNow;
        if (TryResetGearWhenStopped(data, now))
            return;

        if (!CanShift(data, out string reason))
        {
            status = reason;
            return;
        }

        if (now < manualPauseUntilUtc)
        {
            status = "Paused after manual input";
            return;
        }

        if (now < nextShiftAllowedUtc)
        {
            status = "Cooling down";
            return;
        }

        int gear = data.truckInt.gear;
        float rpm = data.truckFloat.engineRpm;
        float throttle = Math.Clamp(data.truckFloat.userThrottle, 0f, 1f);

        if (ShouldUpshift(data, gear, rpm))
        {
            PulseShift(up: true);
            MarkShift(now, gear);
            status = $"Upshift requested at {rpm:0} rpm";
            return;
        }

        if (settings.DownshiftEnabled && ShouldDownshift(data, gear, rpm, throttle))
        {
            PulseShift(up: false);
            MarkShift(now, gear);
            status = $"Downshift requested at {rpm:0} rpm";
            return;
        }

        status = $"Holding gear {FormatGear(gear)} at {rpm:0} rpm";
    }

    public override void OnDisable()
    {
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        settingsHandler?.UnregisterListener<SequentialAutoShiftSettings>(SettingsFileName, OnSettingsChanged);
        settingsHandler?.Dispose();
        settingsHandler = null;
        ClearShiftChannel();
        ClearEngineStartChannel();
        base.OnDisable();
    }

    public override void Shutdown()
    {
        OnDisable();
    }

    public SequentialAutoShiftSettings Settings => settings;
    public string Status => status;
    public int LastGearSeen => lastGear;

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

    public void HandleAction(string actionId, object? value)
    {
        switch (actionId)
        {
            case "enabled":
                settings.Enabled = value is bool enabled && enabled;
                break;
            case "downshiftEnabled":
                settings.DownshiftEnabled = value is bool downshift && downshift;
                break;
            case "startEngineWhenAccActive":
                settings.StartEngineWhenAccActive = value is bool startEngine && startEngine;
                break;
            case "resetGearWhenStopped":
                settings.ResetGearWhenStopped = value is bool resetGear && resetGear;
                break;
            case "startGear":
                settings.StartGear = (int)Math.Round(ClampDouble(value, 1, 6, settings.StartGear));
                break;
            case "upshiftRpm":
                settings.UpshiftRpm = ClampDouble(value, 900, 3500, settings.UpshiftRpm);
                break;
            case "downshiftRpm":
                settings.DownshiftRpm = ClampDouble(value, 500, 3000, settings.DownshiftRpm);
                break;
            case "kickdownRpm":
                settings.KickdownRpm = ClampDouble(value, 500, 3500, settings.KickdownRpm);
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
                settings.MinimumSpeedKph = ClampDouble(value, 0, 25, settings.MinimumSpeedKph);
                break;
            case "stoppedSpeedKph":
                settings.StoppedSpeedKph = ClampDouble(value, 0, 5, settings.StoppedSpeedKph);
                break;
        }

        NormalizeSettings(saveIfChanged: false);
        settingsHandler?.Save(SettingsFileName, settings);
    }

    private void OnTelemetry(GameTelemetryData data)
    {
        lock (sync)
        {
            telemetry = data;
        }

        if (Math.Abs(data.truckFloat.userBrake) > settings.BrakeThreshold ||
            Math.Abs(data.truckFloat.userClutch) > settings.ClutchThreshold)
        {
            manualPauseUntilUtc = DateTime.UtcNow.AddMilliseconds(settings.ManualPauseMs);
        }
    }

    private bool CanShift(GameTelemetryData data, out string reason)
    {
        reason = "";

        if (!data.sdkActive)
        {
            reason = "SDK inactive";
            return false;
        }

        if (data.paused)
        {
            reason = "Game paused";
            return false;
        }

        if (!data.truckBool.engineEnabled)
        {
            reason = "Engine off";
            return false;
        }

        if (data.configUI.gears <= 1)
        {
            reason = "No forward gears available";
            return false;
        }

        if (data.truckInt.gear <= 0)
        {
            reason = "Not in a forward gear";
            return false;
        }

        if (Math.Abs(data.truckFloat.speed) < ToMetersPerSecond(settings.MinimumSpeedKph))
        {
            reason = "Below minimum speed";
            return false;
        }

        if (Math.Abs(data.truckFloat.userBrake) > settings.BrakeThreshold)
        {
            reason = "Brake input detected";
            return false;
        }

        if (Math.Abs(data.truckFloat.userClutch) > settings.ClutchThreshold)
        {
            reason = "Clutch input detected";
            return false;
        }

        if (data.truckFloat.engineRpm <= 0)
        {
            reason = "RPM unavailable";
            return false;
        }

        return true;
    }

    private bool TryResetGearWhenStopped(GameTelemetryData data, DateTime now)
    {
        if (!settings.ResetGearWhenStopped)
            return false;

        if (!data.sdkActive || data.paused || !data.truckBool.engineEnabled)
            return false;

        int targetGear = Math.Clamp(settings.StartGear, 1, Math.Max(1, data.configUI.gears));
        int gear = data.truckInt.gear;
        if (gear <= targetGear)
            return false;

        if (Math.Abs(data.truckFloat.speed) > ToMetersPerSecond(settings.StoppedSpeedKph))
            return false;

        if (now < nextShiftAllowedUtc)
        {
            status = $"Resetting to start gear {targetGear}: cooling down";
            return true;
        }

        PulseShift(up: false);
        MarkShift(now, gear);
        status = $"Stopped in gear {gear}, downshifting toward start gear {targetGear}";
        return true;
    }

    private bool TryStartEngineForAcc(GameTelemetryData data)
    {
        if (!settings.StartEngineWhenAccActive)
            return false;

        if (!data.sdkActive || data.paused)
            return false;

        if (!IsAccActive())
            return false;

        if (data.truckBool.engineEnabled)
        {
            pendingEngineStartUtc = DateTime.MinValue;
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (pendingEngineStartUtc > DateTime.MinValue &&
            now >= pendingEngineStartUtc &&
            now >= nextEngineStartAllowedUtc)
        {
            PulseEngineStart(ignitionOn: false, startEngine: true);
            pendingEngineStartUtc = DateTime.MinValue;
            nextEngineStartAllowedUtc = now.AddMilliseconds(settings.EngineStartRetryMs);
            status = "ACC active, engine start requested";
            return true;
        }

        if (now < nextEngineStartAllowedUtc)
            return false;

        if (data.truckBool.electricEnabled)
        {
            PulseEngineStart(ignitionOn: false, startEngine: true);
            nextEngineStartAllowedUtc = now.AddMilliseconds(settings.EngineStartRetryMs);
            status = "ACC active, engine start requested";
            return true;
        }

        PulseEngineStart(ignitionOn: true, startEngine: false);
        pendingEngineStartUtc = now.AddMilliseconds(700);
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

    private bool ShouldUpshift(GameTelemetryData data, int gear, float rpm)
    {
        if (gear >= data.configUI.gears)
            return false;

        if (WouldUpshiftLug(data, gear, rpm))
            return false;

        return rpm > settings.UpshiftRpm;
    }

    private bool ShouldDownshift(GameTelemetryData data, int gear, float rpm, float throttle)
    {
        if (gear <= 1)
            return false;

        if (WouldDownshiftOverrev(data, gear, rpm))
            return false;

        if (rpm < settings.DownshiftRpm)
            return true;

        if (throttle > 0.85f && rpm < settings.KickdownRpm)
            return true;

        return false;
    }

    private bool WouldUpshiftLug(GameTelemetryData data, int gear, float rpm)
    {
        if (data.truckFloat.userThrottle < 0.65f)
            return false;

        if (!TryProjectShiftRpm(data, gear, gear + 1, rpm, out float projectedRpm))
            return false;

        return projectedRpm < settings.DownshiftRpm + 150;
    }

    private bool WouldDownshiftOverrev(GameTelemetryData data, int gear, float rpm)
    {
        if (!TryProjectShiftRpm(data, gear, gear - 1, rpm, out float projectedRpm))
            return false;

        float maxRpm = data.configFloat.engineRpmMax > 0 ? data.configFloat.engineRpmMax : (float)settings.UpshiftRpm + 500;
        return projectedRpm > maxRpm * 0.94f;
    }

    private static bool TryProjectShiftRpm(GameTelemetryData data, int currentGear, int targetGear, float currentRpm, out float projectedRpm)
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

    private void ClearShiftChannel()
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = shiftChannel,
            Properties = new ControlProperties(),
            Variables = new ControlVariables
            {
                gearup = false,
                geardown = false
            }
        });
    }

    private void ClearEngineStartChannel()
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

    private void MarkShift(DateTime now, int gear)
    {
        lastGear = gear;
        nextShiftAllowedUtc = now.AddMilliseconds(settings.ShiftCooldownMs);
    }

    private void OnSettingsChanged(SequentialAutoShiftSettings newSettings)
    {
        settings = newSettings ?? new SequentialAutoShiftSettings();
        NormalizeSettings(saveIfChanged: false);
    }

    private void NormalizeSettings(bool saveIfChanged)
    {
        bool changed = false;

        if (settings.SettingsVersion < 1)
        {
            settings.SettingsVersion = 1;
            changed = true;
        }

        if (settings.SettingsVersion < 2)
        {
            settings.SettingsVersion = 2;
            settings.DownshiftRpm = 600;
            settings.KickdownRpm = 600;
            settings.UpshiftRpm = 2000;
            changed = true;
        }

        if (settings.SettingsVersion < 3)
        {
            settings.SettingsVersion = 3;
            settings.DownshiftRpm = 800;
            settings.KickdownRpm = 800;
            settings.UpshiftRpm = 2000;
            changed = true;
        }

        if (settings.SettingsVersion < 4)
        {
            settings.SettingsVersion = 4;
            settings.DownshiftRpm = 1000;
            settings.KickdownRpm = 1000;
            settings.UpshiftRpm = 2000;
            changed = true;
        }

        if (settings.SettingsVersion < 5)
        {
            settings.SettingsVersion = 5;
            settings.DownshiftRpm = 1000;
            settings.KickdownRpm = 1000;
            settings.UpshiftRpm = 1800;
            changed = true;
        }

        if (settings.SettingsVersion < 6)
        {
            settings.SettingsVersion = 6;
            settings.StartEngineWhenAccActive = true;
            settings.EngineStartRetryMs = 2500;
            changed = true;
        }

        if (settings.SettingsVersion < 7)
        {
            settings.SettingsVersion = 7;
            settings.ResetGearWhenStopped = true;
            settings.StartGear = 2;
            settings.StoppedSpeedKph = 0.5;
            changed = true;
        }

        if (settings.SettingsVersion < 8)
        {
            settings.SettingsVersion = 8;
            settings.ResetGearWhenStopped = true;
            settings.StartGear = 1;
            settings.StoppedSpeedKph = 5;
            changed = true;
        }

        double originalDownshift = settings.DownshiftRpm;
        double originalKickdown = settings.KickdownRpm;
        double originalUpshift = settings.UpshiftRpm;
        double originalEngineStartRetry = settings.EngineStartRetryMs;
        int originalStartGear = settings.StartGear;
        double originalStoppedSpeed = settings.StoppedSpeedKph;
        settings.UpshiftRpm = Math.Clamp(settings.UpshiftRpm, 900, 3500);
        settings.DownshiftRpm = Math.Clamp(settings.DownshiftRpm, 500, settings.UpshiftRpm - 100);
        settings.KickdownRpm = Math.Clamp(settings.KickdownRpm, settings.DownshiftRpm, settings.UpshiftRpm - 50);
        settings.ShiftCooldownMs = Math.Clamp(settings.ShiftCooldownMs, 250, 2000);
        settings.EngineStartRetryMs = Math.Clamp(settings.EngineStartRetryMs, 1000, 10000);
        settings.ManualPauseMs = Math.Clamp(settings.ManualPauseMs, 0, 5000);
        settings.MinimumSpeedKph = Math.Clamp(settings.MinimumSpeedKph, 0, 25);
        settings.StoppedSpeedKph = Math.Clamp(settings.StoppedSpeedKph, 0, 5);
        settings.StartGear = Math.Clamp(settings.StartGear, 1, 6);
        settings.BrakeThreshold = Math.Clamp(settings.BrakeThreshold, 0.01, 1);
        settings.ClutchThreshold = Math.Clamp(settings.ClutchThreshold, 0.01, 1);

        changed |= Math.Abs(originalDownshift - settings.DownshiftRpm) > 0.01;
        changed |= Math.Abs(originalKickdown - settings.KickdownRpm) > 0.01;
        changed |= Math.Abs(originalUpshift - settings.UpshiftRpm) > 0.01;
        changed |= Math.Abs(originalEngineStartRetry - settings.EngineStartRetryMs) > 0.01;
        changed |= originalStartGear != settings.StartGear;
        changed |= Math.Abs(originalStoppedSpeed - settings.StoppedSpeedKph) > 0.01;

        if (saveIfChanged && changed)
            settingsHandler?.Save(SettingsFileName, settings);
    }

    public Dictionary<string, string> StatusRows
    {
        get
        {
            GameTelemetryData data = LatestTelemetry;
            string maxRpm = data.configFloat.engineRpmMax > 0 ? $"{data.configFloat.engineRpmMax:0}" : "unknown";
            return new Dictionary<string, string>
            {
                { "State", status },
                { "Gear", FormatGear(data.truckInt.gear) },
                { "RPM", $"{data.truckFloat.engineRpm:0} / {maxRpm}" },
                { "Speed", $"{UnitConversions.FromScientificUnits(UnitType.Speed, data.truckFloat.speed, Units.Metric):0} km/h" },
                { "ACC active", IsAccActive() ? "Yes" : "No" },
                { "Engine", data.truckBool.engineEnabled ? "On" : "Off" },
                { "Start gear", settings.StartGear.ToString() },
                { "Last gear seen", FormatGear(lastGear) }
            };
        }
    }

    public float UpshiftRpmSliderMax
    {
        get
        {
            GameTelemetryData data = LatestTelemetry;
            if (data.configFloat.engineRpmMax > 1800)
                return (float)Math.Clamp(Math.Ceiling(data.configFloat.engineRpmMax / 100.0) * 100, 2200, 3500);

            return 2500;
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
