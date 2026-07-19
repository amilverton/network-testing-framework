# Changelog

## 0.1.0 - 2026-07-19

- Add an isolated Unity Player builder with attributed scenario discovery.
- Add a three-process PowerShell coordinator with atomic JSON results and retained diagnostics.
- Add a non-locking three-pane live evidence and raw-log viewer with optional coordinator launch.
- Add a real PurrNet UDP, `ServerRpc`, sender-authority, and `SyncVar` inventory scenario.
- Create PurrNet runtime components before root activation to avoid corrupt serialized Player scenes.
- Enforce one absolute run deadline and wait only on the Unity editor PID during builds.
- Add EditMode protocol tests, a disposable PurrNet integration project, and an agent skill.
