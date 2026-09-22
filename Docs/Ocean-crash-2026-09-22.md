# Ocean crash / 22 September 2026

The previous editor log records `d3d12: failed to Close a command list (8007000e)`,
`D3D12Fence::Wait ... Device removal`, repeated command-state/resource-barrier assertions,
then a native crash in `GfxTaskExecutorD3D12::AddRequiredResourceBarriers`.
The adapter is an NVIDIA RTX 3050 Laptop GPU, Unity 6000.2.8f1, split graphics jobs.
This identifies a D3D12 graphics failure; it does not establish that the ship AI or
NetCode warning caused the crash. The log also explicitly reports no exhaustion
of local/non-local graphics memory, so an ordinary VRAM shortage is not confirmed.

Windows graphics API is now explicitly Direct3D11 in ProjectSettings.asset.
A full editor restart is required. This is a workaround for the failing graphics
path, not a claim of a verified engine fix. A prolonged Ocean play test is still required.
An older Unity issue has the same top stack frames and was DX12-only, but its fixed
version predates this project: https://issuetracker.unity.com/issues/9049/crash-on-gfxtaskexecutord3d12addrequiredresourcebarriers-when-opening-a-specific-scene
Graphics API selection: https://docs.unity3d.com/6000.0/Documentation/Manual/UsingDX11GL3Features.html

NetCode remains 50 Hz with up to four separate catch-up steps. Its warning is not
disabled. Server initialization logs `[NFE timing]` with the actual settings and
graphics API; every ten seconds containing frames above the catch-up budget it
reports their count and longest frame. Repeated warnings after restart require
these measurements and a profiler capture, not another unsupported rate guess.

The unrelated repeated `Physics.ClosestPoint` warning from sword strikes was fixed:
non-convex mesh targets now use a surface raycast, after filtering damage receivers.
