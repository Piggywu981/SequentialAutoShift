# Sequential Auto Shift

Third-party ETS2LA plugin that shifts a sequential gearbox with a throttle-interpolated shift
schedule, checks every shift against the truck's gear ratios before sending anything, and verifies
that the gear actually changed.

The shift law itself lives in `ShiftScheduler.cs` and reads only plain values, so it runs both in game
and under the offline simulator in `scratch/shiftsim` (957 assertions over 12-, 16-speed and
wide-ratio boxes).

## How it decides

**The schedule.** Two lines are interpolated across throttle travel: `Upshift RPM (no throttle)` ..
`Upshift RPM (full throttle)` and `Downshift RPM (no throttle)` .. `Kickdown RPM (full throttle)`.
Separate lines apply while braking (`Braking downshift RPM`) and while engine braking
(`Engine braking band`).

**Landing, not a single RPM.** Every shift is projected through `gearRatiosForward`, so each gear pair
gets its own shift point:
- an upshift is refused when the next gear would land under the `Lug floor`;
- a downshift is refused when the next gear would land above 94% of the redline;
- the lug floor itself is capped below the upshift line, so first gear can always leave.

This is the whole reason a 12-speed shifts at ~1700 rpm in first and ~1500 in eleventh: the ratio step,
not a fixed number, decides. `Lug floor` is therefore a first-class setting: it, not the upshift slider,
sets the shift point of every low gear. The status panel shows the line actually in effect.

**A shift is not instantaneous.** The request goes out slightly early so the engine stays under the
redline during the change, and the amount of lead follows the gearbox's measured shift latency. A
settling window after each confirmed shift stops the new gear from being abandoned on the next tick.

**No hunting.** Braking removes upshifts but keeps downshifts, so the engine keeps braking. A downshift
after an upshift is blocked for `Settling time after a shift`, which is what breaks 1-2-1 ping-pong
even when the two throttle corners are set to the same rpm.

**Following a driver, not just a table:**
- engine brake, retarder or a long descent pull the box down into the braking band (1700 rpm), not to
  idle, because the brake only works near rated speed;
- a throttle *stab* adds `Kickdown boost` on top of the kickdown corner, so a pull request is answered
  when the driver asks, not when the rpm happens to be low;
- an empty truck skips gears (`2-4-6`) when the ratios allow it and pulls away in a higher gear;
- a loaded truck never skips and starts low;
- the upshift is held while the steering wheel is turned at low speed;
- the road gradient is read from the truck pitch, with the sign learned on the first climb.

## Closed loop

After a gear-up/gear-down pulse the plugin waits for the gear to change. The wait adapts to the
gearbox's measured latency. A shift the game is still executing (its own clutch is applied) is never
followed by a second pulse, so a slow gearbox is not double shifted. After three genuinely ignored
requests the plugin stops shifting for 20 s and says so.

A gear change the plugin did not request, or **clutch pedal** travel above the threshold, is treated
as the driver taking over and pauses automatic shifting. The clutch the game applies for its own
shifts is *not* a takeover signal - reading it would pause the plugin after every shift it makes.

## Transmission gating

Only `sequential` and `semi-automatic` boxes respond to `gearup`/`geardown`. When telemetry reports
`manual` or `hshifter` the plugin stays idle and says so once, instead of firing pulses the game
ignores and reporting a failure every 20 s.

## Neutral and stopped

A neutral truck with the driver asking to move selects the start gear (crawl speed only). When the
truck has been stopped for the dwell time the gearbox is walked down to the start gear in bursts of up
to three gears per request, so twelfth to first takes a couple of seconds instead of ten.

## Notes

- The plugin does not write a target gear; it simulates sequential shift button presses through
  ETS2LA's public output API.
- Telemetry is copied into an immutable snapshot on the event, so the 60 Hz in-place rewrite in the
  host cannot mix rpm and speed from two frames.
- Settings live in `%APPDATA%/ETS2LA/SequentialAutoShift.json`. Version migrations only replace values
  that still match the default of the previous version, so tuned numbers survive updates.
- Engine starting on behalf of the assists used to live here. It was removed: it is not part of
  choosing a gear, and pulsing the ignition from a shift controller made a shift failure look like an
  engine failure.
- The status panel shows both lines, the lug floor, projected landing rpm for the next gear, the
  measured verify window, and shift / skip / veto / failure counters - enough to calibrate from one
  drive.
