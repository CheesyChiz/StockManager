<p align="center">
  <img src="assets/icon.png" width="160" alt="Stock Manager icon">
</p>

<h1 align="center">Stock Manager</h1>

<p align="center">
  Automatic Island Sanctuary stock balancing with routes imported into Visland.
</p>

Stock Manager keeps selected gathering resources at your chosen levels. It runs one Visland route at a time, checks the Island inventory after every loop, and selects the next resource using your chosen priority strategy.

## Features

- Uses routes already imported into Visland's `Island` group.
- Offers relative-deficit, lowest-stock, highest-stock, and fastest-route farming priorities.
- Enables resources independently without losing their configured stock values.
- Sorts the resource table by any column.
- Selects the best compatible imported route automatically for each enabled resource.
- Uses vnavmesh to travel from the character's current position before handing control to Visland.
- Offers mount roulette and every unlocked mount in one dropdown; a specific selection also replaces Visland's roulette during Stock Manager routes.
- Detects Island flight at Sanctuary Rank 10 and excludes flight-only routes and high-altitude nodes while it is locked.
- Enters water, dives, rebuilds navigation in 3D, and mounts underwater when an imported route requires it.
- Can temporarily skip a route whose vnavmesh approach makes no progress and try another route or resource.
- Detects resources locked by Island progression or missing tools and ignores them automatically.
- Optionally uses Lifestream to travel to the Island from a button or when automation starts.
- Can stop when all managed resources reach their targets.
- Can continue farming and export surplus materials for cowries using individual sell limits.
- Includes a schematic route map and a clustered experimental route generator based on imported nodes.

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
5. Open the plugin with `/stockmanager` or the shorter `/sm` alias. Use its **Settings** tab for mount, priority, and stuck-recovery options.

Installing through the custom repository provides normal Dalamud update notifications. Manual ZIP installation is not recommended.

## Quick start

1. Travel to your Island Sanctuary so Stock Manager can detect flight and tool access. If Lifestream is installed, use **Travel to Island** or enable automatic travel on start.
2. Check the resources you want to farm and set their stock values. **Enable all available resources** toggles every currently farmable resource at once without changing its value.
3. Choose what happens after all managed resources reach their targets:
   - **Stop** ends automation.
   - **Farm and export for cowries** continues gathering and sells configured surplus.
4. Click **Start automation**. Stock Manager selects routes automatically from the imported `Island` group.

Click the `Resource`, `Current`, stock-value, or `Status` table header to sort the resource list. The default priority is **Largest relative deficit**, which compares each current count to its own target instead of comparing raw counts.

Only enabled, unlocked resources served by at least one compatible imported route participate in completion checks.

In **Stop** mode, a resource is unchecked for the current run as soon as it reaches its target; no restart is required before the next resource is selected. This is session state only: the saved selection returns on the next start. Raising the target or consuming stock below it during the same run activates the resource again automatically.

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

In export mode, the resource is initially complete at 800. Stock Manager then continues farming it. As soon as any enabled resource is at 900 or higher after a route loop, the next action is a trip to the exporter. Once the shop is open, the plugin sells every resource above its individual `Sell above` value, including resources whose farming checkbox is off, and then resumes gathering. This gap prevents a new exporter trip after every route loop.

The stock value plus the export batch can never exceed the Island material cap of `999`. Invalid settings are shown in red and automation cannot start until they are corrected. A stock value of `999` is therefore valid only in **Stop** mode; export mode needs room for at least one gathered item above the sell value.

Stock Manager owns surplus selling in export mode and automatically disables Visland's global **Auto Export** option, so there is only one active set of export rules.

## Route rules

- Routes are not enabled manually. A checked resource is the single source of truth, and Stock Manager chooses its best available route by yield and current deficits.
- Stock Manager asks vnavmesh to path from the character's current position to the selected route. It does not return to the Island base first.
- For a longer transfer, Stock Manager mounts before starting vnavmesh. The mount dropdown begins with roulette and then lists every unlocked mount. A specific selection is applied both to the transfer and to mount requests made by Visland while a Stock Manager route is active.
- When the character is already within 35 yalms of a route, the closest waypoint becomes the start of that loop. Otherwise Stock Manager approaches the route's original first waypoint.
- While on the Island, Sanctuary Rank 10 and the game's flight-access flag are checked together.
- If flight is locked, routes named as flying routes, routes containing high-altitude-only nodes, and surface routes that require `MountFly` are ignored. Mixed surface/underwater routes remain usable and their surface transfer is downgraded to ground-mounted movement.
- Underwater route approaches automatically stop the old 2D path, dive, and ask vnavmesh for a 3D path. A manual dive is detected and causes the same repath.
- Optional stuck recovery measures progress toward the route start. After the configured timeout, the route cools down for five minutes and another eligible choice is made.
- Routes with no recognized Island gathering nodes are ignored.

Stock Manager currently recognizes the English interaction names used by the commonly distributed Island route collection.

## Experimental route generator

Expand **Experimental route generator** under the automatic route summary to build a temporary mixed-resource route. It finds a compact cluster that covers the checked resources, fills the route with nearby target nodes using the smallest added detour, adds support nodes only when needed for a stable 11-node loop, and orders the result into a short cycle. Nearby hops are marked for walking; longer legs use the selected mount.

When flight is locked, flight-only source routes and high-altitude nodes are excluded from generation. Generated previews are not saved to Visland and are not used by normal automation yet. Their displayed length is a straight-line estimate; terrain-aware movement is handled by vnavmesh during the test. Use **Stop test loop** to end only the generated test, or **Emergency stop** if all automation must be aborted.

The **Map** tab plots imported routes and the latest generated preview in Island world coordinates. It shows waypoint links, walking/mounted/flying/underwater movement, gathering nodes, the player position, and hover details. This first version is a schematic coordinate view rather than the in-game map texture.

## Commands

| Command | Action |
| --- | --- |
| `/stockmanager` | Open the Stock Manager window. |
| `/stockmanager start` | Start automation and reset the collection statistics for this run. |
| `/stockmanager stop` | Stop automation and the current route. |
| `/stockmanager status` | Print the current state, active route, elapsed time, and resources collected this run. |
| `/stockmanager travel` | Ask Lifestream to travel to the Island. |
| `/stockmanager emergency` | Stop Stock Manager, Visland, vnavmesh, and active Lifestream travel immediately. |
| `/stockmanager help` | Print the command list. |

`/sm` is an alias for `/stockmanager`; every subcommand works with either form.

Session statistics count positive inventory changes while automation is active, so gathered materials remain in the total even after Stock Manager exports the surplus.

## Building

The project targets .NET 10 and `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/StockManager/StockManager.csproj -c Release
```

## Disclaimer and license

Gameplay automation may violate FFXIV rules. Use this plugin at your own risk and supervise initial route and export tests.

Stock Manager is distributed under the [MIT license](src/StockManager/LICENSE). The plugin icon was generated with OpenAI image generation and locally post-processed for transparency and small-size readability.
