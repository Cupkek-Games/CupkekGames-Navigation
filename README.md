# CupkekGames Navigation

Navmesh agent controller for CupkekGames games. Wraps UnityEngine.AI.NavMesh primitives.

## Locomotion

`NavMeshAgentController` drives its rig from what the agent really does, every frame:

- While it moves it plays its move kind (`MoveAnimationKind`, serialized: `Walk` unless the rig names another, such as a `Run`); standing, or held up against something, it plays `Idle`. A rig without the move kind walks.
- It plays only when the kind changes, so a looping clip is never restarted and a one-shot is never stomped. Speed thresholds and a short stop delay keep a corner or a bump from flickering it.
- `StartLocomotion()` / `StopLocomotion()` turn it on and off.
- `HoldLocomotion()` returns a counted hold: while any is alive, a one-shot (a gather, a throw, sitting) owns the rig. When the last is disposed, locomotion plays what the agent really does at once. If a one-shot's own end puts the rig on `Idle` while the agent moves, locomotion walks it again on the next frame.
- `TurnSharpness` (serialized): `0` lets the agent turn itself; above `0` the controller eases the facing toward where the agent really goes (about 12 is brisk).
- `MoveTo(destination)` walks the agent there and raises `OnDestinationReached` once as it arrives. `ClearDestination()` calls it off.
- `BaseSpeed` is the agent's own top speed; a time scale and `Root` act on it.
- A time scale of zero pauses the follow and freezes locomotion; time again brings back only what was running.

Extracted from HeroManager (Phase A, 2026-04-30).
