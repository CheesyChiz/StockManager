# Changelog

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
