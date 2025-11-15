# Usage

TODO

## Home

### Subathon & Timer

### Multiplier

### Points

### Upcoming Goals

### Event List

## Overlays

### Overlays Page

### Overlay Editor

## Goals

## Settings

See [Configuration](Configuration.md)

## Chat Commands

See [Configuration](Configuration.md) for the command names.

<details>
<summary>Points Commands</summary>
<ul>
  <li><b>AddPoints</b>: Add a number of points. Ex <code>!addpts 10</code></li>
  <li><b>SubtractPoints</b>: Remove a number of points. Ex <code>!subtractpts 10</code></li>
  <li><b>SetPoints</b>: Set the points to a specific number. Ex <code>!setpts 140</code></li>
</ul>
</details>

<details>
<summary>Time Commands</summary>
Format for the values: <code>##d##h##m##s</code>, ex 5 minutes can be both 300s or 5m 
<ul>
  <li><b>AddTime</b>: Add time. Ex <code>!addtime 1h30m</code></li>
  <li><b>SubtractTime</b>: Remove time. Ex <code>!subtracttime 5m</code></li>
  <li><b>SetTime</b>: Set time. Ex <code>!settime 8h35m5s</code></li>
</ul>
</details>

<details>
<summary>Multiplier Commands</summary>

The multiplier can apply to either time, points, or both. It can also be supplied an optional duration, after which, it will reset to x1 x1. Multipliers are not preserved between app restarts.

Format for SetMultiplier: <code>#xt</code> multiplier for just Time, <code>#xp</code> multiplier for just Points, <code>#xpt or #xtp</code> for both, <code>##d##h##m##s</code> for optional duration.
<ul>
  <li><b>SetMultiplier</b>: Set the multiplier to the current options. Overwrites any current multiplier. Ex <code>!setmultiplier 2xtp 2h</code> <code>!setmultiplier 2xt 30m</code> <code>!setmultiplier 2.5xpt </code></li>
  <li><b>StopMultiplier</b>: Stop the multiplier entirely, resetting to 1x for both time and points</li>
</ul>
</details>

<details>
<summary>Other Commands</summary>
<ul>
  <li><b>Lock</b>: Lock the subathon, preventing new events from contributing</li>
  <li><b>Unlock</b>: Unlock the subathon so all events can be added</li>
  <li><b>Pause</b>: Pause the timer from counting down</li>
  <li><b>Resume</b>: Resume counting down the timer</li>
  <li><b>Refresh Overlays</b>: Refresh *all* active browser overlays</li>
</ul>
</details>
