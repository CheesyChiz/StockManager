<p align="center">
  <img src="assets/icon.png" width="160" alt="Stock Manager icon">
</p>

<h1 align="center">Stock Manager</h1>

<p align="center">
  Automatic Island Sanctuary stock balancing with routes imported into Visland.
</p>

Stock Manager keeps selected gathering resources at your chosen levels. It runs one Visland route at a time, watches Island inventory while the route is active, and recalculates when the current target is reached or its selected priority is due for review.

## Features

- Uses routes already imported into Visland's `Island` group.
- Offers strict relative-deficit, lowest-stock, highest-stock, and overall route-progress farming priorities.
- Enables resources independently without losing their configured stock values.
- Sorts the resource table by any column.
- Selects the best compatible imported route automatically for each enabled resource.
- Uses vnavmesh to travel from the character's current position before handing control to Visland.
- Offers mount roulette and every unlocked mount in one dropdown; a specific selection also replaces Visland's roulette during Stock Manager routes.
- Detects Island flight at Sanctuary Rank 10 and excludes flight-only routes and high-altitude nodes while it is locked.
- Detects the initial cave unlock at Sanctuary Rank 12 and uses guided vnavmesh paths both into and out of its corridors instead of navigating directly through terrain.
- Enters water, dives, rebuilds navigation in 3D, and mounts underwater when an imported route requires it.
- Can temporarily skip an approach or active route that makes no movement or gathering progress and try another route or resource.
- Uses the game's item/tool availability flags and ignores unavailable resources automatically.
- Optionally uses Lifestream to travel to the Island from a button or when automation starts.
- Can stop when all managed resources reach their targets.
- Can continue farming and export surplus materials for cowries using individual sell limits.
- Interrupts the active gathering loop as soon as an enabled material reaches its export trigger, then sells every configured surplus in one visit.
- Uses **Isle Return** before long exporter trips, stops the travel route at the NPC, and walks out of the cabin before trying to mount again.
- Includes a zoomable, pannable Island map with route overlays, all-node and per-resource views, and resource tooltips.
- Includes a separate experimental route workbench for generated previews and editable copies of imported routes.

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

Click the `Resource`, `Current`, stock-value, or `Status` table header to sort the resource list. New installations default to **Best overall route progress**. Existing installations keep their selected priority.

The farming priorities intentionally behave differently:

- **Largest relative deficit (strict)** selects the resource with the lowest `current / target` ratio first. It may keep choosing a long route that contains only one or two matching nodes when that resource is far behind.
- **Best overall route progress** scores every compatible route using all enabled unfinished resources it can advance. It also considers useful node yield, remaining deficits, already-complete nodes, estimated loop length, and the approach from the character's current position. To prevent long-distance ping-pong, it keeps the current area while its route remains within 80% of the best score.
- **Lowest current stock** and **Highest current stock** compare raw inventory counts. Like relative deficit, they are strict resource-first modes.

Only enabled, unlocked resources served by at least one compatible imported route participate in completion checks.

While **Best overall route progress** is running, Stock Manager also re-evaluates the active route as stocks change. After at least 45 seconds on the route, it switches only when the same alternative remains substantially better for eight consecutive seconds. The route panel shows the actual active route and tracked target separately from the next recommendation.

Strict modes choose the winning resource before applying short-route respawn detours, so a detour can change the route but cannot silently replace the selected resource. The active choice is reviewed every 10 minutes, immediately when its target is reached, and immediately after relevant settings change. The route panel shows both the current strict winner and the time until the next scheduled review.

In **Stop** mode, a resource is unchecked for the current run as soon as it reaches its target. If it was the current route target, Stock Manager stops that loop and recalculates immediately; no manual restart is required. This is session state only: the saved selection returns on the next start. Raising the target or consuming stock below it during the same run activates the resource again automatically.

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

In export mode, resources below 800 are filled first. A resource at or above 800 is no longer selected as the intentional target while another enabled resource is still below its limit, although it can still be gathered as a by-product of a useful route. Once every enabled resource has reached its retained stock, Stock Manager selects efficient overall routes for continued cowrie farming.

As soon as any enabled resource reaches 900, Stock Manager interrupts the active loop and visits the exporter. Once the shop is open, the plugin sells every configured resource above its individual `Sell above` value, including resources whose farming checkbox is off, and then resumes gathering. This gap prevents a new exporter trip after every route loop.

The stock value plus the export batch can never exceed the Island material cap of `999`. Invalid settings are shown in red and automation cannot start until they are corrected. A stock value of `999` is therefore valid only in **Stop** mode; export mode needs room for at least one gathered item above the sell value.

