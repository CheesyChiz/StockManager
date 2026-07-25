<p align="center">
  <img src="assets/icon.png" width="160" alt="Stock Manager icon">
</p>

<h1 align="center">Stock Manager</h1>

<p align="center">
  Automatic Island Sanctuary stock balancing with routes imported into Visland.
</p>

Stock Manager keeps selected gathering resources at your chosen levels. It runs one Visland route at a time, checks the Island inventory after every loop, and switches to whichever available resource is furthest below its target.

This is an independent plugin. It does not contain or require ExplorersIcebox files.

## Features

- Uses routes already imported into Visland's `Island` group.
- Balances resources by their relative deficit instead of repeatedly farming one fixed route.
- Supports one-click targets and individual per-resource targets.
- Supports ground-only routes or both ground and flying routes.
- Detects resources locked by Island progression or missing tools and ignores them automatically.
- Can stop when all managed resources reach their targets.
- Can continue farming and export surplus materials for cowries using individual sell limits.
- Provides independent, side-by-side resource and route lists.

## Requirements

- Dalamud API 15
- [Visland](https://github.com/awgil/ffxiv_visland)
- vnavmesh, as required by Visland route execution
- Island gathering routes imported into Visland's `Island` group

Test every imported route manually in Visland before enabling unattended switching.

## Installation

1. Open `/xlsettings`.
2. Select **Experimental**.
3. Add the following URL under **Custom Plugin Repositories**:

   ```text
   https://raw.githubusercontent.com/CheesyChiz/StockManager/main/repo.json
   ```

4. Open `/xlplugins` and install **Stock Manager**.
5. Open the plugin with `/stockmanager`.

Installing through the custom repository provides normal Dalamud update notifications. Manual ZIP installation is not recommended.

## Quick start

1. Select **Ground only** or **Ground and flying**.
2. Enable the routes Stock Manager may use.
3. Set **Farm target for all**, click **Apply**, and adjust individual resources if needed.
4. Choose what happens after all managed resources reach their targets:
   - **Stop** ends automation.
   - **Farm and export for cowries** continues gathering and sells configured surplus.
5. Click **Start automation** while on your Island Sanctuary.

Only unlocked resources served by at least one enabled, movement-compatible route participate in completion checks.

## Targets and exporting

| Setting | Meaning |
| --- | --- |
| **Farm** | Minimum desired stock. `0` disables the resource as a balancing target. |
| **Sell above** | Amount retained after exporting. `999` effectively disables selling that resource. |
| **Minimum export batch** | Extra stock required before travelling to the exporter. |

Example:

```text
Farm:                 99
Sell above:          800
Minimum export batch: 100
```

The resource remains complete after reaching 99. In export mode, Stock Manager continues farming it, visits the exporter at 900, sells it back to 800, and resumes gathering. This gap prevents a new exporter trip after every route loop.

`Sell above` can never be lower than `Farm`. Raising a farm target automatically raises an incompatible sell limit.

Stock Manager owns surplus selling in export mode and automatically disables Visland's global **Auto Export** option, so there is only one active set of export rules.

## Route rules

- **Ground only** excludes every route containing a `MountFly` waypoint.
- `MountNoFly` waypoints are ground-compatible.
- Disabled routes do not contribute resources to completion or export decisions.
- Routes with no recognized Island gathering nodes are ignored.

Stock Manager currently recognizes the English interaction names used by the commonly distributed Island route collection.

## Commands

| Command | Action |
| --- | --- |
| `/stockmanager` | Open the Stock Manager window. |

Use **Emergency stop** to disable Stock Manager and stop the current Visland route immediately.

## Building

The project targets .NET 10 and `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/StockManager/StockManager.csproj -c Release
```

## Disclaimer and license

Gameplay automation may violate FFXIV rules. Use this plugin at your own risk and supervise initial route and export tests.

Stock Manager is distributed under the [MIT license](src/StockManager/LICENSE). The plugin icon was generated with OpenAI image generation and locally post-processed for transparency and small-size readability.
