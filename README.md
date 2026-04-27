# Schmube

Windows desktop IPTV player built with .NET WPF + LibVLCSharp.

## Run

```powershell
dotnet run --project .\Schmube.csproj
```

## Built-in configuration

Schmube reads `schmube.config.json` from the application folder. The file supports:

- `subscriptionUrl`
- `defaultGroup`
- `autoLoadOnStartup`
- `recordingsDirectory`

`defaultGroup` is a single channel group name. When channels load, Schmube automatically selects that group in the group filter if it exists.

If `recordingsDirectory` is empty, recordings are written to `%USERPROFILE%\Videos\Schmube`.

Recordings are written as local `.ts` files. Schmube records one stream at a time; if a recording is already running, another channel cannot start recording until the active recording stops.

The control window can record without opening the player window. Select a channel and click `Record` to start immediately. While that channel is selected, the button changes to `Stop Rec`; if a different channel is selected while recording is active, the button shows that recording is already running and is disabled until you select the recorded channel.

The control window can also schedule a channel recording directly. Select a channel, set the schedule date, start time, and length in minutes next to the `Record` button, then click `Schedule`. Keep Schmube open until the schedule runs. Pending schedules and recording history are saved in `%APPDATA%\Schmube\settings.json`.

Program guide entry `Record` buttons schedule that specific program using the configured start/end padding. Player-window `Record`, `Schedule`, `History`, and `Folder` controls remain available when the player window is open.

## Group flags

Schmube reads `schmube.group-flags.json` from the application folder and uses it to decorate channels with local country flag images. It first tries to infer the country from the channel name prefix and falls back to the group name if the channel prefix does not resolve.

The mapping file is optional. Standard prefixes such as `PT`, `DE`, `NL`, `USA`, or full names like `Portugal` are resolved automatically. Use the JSON file for provider-specific aliases and forced matches.

Some provider group patterns are handled directly. `LA| ARGENTINA` maps to Argentina, while generic `AR|` groups do not map to Argentina. Arabic-region groups such as `AR| ALGERIA`, `AR| ALGERIE`, `AR| BAHRAIN`, `AR| EGYPT`, `AR| EMIRATES`, `AR| IRAQ`, `AR| JORDAN`, `AR| KUWAIT`, `AR| LEBANON`, `AR| LIBYA`, `AR| MOROCCO`, `AR| OMAN`, `AR| PALESTINE`, `AR| QATAR`, `AR| SUDAN`, `AR| SYRIA`, `AR| TUNISIA`, and `AR| YEMEN` map to their country flags. `AR| NETFLIX`, `AR| BEIN`, `AR| MBC`, and `AR| ALWAN` map to bundled logo assets instead of country flags.

Example:

```json
{
  "aliases": {
    "CHL": "CL",
    "UKM": "GB"
  },
  "rules": [
    {
      "match": "24/7",
      "matchMode": "Contains",
      "countryCode": "GB",
      "label": "United Kingdom"
    }
  ]
}
```

`aliases` maps any custom provider token to the real two-letter flag asset code used by the app. So if your provider uses `CHL: Canal 13`, adding `"CHL": "CL"` makes Schmube use the Chile flag. If you map to a country that is not already bundled, add the matching lowercase PNG to `Assets/Flags`, for example `Assets/Flags/cl.png`.

## TV listings

Use `TV Listings` after loading a playlist. It opens a browser window inside Schmube so you can load a football match page such as a Live Soccer TV match page, generate a list of broadcaster offers from the rendered page, match those offers against your current playlist, and turn the compatible results into a temporary list.

The TV listings window now separates three things:

1. broadcaster extraction from the current page
2. compatible playlist channel matching
3. temporary list creation from the generated compatible channels

The temporary list only affects the current browsing session. Once applied, the main channel grid, `Play Selected`, and the player window next/previous controls all operate on that generated subset until you click `Clear Temp`.

Optional channel alias overrides can be stored in `schmube.channel-matching.json`. Use `schmube.channel-matching.example.json` as a starting point when a website broadcaster name does not closely match the name used in your playlist.

## Usage

1. Launch the app.
2. The configured subscription URL is preloaded automatically.
3. Click `Load Channels` or let auto-load run on startup.
4. The configured default group is auto-selected after load.
5. Channels are sorted by name by default. Switch column presets with `Columns`; the standard and detail presets include the original source group as `Source Group`.
6. Filter further by search text, manual group selection, favorites, or recents.
7. Double-click a channel or click `Play Selected` to open it in the independent player window.
8. Use `Record` in the selected-channel panel to record a channel immediately without opening the player window.
9. Use the selected-channel date, time, and length fields plus `Schedule` to schedule a future recording without opening the player window.
10. Use a program guide entry's `Record` button to schedule that program with the configured start/end padding.
11. Use `Shot` in the player window, or press `Ctrl+S`, to save the current frame to `%USERPROFILE%\Pictures\Schmube\Screenshots`.
12. Use `Record` or `Schedule` in the player window when you are already watching a stream.
13. Use `History` in the player window to review pending and completed recordings, open the file location, play a saved file, or delete it.
14. Use `Folder` in the player window to jump directly to the recordings directory.

## Persistence

- App-level config: `schmube.config.json`
- Group-flag mapping: `schmube.group-flags.json`
- User settings, favorites, recents, recording defaults, pending schedules, and recording history: `%APPDATA%\Schmube\settings.json`
- Logo cache: `%LOCALAPPDATA%\Schmube\logos`
- Default recordings folder: `%USERPROFILE%\Videos\Schmube`
