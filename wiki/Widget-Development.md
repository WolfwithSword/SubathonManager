# Widget Development

You can develop your own widgets by either modifying the existing [presets](https://github.com/WolfwithSword/SubathonManager/tree/main/presets)
or by developing your own, with a few considerations.

For developing your own, you will need to implement certain function names to accept data that is pushed to each widget.
You don't need to worry for the internal websocket connection, we take care of that.

Each widget is treated as an isolated iframe within an overlay, and will automatically connect to the localhost websocket.

## Structure

You can develop your widgets any way you wish. When imported into SubathonManager,
only a single `.html` file is imported. Any relative imports to media files, `.css` or `.js` files will resolve.

For the required functions, and anything important script-wise, it is recommended to
embed it in `<script>` tags in the html file, such that they are loaded properly.

The HTML files do not need to be full document-valid files, but still valid HTML.

We currently do not have a way to inject variable overrides for scripts and that sort of customization, but it is in the plans.

You can import any scripts, css, fonts, etc you want. But only local CSS files will be scanned for variables.

### CSS

It is recommended in your CSS files, you specify some variables in a `:root` tag.

Any and all CSS variables will be able to be configured and overwritten via the UI
and injected when in use without modifying the raw files.

ex. 
```css
:root {
    --main-background-color: #cecece;
    --text-size: 16px;
}
```

## Functions & Data

The following functions are required only if you want their associated data in your widget:

- [handleSubathonUpdate(data)](#handlesubathonupdate)
  - data.type: 'subathon_timer'
- [handleSubathonEvent(data)](#handlesubathonevent)
  - data.type: 'event'
- [handleGoalsUpdate(data)](#handlegoalsupdate)
  - data.type: 'goals_list'
- [handleGoalCompleted(data)](#handlegoalcompleted)
  - data.type: 'goal_completed'
- [handleSubathonDisconnect](#handlesubathondisconnect)

### handleSubathonUpdate
This function will be updated very frequently.

From it, you will get the current seconds and points as they change, 
lock/unlock status, pause/resume status, and multiplier values.

*When multiplier for seconds or points is 1, then there is no multiplier running.

```json
{
  "type": "subathon_timer",
  "total_seconds":  int, //full seconds to make up whole timer
  "days":  int, // number of days left in timer
  "hours":  int, // remainder hours left in timer after days
  "minutes":  int // remainder minutes left after hours,
  "seconds":  int, // remainder seconds left after minutes
  "total_points":  int,
  "is_paused":  bool,
  "is_locked":  bool,
  "multiplier_points":  float, // 1 = multiplier inactive for points
  "multiplier_time":  float, // 1 = multiplier inactive for time
  "multiplier_start_time": timestamp or null,
  "multiplier_seconds_total": int, // 0 if inactive or no duration set 
  "multiplier_seconds_remaining": int, // 0 if inactive or no duration set or duration ended
  "total_seconds_elapsed": int, // total seconds ever elapsed when unpaused
  "total_seconds_added":  int // total seconds ever added to timer
}
```

### handleSubathonEvent

Whenever an event is processed as having added to the subathon timer successfully,
this data is pushed.

```json
{
    "type": "event",
    "event_type":  string, 
    "source":  string,
    "seconds_added": int, // seconds added from this event
    "points_added":  int, // points added from this event
    "user":  string, // User who triggered event, or SYSTEM or AUTO
    "value":  string, // For certain events, can contain useful data, e.g., $ amount for tips, number of bits, tier of sub. 
    "amount": int,  // For gift subs, will be number of subs. Otherwise, usually 1
    "currency": string, // currency of donations, or type such as "sub", "bits"
    "command":  string, // if event_type was a Command, which command
    "event_timestamp": datetime // timestamp of event triggering
}
```

Types
- [event_type](https://github.com/WolfwithSword/SubathonManager/tree/main/SubathonManager.Core/Enums/SubathonEventType.cs)
- [source](https://github.com/WolfwithSword/SubathonManager/tree/main/SubathonManager.Core/Enums/SubathonEventSource.cs)
- [command](https://github.com/WolfwithSword/SubathonManager/tree/main/SubathonManager.Core/Enums/SubathonCommandType.cs)

### handleGoalsUpdate

When the goals list updates, either with new goals, changed goals, or removal of points

```json
{
    "type": "goals_list",
    "points": int, // current points of the subathon
    "goals": [
      // ... for each goal in all goals
      {
          "text": string, // goal text
          "points":  int, // points for the goal
          "completed": bool // is it completed based on subathong points?
      }
      // ...
    ]
}
```

### handleGoalCompleted

Whenever a new goal is completed (and unchanged from current list)

```json
{
    "type": "goal_completed",
    "goal_text":  string,
    "goal_points":  int, // points for the goal
    "points": int // current points of the subathon at time of completion
}
```

### handleSubathonDisconnect

Triggers whenever the socket disconnects from SubathonManager.

For reconnections, it will always send initial messages to the other fields, as well as a ping/hello system.
So you can simply wait for new data where required.

## Redistribution

As a widget developer, you have full rights to your developed widget(s) and associated files.
You are free to redistribute your widgets, commercially or not, as you wish.

We wish to provide SubathonManager as an open ecosystem to enable 
creatives, artists, hobbyists, content-creators, and more. 
While the program itself is non-commercial and should not be redistributed, widgets are perfectly OK to be!

Although, in the spirit of this project and in support of creatives and developers,
we ask you to not use AI to assist in development of your widgets.