Stock Manager owns surplus selling in export mode and automatically disables Visland's global **Auto Export** option before travelling to the shop, so there is only one active set of export rules. Visland's **Exports Automation** window may still be opened manually, but its **Auto Export** checkbox must remain off while Stock Manager is running; the plugin enforces this again before each export trip.

For a distant export trip, Stock Manager uses the Island's **Isle Return** action and then runs only the short exterior-to-exporter path. It stops Visland at the NPC before opening the menus, preventing the one-shot route from walking back to its beginning. After selling, it walks to the base exterior before the next mount attempt; if the cabin exit path fails, Isle Return is used as a fallback.

## Route rules

- Routes are not enabled manually. A checked resource is the single source of truth, and Stock Manager chooses its best available route by yield and current deficits.
- Stock Manager asks vnavmesh to path from the character's current position to the selected route. It does not return to the Island base first.
- For a longer transfer, Stock Manager mounts before starting vnavmesh. The mount dropdown begins with roulette and then lists every unlocked mount. A specific selection is applied both to the transfer and to mount requests made by Visland while a Stock Manager route is active.
- If a mount remains unavailable near the Cozy Cabin, Stock Manager first walks to the base exterior instead of retrying the mount indoors.
- When the character is already within 35 yalms of a route, the closest waypoint becomes the start of that loop. Otherwise Stock Manager approaches the route's original first waypoint.
- While on the Island, Sanctuary Rank 10 and the game's flight-access flag are checked together.
- If flight is locked, routes named as flying routes, routes containing high-altitude-only nodes, and surface routes that require `MountFly` are ignored. Mixed surface/underwater routes remain usable and their surface transfer is downgraded to ground-mounted movement.
- Coal, Shale, Glimshroom, Effervescent Water, and Spectrine routes are ignored before the rank 12 cave expansion. Once available, Stock Manager follows guided flight corridors both into the cave and back outside before handing movement to an imported Visland route. If an exit cannot make progress, Isle Return is used as a safety fallback. If the entrance is still physically closed, complete the rank 12 cave expansion and Mammet-sized Spelunking Tools; the plugin reports this instead of repeatedly flying into the wall.
- Underwater route approaches keep swimming on the surface until diving is possible, then dismount, dive, mount underwater, and ask vnavmesh for a 3D path. A manual dive is detected and causes the same mounted repath. Once Visland starts the route, Stock Manager no longer interrupts its active underwater path.
- Optional stuck recovery measures both movement and inventory progress during the approach and active gathering loop. After the configured timeout, the route cools down for five minutes and another eligible choice is made. The supported timeout range is 8-60 seconds; a lower value would commonly fire during normal pathfinding, mounting, or gathering animations.
- Empty on-foot transition waypoints immediately after mounted travel receive a small temporary arrival radius. This lets Visland dismount before tight shoreline or cave geometry without changing gathering-node interaction radii.
- Imported routes with fewer than 12 unique gathering nodes prefer another useful route before repeating when one is available. Eleven nodes is the theoretical respawn minimum; the extra detour leaves room for one missed interaction.
- Routes with no recognized Island gathering nodes are ignored.

Stock Manager currently recognizes the English interaction names used by the commonly distributed Island route collection.

## Experimental route workbench

The **Routes** tab contains the route generator and editor. The generator has its own saved multi-resource selection, independent from the farming checkboxes on **Automation**. Use **Use Automation selection** as a shortcut, select every currently available resource, or clear the list and choose one or more materials manually. It builds a compact temporary route from compatible gathering nodes already present in imported Visland routes, adds nearby target nodes before distant detours, adds support nodes until the loop has a 12-node respawn safety margin, and orders the result into a short cycle. Nearby hops are marked for walking; longer legs use the selected mount.

An imported route or generated preview can be saved as an editable Stock Manager route. Its name, waypoint order, coordinates, arrival radii, movement mode, and pathfinding flag can be changed without modifying the original route in Visland. A uniquely named custom route can optionally participate in automatic selection. The editor can add the character's current position or any gathering node known from the imported route collection.

When flight is locked, flight-only source routes and high-altitude nodes are excluded from generation. That does not prove that every remaining node-to-node leg is reachable: imported data can include progression-gated cave nodes, and generated routes do not preserve every traversal waypoint from their source routes. Ground and flight test loops are therefore allowed but explicitly experimental and must be supervised. Use **Stop test loop** to end only the test, or **Emergency stop** to abort all automation.

The **Map** tab uses the in-game Island map texture at a fixed square aspect ratio. Imported routes, editable Stock Manager routes, and the latest generated preview are placed using the map's real size factor and offsets. Use the mouse wheel to zoom around the cursor, left-drag to pan, and **Reset view** to return to the default framing. The view selector can show one route, all gathering nodes known from imported and editable routes, or only nodes for one resource. Hover a gathering point to see its resources and world coordinates.

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
