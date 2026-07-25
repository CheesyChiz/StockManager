<p align="center">
  <img src="assets/icon.png" width="160" alt="Stock Manager icon">
</p>

<h1 align="center">Stock Manager</h1>

<p align="center">
  Automatic Island Sanctuary stock balancing with routes imported into Visland.
</p>

Stock Manager keeps selected gathering resources at your chosen levels. It runs one Visland route at a time, checks the Island inventory after every loop, and switches to whichever available resource is furthest below its target.

## Features

- Uses routes already imported into Visland's `Island` group.
- Balances resources by their relative deficit instead of repeatedly farming one fixed route.
- Enables resources independently without losing their configured stock values.
- Selects the best compatible imported route automatically for each enabled resource.
- Uses vnavmesh to reach a route's first waypoint before handing control to Visland.
- Detects Island flight access and excludes flying routes and their exclusive resources when flight is locked.
- Detects resources locked by Island progression or missing tools and ignores them automatically.
- Optionally uses Lifestream to travel to the Island from a button or when automation starts.
- Can stop when all managed resources reach their targets.
- Can continue farming and export surplus materials for cowries using individual sell limits.
- Includes an experimental mixed-resource route generator based on nodes found in imported routes.

## Requirements

- Dalamud API 15
- [Visland](https://github.com/awgil/ffxiv_visland)
- vnavmesh, as required by Visland route execution
- Island gathering routes imported into Visland's `Island` group

Optional: [Lifestream](https://github.com/NightmareXIV/Lifestream) enables the **Travel to Island** button and automatic travel on start. Stock Manager waits for the Island to finish loading before it selects or starts a route.

Test every imported route manually in Visland before enabling unattended switching.

## Installation

1. Open `/xlsettings`.
2. Select **Experimental**.
3. Add the following URL under **Custom Plugin Repositories**:

   ```text
   https://raw.githubusercontent.com/CheesyChiz/StockManager/main/repo.json
   ```

4. Open `/xlplugins` and install **Stock Manager**.
5. Open the plugin with `/stockmanager` or the shorter `/sm` alias.

Installing through the custom repository provides normal Dalamud update notifications. Manual ZIP installation is not recommended.

## Quick start

1. Travel to your Island Sanctuary so Stock Manager can detect flight and tool access. If Lifestream is installed, use **Travel to Island** or enable automatic travel on start.
2. Check the resources you want to farm and set their stock values. **Enable all available resources** toggles every currently farmable resource at once without changing its value.
3. Choose what happens after all managed resources reach their targets:
   - **Stop** ends automation.
   - **Farm and export for cowries** continues gathering and sells configured surplus.
4. Click **Start automation**. Stock Manager selects routes automatically from the imported `Island` group.

Only enabled, unlocked resources served by at least one compatible imported route participate in completion checks.

## Stock values and exporting

| Setting | Meaning |
| --- | --- |
| **Target stock** | In **Stop** mode, the amount that must be gathered before the resource is complete. |
| **Sell above** | The same stock value in export mode: the amount retained whenever surplus is sold. |
| **Export batch** | Extra stock required before travelling to the exporter. |

Example:

```text
Sell above:   800
Export batch: 100
```

In export mode, the resource is initially complete at 800. Stock Manager then continues farming it, visits the exporter at 900, sells it back to 800, and resumes gathering. This gap prevents a new exporter trip after every route loop.

The stock value plus the export batch can never exceed the Island material cap of `999`. Invalid settings are shown in red and automation cannot start until they are corrected. A stock value of `999` is therefore valid only in **Stop** mode; export mode needs room for at least one gathered item above the sell value.

Stock Manager owns surplus selling in export mode and automatically disables Visland's global **Auto Export** option, so there is only one active set of export rules.

## Route rules

- Routes are not enabled manually. A checked resource is the single source of truth, and Stock Manager chooses its best available route by yield and current deficits.
- Before every gathering or export route, Stock Manager runs a short Visland/vnavmesh approach route to the first waypoint and verifies arrival before starting the real route. This avoids direct movement into terrain when a route starts far away and preserves Visland's mount and flight handling.
- While on the Island, the game flight-access flag is detected automatically.
- If flight is locked, every route containing a `MountFly` waypoint and resources available only through those routes are ignored.
- `MountNoFly` waypoints remain ground-compatible.
- Routes with no recognized Island gathering nodes are ignored.

Stock Manager currently recognizes the English interaction names used by the commonly distributed Island route collection.

## Experimental route generator

Expand **Experimental route generator** under the automatic route summary to build a temporary mixed-resource route. It combines nodes for the checked resources, adds support nodes when needed for a stable 11-node loop, orders them into a short cycle, and lets Visland execute one supervised test loop through vnavmesh.

Generated previews are not saved to Visland and are not used by normal automation yet. Their displayed length is a straight-line estimate; terrain-aware movement is handled by vnavmesh during the test. Always supervise experimental routes and use **Emergency stop** if navigation is incorrect.

## Commands

| Command | Action |
| --- | --- |
| `/stockmanager` or `/sm` | Open the Stock Manager window. |
| `/sm start` | Start automation and reset the collection statistics for this run. |
| `/sm stop` | Stop automation and the current route. |
| `/sm status` | Print the current state, active route, elapsed time, and resources collected this run. |
| `/sm travel` | Ask Lifestream to travel to the Island. |
| `/sm emergency` | Stop Stock Manager, Visland, vnavmesh, and active Lifestream travel immediately. |
| `/sm help` | Print the command list. |

Session statistics count positive inventory changes while automation is active, so gathered materials remain in the total even after Stock Manager exports the surplus.

## Building

The project targets .NET 10 and `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/StockManager/StockManager.csproj -c Release
```

## Disclaimer and license

Gameplay automation may violate FFXIV rules. Use this plugin at your own risk and supervise initial route and export tests.

Stock Manager is distributed under the [MIT license](src/StockManager/LICENSE). The plugin icon was generated with OpenAI image generation and locally post-processed for transparency and small-size readability.
