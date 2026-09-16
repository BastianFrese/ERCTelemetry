# ERCTelemetry

Windows desktop app (WPF, .NET 10) that reads EA F1 26 UDP telemetry, shows live
standings/results for all players, a player-vs-rival telemetry dashboard, stream
overlays (OBS browser source + in-game overlay window) and stores session history.

## Before doing anything

Read `docs/HANDOFF.md` — it has the current project status, what is already done,
the remaining implementation phases (3–6) in order, and verified facts about the
F1Game.UDP 26.0.0 packet API that are not obvious from the package docs.

## Project layout

- `src/ERCTelemetry.Core` — net10.0 class library, all logic (headless-testable)
- `src/ERCTelemetry.App` — net10.0-windows WPF app (AssemblyName: `ERCTelemetry`)
- `tests/ERCTelemetry.Core.Tests` — xUnit tests
- `ERCTelemetry.slnx` — solution (the old classic `ERCTelemetry/` folder at the root is
  dead code, not part of the build — see HANDOFF.md)

## Workflow

- `dotnet build` and `dotnet test` must stay green after every change.
- **Before publishing a release, read `docs/RELEASE.md`** — version rules, the mandatory
  `UPDATELOG.md` entry (user-visible via the in-app "Update-Log" button) and the publish
  command. Never publish without it.
- Never read an existing `Channel<T>` from a second consumer — channels are
  competing-consumer queues; give each new consumer its own channel.
- Guard all packet-array loops with `Math.Min(span.Length, TelemetryConstants.MaxCars)` —
  F1Game.UDP arrays have 24 slots, the game has max 22 cars.