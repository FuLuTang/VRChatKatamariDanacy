# O4 hub wiring: why the seat never happened (2026-11-16, Linux-side diagnosis)

> Read-only diagnosis from `play1_lines.log` / `play2_lines.log` / `README.md` in this folder plus the C# at
> `Runtime/Hub/HubLevelLauncher.cs`, `Runtime/Hub/HubLoopMath.cs`, `Runtime/Riding/BallRideStation.cs`,
> `Runtime/Riding/SeatEyeCompensation.cs`. **No Unity run by this author.** The fix and the full operator
> checklist are in `Docs/Design/StoryMode/HUB_ROOM.md` §8–§10.

## What the logs actually show

1. `[hub] round started cause=arrived … distanceFromEntry=0.022` — the arrival teleport worked and the round
   started. `arrival=(0.00, 0.25, -2.20)`.
2. `[ride] seated entered` **never appears**. `VRCStation.UseStation` had exactly one caller before this round:
   `BallRideStation.Interact()` (a click). Nothing in the hub flow called it, so standing next to the ball was
   the expected result even with a perfect arrival point.
3. `[seat] seat restored to authored offset` is `SeatEyeCompensation.EndSeated()`. It has two callers:
   `OnStationExited` and `OnPlayerRespawn`. `[ride] station exit reason=…` is **absent**, so this was a
   **respawn**.
4. Why a respawn: the scene's `LevelEntry` is at `(0, 0.15, -2.2)` (scene root, world), the ball is at
   `(-12.5, 0.15, -12.5)` (`startCenterWorld`), and the level's player floor covers `[-19.4, -6]²`.
   `(0, -2.2)` is outside that floor → the player fell → ClientSim respawned at `VRCSceneDescriptor.spawns`
   (`HubSpawn`, `(1200, 0.1, -2.8)`).
5. `[hub] teleport cause=hold-in-level attempt=1..3 distanceFromLevel=1200 from=(1200.00, 0.10, -2.80)` is the
   hold seeing the respawned player at the room and pulling them back to the same void entry; the next frame
   they fall again. Three attempts, then it gives up. `distanceFromLevel=1200` is the hub↔level separation,
   not a station reading.
6. The operator's `TeleportTo(1200, 0.1, 1.2)` was the stand-in for "walk onto the mat". It is not in the
   runtime, and the hold log's `from` is `HubSpawn`, not the mat — so it was not repeatedly teleporting the
   player. It did its one job (proximity → auto-start) and then stopped mattering.

## What it is not

- **Not** the Station being inactive or out of range: `RideSeat` is active; `UseStation(player)` is a script
  API with no ray/distance requirement.
- **Not** `disableStationExit=1`: that only blocks manual stand-up; `ExitStation` is the documented exit and
  the launcher already uses it.
- **Not** `StandPoint(0.45,-0.15,0)` / `ExitPoint(-0.55,-0.15,0)`: those are enter/exit placements, not
  seating gates.
- **Not** `PlayerMobility=1`: it immobilizes the seated player, it does not refuse `UseStation`.
- **Not** `SeatEyeCompensation` expecting an `Interact`: it starts from `OnStationEntered` → `BeginSeated`.
- **Not** a seated `GetPosition` problem *in this run*: nobody was ever seated. (For the fix, a seated player
  is treated as in-level by the station flag, because a client may report a seated position at the seat, the
  station or somewhere in between.)

## Fix applied (code only; Unity re-run is NOT_RUN)

- `HubLevelLauncher`: when a station + seat are wired, the arrival point is `rideStation.transform` (the seat),
  not the hand-placed `LevelEntry`; after `BeginRound`/`RequestRetry` it calls
  `BallRideStation.TrySeatLocalPlayer()` (3 attempts / 30-frame window), logs `[hub] seat request …` and
  `[hub] seat result seated=…`. The hold uses the same point, skips when seated, and waits
  `holdGraceSeconds=2.5 s` after a start; a hold correction re-requests the seat.
- `BallRideStation.TrySeatLocalPlayer()`: the one `UseStation` path, shared with `Interact`; `OnStationEntered`
  remains the only place that opens the ride gate, notifies the round and starts the seat-eye measurement.
- `HubLoopMath`: pure `ShouldHoldInLevelNow(...)` (seated/grace/attempts) + `ShouldRetrySeat(...)` (retry window) + 5 EditMode tests.
- `HubRoomWiringCheck`: warns when `levelEntries[0]` is more than 5 m from `BallMotor.startCenterWorld`.

## What the operator must see next (same session, ClientSim or real client)

```text
[hub] ready levels=1 … autoSeat=true holdGrace=2.5 …
[hub] teleport cause=enter-level … arrivalSource=seat arrival=(≈ -12.5, 1.16, -13.6)
[hub] round started cause=arrived …
[hub] seat request cause=started requested=true seated=false
[ride] seat requested without a click frame=…
[ride] seated entered
[hub] seat result seated=true frame=…
```

Then `[StoryRoundController] sit-down ignored … state=1` is expected (the round is already Running; the
notification does not restart it). **No** `[seat] seat restored` and **no** `[hub] teleport cause=hold-in-level`
in the first 2.5 s. At the bell, `[ride] station exit reason=station-exit wasSeated=true` followed by
`[seat] seat restored` is normal (return path).

If instead `[hub] seat result seated=false` appears after 30 frames, the client did not take `UseStation`
(ClientSim may not implement stations); the round still runs and the player can click the ball, but a real
client is needed to confirm the seat. `[seat] seat restored` **during the first seconds of a round** still
means a respawn — check that `LevelEntry` was moved onto the level floor (`(-12.5, 0.15, -10.4)`, yaw 180)
and that the arrival log says `arrivalSource=seat`.

## Not run here

Unity/UdonSharp compile, Play Mode, ClientSim, Build & Test, and the seat itself are **NOT_RUN** in this
Linux-side fix; this file records the diagnosis and the exact next observations, not a passing run.
