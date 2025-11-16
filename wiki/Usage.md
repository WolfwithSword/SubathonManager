# Usage

## Home

### Subathon Controls


<details>
<summary>Subathon & Time Management</summary>
In this section, you can create a new subathon with an initial time set.

In addition to viewing what the current timer value is, you can also toggle **pausing** and **locking** the subathon.

There is a buton to quickly force refresh all browser overlays.

Finally, there is a section for quick adding, removing, or setting of time. Format for time is the same as commands, such as `#d#h#m#s`

</details>

<details>
<summary>Multiplier Control</summary>
Here you can initiate a multiplier for either time, points, or both. 
The duration is optional - if not set, multiplier only ends 
on app restart or force end. If set, multiplier values will 
reset to 1x when it counts down to 0.

It will preview the current multiplier values as well as a current duration if enabled and active. You can also stop a multiplier here at any time.

Multiplier values can be any number, ex: `2`x, `2.5`x, any positive value. The only limitation is both time and points share the same multiplier.

</details>


<details>
<summary>Points Management & Upcoming Goals</summary>
In this section, you can preview your current points in the subathon, 
as well as quick add, subtract, or set the points.

Under here, you can preview your upcoming goals list with their points value, as well as see your most recently completed goal.

</details>


### Recent Event List

<details>
<summary>Recent Events</summary>
This list contains a subset of recent events processed by your active subathon!

For each event, you will see the event type, source, time, user who triggered it, and its value(s).

Additionally, you will see whether or not it was successfully processed.

For each event, you are able to delete it from this list, which will *undo* time/points associated with it if applicable.

If an event is not processed, you are able to try reprocessing it. 
</details>


## Overlays

To use an overlay in OBS, please create a browser source of the correct height and width, then paste in the link for your overlay that you can copy from here.

<details>
<summary>Overlays Page</summary>
On this page, you will see on the left, a list of all your current overlays.

From here, you can create a new overlay, or select one from the list where you can then copy the url, edit it (via button or double click), duplicate it, or permanently delete it.

On the right, you will see a button which will let you quickly refresh all overlays that are open.
</details>

### Editor

<details>
<summary>Overlay Settings</summary>
You can rename an overlay here, as well as set its Width and Height. 

It is important to match this width and height when you create your OBS browser source for the best results.

You can copy the link, open it in a browser, or save your settings here.

Saving the overlay will cause it to refresh in all places.
</details>

<details>
<summary>Widget Control</summary>
A list of widgets will be shown here that are active in the overlay.

You can also import new widgets as html files from your system with the **Import Widget** button.

Their order (also indicated by the Z value) dictacts overlapping rules. Widgets higher up / with a bigger number will appear above others.

Options are available to **toggle visibility**, which keeps the widget in the overlay but not visible when in OBS.
You can adjust the overlap position with the arrows, open the widget in the widget editor (or via double click), duplicate the widget, or delete it from the overlay.

When you double click or click the edit button on a widget in this list, it will populate it in the *Widget Editor* for customization.

</details>


<details>
<summary>Built-In Editor</summary>

This editor preview allows you to view what the widgets in the overlay will currently look like.

You can drag and move around the widgets, as well as click them to select them to populate in the *widget editor* on the right.

You can hover over a widget t oview it's name and Z value, and widgets with visibility toggled off will be faded.

</details>


<details>
<summary>Widget Editor/Settings</summary>
When a widget is selected either via the list or the preview UI, you can do various actions.

You can rename the widget, set its width and height, and change it's X and Y position (will also update when you drag them around).

A list of detected CSS variables will also be displayed in, which you can customize to *override* defaults found as CSS Vars in your linked CSS variables from within the widget's local referenced css files.

To load and preview your customizations, you will need to click **Save**. To reload CSS variable detection from your raw files, click the *Reload CSS Vars* button.

Saving any widget will cause the overlay to refresh in all places.
</details>

## Goals

<details>
<summary>Goals List</summary>
Goals are tracked separately from your subathon, so that once you set them up, they will persist here unless edited or a new list is made.

You can add new goals with the **+** button, where for each goal you can set the Text and the number of Points required to achieve. Each goal can also be deleted, and the list will always be sorted from lowest to highest points.

To save changes, click the Save button in the Editor pane.
</details>

<details>
<summary>Goals Editor</summary>
In the goals editor, you can set a new name for your current goals list. This name is only for logging and self-organization purposes, as only one set of goals can be active at any given time.

You can preview your live current number of points on this page.

Clicking the **Create New List** button will create a whole new empty goals list.

To save changes to your list, click Save Changes. 
</details>


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
