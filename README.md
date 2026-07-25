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
- **Farm and export for cowries** keeps gathering after the targets are reached. When any normal material reaches the configured export trigger, Stock Manager runs its built-in Visland route to the Island exporter, selects **Export Materials**, lets Visland Auto Export sell the surplus, and resumes gathering.

The second mode requires **Auto Export** to be enabled in Visland's Exports window. Visland's **Sell normal above** value controls how many materials are kept; Stock Manager never changes that value.

Imported routes are read from Visland's own configuration. Stock Manager currently recognizes Island gathering nodes by their English interaction names, as stored by the commonly distributed Island route collection.

Use automation at your own risk. Supervise the first run and make sure each imported route works correctly in Visland before enabling automatic switching.

## Building

The project targets .NET 10 and `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/StockManager/StockManager.csproj -c Release
```

Stock Manager is distributed under the MIT license.
