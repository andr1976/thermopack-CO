# Resume Notes — CAPE-OPEN Error Handling

## Current State
Builds clean (0 warnings, 0 errors). Solution: `capeopen/ThermoPack.CapeOpen.sln`

## What was fixed in this session

### Recoverable faulted state (the critical fix)
- `_lock` changed from `static readonly` to `static` (replaceable)
- Added `[ThreadStatic] _heldLock` so each thread releases the exact lock it acquired
- `AcquireFortranLock()`: if `_faulted`, creates a NEW lock object and resets `_faulted = false`
  - The old lock is abandoned (held forever by the suspended dead thread)
  - New operations proceed with the new lock
- `ReleaseFortranLock()`: uses `_heldLock` (not `_lock`) to release the correct object
- Removed `CheckFaulted()` — recovery is automatic inside `AcquireFortranLock`
- Result: after a GERG-2008 failure, SRK/PR/etc. operations resume normally

### Dialog watcher (previous session)
- Concurrent watcher thread polls every 50ms for Intel Fortran dialog
- On detection: suspends worker thread via `SuspendThread()`, signals main thread
- Does NOT call `ShowWindow` after suspending (that caused a deadlock)
- The frozen dialog stays visible but unresponsive (user can't click OK → no ExitProcess)

### ECapeUser error properties (previous session)
- Added `name`, `description`, `source`, `scope`, `interfaceName`, `operationName`, `moreInfo`
- Added `SetError()` called before every COMException throw
- Re-entrancy protection with `_inCalcEquilibrium` flag
- `RecreateEngine` catches constructor exceptions when faulted
- All COMExceptions use proper HRESULT codes (ECapeUnknown, ECapeNoImpl)

## Remaining cosmetic issue
- Intel Fortran dialog appears as a frozen white window (thread suspended before rendering)
- Harmless but ugly — the process survives, user just ignores it
- Could add a small delay before suspending to let it render, but risks user clicking OK

## Key Files
- `capeopen/ThermoPack.Core/ThermoPackEngine.cs`
- `capeopen/ThermoPack.CapeOpen/ThermoPackPropertyPackageBase.cs`
