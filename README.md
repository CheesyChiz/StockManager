# Stock Manager

Stock Manager automatically balances Island Sanctuary gathering resources by running routes imported into Visland.

It selects the resource with the lowest `current amount / target` ratio, chooses an enabled route that gathers it, runs that route once through Visland, and reevaluates the inventory after the route finishes.

Stock Manager is an independent plugin. It does not contain or depend on ExplorersIcebox files.

## Requirements

- Dalamud API 15
- Visland with Island gathering routes imported into the `Island` group
- vnavmesh, as required by Visland route execution

## Installation

Add this URL under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/CheesyChiz/StockManager/main/repo.json
```

Then open `/xlplugins`, install **Stock Manager**, and open it with `/stockmanager`.

## Usage

- Enter a value in **Target for all resources** and click **Apply to all**, or edit individual resource targets.
- A target of `0` disables that resource.
- Choose **Ground only** to reject any route containing a `MountFly` waypoint.
- Choose **Ground and flying** to allow both kinds of routes. `MountNoFly` waypoints are considered ground-compatible.
- Resources locked by the current Island Sanctuary progression or missing tools are detected through the game state and ignored automatically.
- Disable individual routes in the route list when desired.
- **Emergency stop** disables Stock Manager and stops the current Visland route.

### Completion behavior

- **Stop** disables automation when every available target has been reached.
- **Farm and export for cowries** keeps gathering after the targets are reached. Each resource has an independent **Sell above** value. Stock Manager visits the exporter only after the resource reaches `Sell above + Minimum export batch`, sells it back down to **Sell above**, and resumes gathering.

For example, `Sell above = 800` and `Minimum export batch = 100` means the exporter is visited at 900 and the resource is sold back to 800. This hysteresis prevents an exporter trip after every gathering loop. **Visland Auto Export must be disabled** because its global limit would conflict with Stock Manager's per-resource limits.

Imported routes are read from Visland's own configuration. Stock Manager currently recognizes Island gathering nodes by their English interaction names, as stored by the commonly distributed Island route collection.

Use automation at your own risk. Supervise the first run and make sure each imported route works correctly in Visland before enabling automatic switching.

## Building

The project targets .NET 10 and `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/StockManager/StockManager.csproj -c Release
```

Stock Manager is distributed under the MIT license.
