# Schmube

Windows desktop IPTV player built with .NET WPF + LibVLCSharp.

## Run
```powershell
dotnet run --project .\Schmube\Schmube.csproj
```

## Built-in configuration
Schmube reads `schmube.config.json` from the application folder. The file currently contains your subscription URL and supports:
- `subscriptionUrl`
- `defaultGroups`
- `applyDefaultGroupsOnLoad`
- `autoLoadOnStartup`

## Usage
1. Launch the app.
2. The configured subscription URL is preloaded automatically.
3. Click `Load Channels` or let auto-load run on startup.
4. Filter by search text, group, favorites, or configured default groups.
5. Double-click a channel or click `Play Selected`.

## Persistence
- App-level config: `schmube.config.json`
- User settings and favorites: `%APPDATA%\Schmube\settings.json`
