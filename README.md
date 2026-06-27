# Sequential Auto Shift

Third-party ETS2LA plugin that performs conservative sequential gearbox auto shifting by reading telemetry and sending `gearup` / `geardown` control pulses.

## Notes

- ETS2/ATS must be configured so `gearup` and `geardown` control inputs work for the selected transmission mode.
- This plugin does not write a target gear directly. It simulates sequential shift button presses through ETS2LA's public output API.
- Defaults are intentionally conservative. Tune the RPM thresholds in the plugin settings after testing with your truck.
