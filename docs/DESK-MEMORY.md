# Desk Memory

Desk Memory stores a parcel’s current Windows monitor topology and the eligible top-level application-window geometry needed to restore it later. The data is local to the existing WorkParcel SQLite database; the migration is additive and does not recreate or delete the database.

Captured records contain stable executable/process/title/class identity, monitor identity, absolute and monitor-relative bounds, normal bounds, state, z-order rank, source DPI, and confidence metadata. Runtime HWNDs and PIDs are used only while a capture or restore operation is running. WorkParcel does not read window contents, screenshots, process memory, or other users’ windows.

Capture is available from Capture Current Setup and Pack Away. The review preview is generated from the saved geometry and includes negative virtual-screen coordinates, primary-monitor marking, work-area/DPI labels, and per-window tooltips. Parcel Details exposes layout-only restore, recapture, per-window edit/disable, monitor selection, state and bounds editing, disable/enable, and layout removal.

Restore uses deterministic executable/title/class/process signals. Exact and high-confidence matches can move automatically; possible and ambiguous matches require review or are skipped. Changed or missing monitors are mapped by exact display identity, role/geometry, nearest equivalent monitor, then primary fallback. Bounds are converted to the current work area, DPI-adjusted when needed, clamped to keep a title-bar portion visible, and never sent off-screen. Maximized windows are restored from their valid normal bounds before maximize.

## Real-window validation

The normal test suite uses fake monitors and fake windows. The opt-in manual test launches two disposable WinForms windows, persists their Desk Memory through WorkParcel’s store, moves them, closes them, reopens them through `ItemLaunchService`, and verifies both are restored through the native provider:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-DeskMemoryManualTest.ps1
```

The helper windows are temporary and are closed by the test. The script is intentionally separate from the normal test command because it changes real desktop window state.
