# SubathonManager - Elgato Stream Deck Plugin

A Stream Deck plugin that sends SubathonManager commands over the
app's websocket (`ws://127.0.0.1:<port>/ws`)

- On connect it identifies itself with `{ "ws_type": "IntegrationSource", "source": "StreamDeck" }`, connection status visible in SubathonManager
- It requests the live command catalog with `{ "ws_type": "Command", "request": "commands" }` to fetch all current commands
- A key press sends
  `{ "ws_type": "Command", "type": "Command", "command": "<cmd>", "message": "<parameter>", "user": "EXTERNAL", "source": "StreamDeck", "context": "<key>" }`
  and shows the Stream Deck OK/alert overlay based on the app's `command_ack` reply.

## Parameter examples

| Command | Parameter |
|---------|-----------|
| Add/Remove/Set Time | `10m30s`, `1h`, `90s` |
| Add/Remove/Set Points | `500` |
| Add/Remove Money | `5 USD` |
| Set Multiplier | `2xtp 10m` (x = multiplier, t = time, p = points) |
| Add/Set/Remove Wheel Spins | `2` |

Many commands do not require parameters

## Setup

- `package.ps1` makes a `SubathonManager_StreamDeck.streamDeckPlugin` - double-click to install in the Stream Deck app.
- Can also grab it from the CI/CD or release
