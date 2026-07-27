# Changelog

## 1.7.2

- Added a saved multi-resource selector specifically for the experimental route generator.
- Added shortcuts to copy the Automation selection, select all currently available resources, or clear the generator list.
- Restricted generator candidates and respawn-support nodes to routes compatible with current flight access.
- Renamed the per-resource route reference and explained that it compares direct matching-node yield rather than representing the Best overall decision.

## 1.7.1

- Re-evaluate an active **Best overall route progress** route as resource stocks change.
- Switch only when another route remains substantially better, with minimum-runtime and stability guards to prevent route ping-pong.
- Show the active route and its tracked target separately from the next live recommendation.
- Prevent low-yield support resources from keeping an outdated route active indefinitely.

## 1.7.0

- Added the experimental Routes workbench and editable Stock Manager route copies.
- Improved tight mounted-to-foot transitions, including Rocksalt/Laver.
- Added respawn-aware detours before repeating short routes.
- Improved target-first farming and immediate surplus-export triggers.
