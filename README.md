# Sequential Auto Shift

Third-party ETS2LA plugin that shifts a sequential gearbox with a throttle-interpolated shift
schedule, checks the shift against the truck's gear ratios before sending anything, and verifies
that the gear actually changed.

## How it decides

- **Shift lines are interpolated across throttle travel**, not a single fixed engine speed:
  `Upshift RPM (no throttle)` .. `Upshift RPM (full throttle)` and
  `Downshift RPM (no throttle)` .. `Kickdown RPM (full throttle)`. The upshift line is capped at
  97% of the truck's redline, so it can never demand a shift the engine would not reach.
- **Every upshift is projected through the gear ratios.** If the engine would land below
  `Downshift RPM + Landing margin`, the shift is skipped and the gear is held. Because the ratio
  span differs per gear, this produces per-gear shift points (short low gears are held longer,
  tall top gears shift early) and it removes 1-2-1 hunting at any throttle position.
- **Every downshift is projected too**, and skipped if the landing rpm would come out above 94%
  of redline.
- An upshift is still forced at 97% of redline, so a gear can never be held into the limiter.
- **Braking suppresses upshifts but not downshifts**, so the truck keeps walking down through the
  gears for engine braking; the retarder or engine brake being on also holds the gear.
- Downshifting into 1st is only allowed at a crawl, since 1st is a launch gear.
- **Load and gradient** are estimated from "throttle demanded but the truck is not accelerating"
  plus truck mass, and raise both lines when the truck is working hard (`Adapt to load and gradient`).
- **Inputs use the throttle and brake the game actually applied** (`gameThrottle` / `gameBrake`
  merged with the pedal values), so cruise control, ACC and other plugins are seen correctly.
- **Shifts are verified.** After a gear-up/gear-down pulse the plugin waits for the gear to change;
  it retries, and after three ignored requests it stops shifting for 20 s and raises a notification
  instead of pushing the truck through the gates.
- A gear change the plugin did not request, or clutch travel above the threshold, is treated as the
  driver taking over and pauses automatic shifting.
- Minimum time in gear counts from the shift that actually completed.

## Notes

- ETS2/ATS must be configured so `gearup` and `geardown` control inputs work for the selected
  transmission mode. The plugin does not write a target gear; it simulates sequential shift button
  presses through ETS2LA's public output API. If the game ignores the pulses, the plugin notices and
  says so rather than repeating them forever.
- The status panel shows both shift lines, the projected landing rpm for the next gear, resistance,
  shift/veto/failure counters and the transmission mode reported by telemetry - enough to calibrate
  the schedule from one drive.
- Defaults are starting points for a typical stock truck. Tune `Landing margin` and the four RPM
  lines after testing with your truck.
- Settings live in `%APPDATA%/ETS2LA/SequentialAutoShift.json`. Version migrations only replace
  values that still match the default of the previous version, so tuned numbers survive updates.
