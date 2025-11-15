# Configuration


## App Settings

<details>
<summary>Config</summary>
<ul>
<li><b>Port</b>: The port the webserver runs on for your browser sources</li>
<li><b>Default Currency</b>: The currency to convert donations to when calculating time/points</li>
</ul>
</details>

<details>
<summary>Options</summary>
<ul>
<li><b>Open Data Folder</b>: Open the folder that all data is saved to</li>
<li><b>Export Events</b>: Export all events for the current subathon to a CSV</li>
<li><b>Check for Updates</b>: Check for updates, and if available, prompt to auto install</li>
<li><b>Help?</b>: Opens your browser to this wiki</li>
<li><b>Undo Simulated Events</b>: For the current subathon, delete all simulated events and undo their points/seconds that came from this settings page. <b>Warning</b> - this may bring you to negative time/points.</li>
</ul>
</details>

## Twitch

Click connect to link your Twitch account. Once authenticated, it will display your username.

<details>
<summary>Options</summary>
<ul>
<li><b>Auto Pause</b>: Pause on stream end</li>
<li><b>Auto Lock</b>: Lock on stream end</li>
<li><b>Auto Resume</b>: Resume on stream start</li>
<li><b>Auto Unlock</b>: Unlock on stream start</li>
</ul>
</details>

<details>
<summary>Values</summary>
<ul>
<li><b>Cheers/Bits</b>: Seconds per 100 bits, points per 100 bits (rounded)</li>
<li><b>Raids</b>: Seconds per viewer, points per raid</li>
<li><b>Follows</b>: Seconds per follow, points per follow</li>
<li><b>Subs</b>: For T1, T2, T3, seconds and points per sub. Prime counts as T1</li>
<li><b>Gift Subs</b>: For T1, T2, T3, seconds and points per sub gifted</li>
</ul>
</details>

## YouTube

Type in your YT Channel's Handle then click connect

<details>
<summary>Values</summary>
<ul>
<li><b>Super Chats</b>: Seconds per 1$, Points per 1$ (rounded down). 1$ of Default Currency after conversion.</li>
<li><b>Memberships</b>: Seconds and points per membership. Only supports single tier/level*</li>
<li><b>Gift Memberships</b>: Seconds and points per membership gifted. Only supports single tier/level*</li>
</ul>
</details>

## StreamElements

Go to [your dashboard](https://streamelements.com/dashboard/account/channels) and copy your **JWT Token**, paste it in and click connect.

<details>
<summary>Values</summary>
<ul>
<li><b>Tips</b>: Seconds per 1$, Points per 1$ (rounded down). 1$ of Default Currency after conversion.</li>
</ul>
</details>

## StreamLabs

Go to [your dashboard API Settings](https://streamlabs.com/dashboard#/settings/api-settings), go to **API Tokens** and copy your **Socket API Token**, paste it in and click connect.

<details>
<summary>Values</summary>
<ul>
<li><b>Donations</b>: Seconds per 1$, Points per 1$ (rounded down). 1$ of Default Currency after conversion.</li>
</ul>
</details>

## Chat Commands

For Twitch and Youtube Chat, commands can be used to control some aspects of the subathon manager.
All commands can be triggered in chat via `!<name> <options>`.

Channel owner / broadcaster will have permissions for all commands.

For each command, you can set the following:
- **Name**: The command name, such as `addpts`
- **Mods?**: Can mods use this command?
- **VIPs?**: [Twitch Only] Can VIPs use this command?
- **User Whitelist**: Comma separated list of usernames that can use the command

## Discord Webhooks

Log certain events to discord channels.

<details>
<summary>Config</summary>
<ul>
<li><b>Error Log URL</b>: Webhook URL to log errors and notices from the application</li>
<li><b>Event Log URL</b>: Webhook URL to log events for the subathon for audit logging. Sends in batches every minute</li>
<li><b>Log Simulated Events?</b>: For event logs, log simulated events or not?</li>
</ul>
</details>

<details>
<summary>Options</summary>
For each event type, you can choose which ones to log to the Event Log webhook.
</details>

