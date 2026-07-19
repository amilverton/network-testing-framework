# Changelog

## 0.2.0 - 2026-07-19

- Add RPC routing, cross-player damage, late-join snapshot/delta, owner NetworkTransform,
  ownership-transfer, and ordered SyncList scenarios.
- Add a seven-scenario suite runner with repeat support and build reuse after the first run.
- Separate shared facts from per-role evidence and assertions in protocol schema version 2.
- Validate built-in scenarios against coordinator-owned exact fact, evidence, assertion, milestone,
  and revision contracts.
- Stagger client launch, require natural Player exits, and reject stale Player reuse by fingerprint.
- Expand the viewer with role-owned assertions, role-local evidence, and differentiated harness logs.

## 0.1.0 - 2026-07-19

- Add an isolated Unity Player builder with attributed scenario discovery.
- Add a three-process PowerShell coordinator with atomic JSON results and retained diagnostics.
- Add a non-locking three-pane live evidence and raw-log viewer with optional coordinator launch.
- Add a real PurrNet UDP, `ServerRpc`, sender-authority, and `SyncVar` inventory scenario.
- Create PurrNet runtime components before root activation to avoid corrupt serialized Player scenes.
- Enforce one absolute run deadline and wait only on the Unity editor PID during builds.
- Add EditMode protocol tests, a disposable PurrNet integration project, and an agent skill.
