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

`defaultGroup` is a single channel group name. When channels load, Schmube automatically selects that group in the group filter if it exists.

## Usage
1. Launch the app.
2. The configured subscription URL is preloaded automatically.
3. Click `Load Channels` or let auto-load run on startup.
4. The configured default group is auto-selected after load.
5. Filter further by search text, manual group selection, or favorites.
6. Double-click a channel or click `Play Selected`.

## Persistence
- App-level config: `schmube.config.json`
- User settings and favorites: `%APPDATA%\Schmube\settings.json`
- Logo cache: `%LOCALAPPDATA%\Schmube\logos`
