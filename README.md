# Icebox Route Manager

Дополнение для `ExplorersIcebox 1.1.0.3`, которое автоматически переключает маршруты Island Sanctuary в зависимости от нехватки ресурсов.

Менеджер выбирает предмет с минимальным отношением `текущее количество / цель`, затем запускает разрешённый маршрут с наибольшим количеством этого предмета за круг. После завершения задания запасы перечитываются и решение принимается заново.

## Состав

- `src/IceboxRouteManager` — отдельный плагин-менеджер.
- `src/ExplorersIcebox` — совместимый форк ExplorersIcebox с минимальным IPC-мостом.
- Готовые DLL публикуются в разделе **Releases**.

## Требования

- Dalamud API 15;
- ExplorersIcebox 1.1.0.3;
- Visland;
- vnavmesh.

## Установка

1. Скачайте архив из последнего GitHub Release.
2. Полностью закройте FFXIV и XIVLauncher.
3. Сделайте резервную копию `%APPDATA%\XIVLauncher\installedPlugins\ExplorersIcebox\1.1.0.3\ExplorersIcebox.dll`.
4. Замените её файлом `PatchedExplorersIcebox\ExplorersIcebox.dll` из архива.
5. Запустите игру и откройте `/xlsettings`.
6. Добавьте `IceboxRouteManager\IceboxRouteManager.dll` в **Dev Plugin Locations** и загрузите плагин.
7. Откройте настройки командой `/iceboxmanager`.

## Использование

- Цель `0` отключает ресурс.
- В секции маршрутов можно запретить нежелательные маршруты.
- `Maximum loops before reevaluation` ограничивает число кругов до следующего пересчёта.
- `Emergency stop` останавливает менеджер и текущее задание ExplorersIcebox.

Первый запуск рекомендуется выполнить под наблюдением с 1–3 кругами до пересчёта.

## Сборка

Проекты рассчитаны на .NET 10 и `Dalamud.NET.Sdk 15.0.0`:

```powershell
dotnet build src/ExplorersIcebox/ExplorersIcebox.sln -c Release
dotnet build src/IceboxRouteManager/IceboxRouteManager.csproj -c Release
```

## Лицензии

- Модифицированный ExplorersIcebox основан на проекте [LeontopodiumNivale14/Explorers-Icebox](https://github.com/LeontopodiumNivale14/Explorers-Icebox) и распространяется по AGPL-3.0-or-later.
- Icebox Route Manager распространяется по MIT; текст лицензии находится в `src/IceboxRouteManager/LICENSE`.

Автоматизация игрового процесса может нарушать правила FFXIV. Используйте на свой риск.
