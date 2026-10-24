# MonoDebugAdapter

Debug Adapter Protocol server for the Mono soft debugger. Visual Studio's Debug Adapter Host starts it
(`mono-debug.exe`) and it connects to the Eagle engine's Mono debugger agent.

Based on [microsoft/vscode-mono-debug](https://github.com/microsoft/vscode-mono-debug) at commit
`b58eb0fd3bade8f50e37dda1ff4c6d5b3e02b964` (MIT, see `LICENSE.txt` and `ThirdPartyNotices.txt`).
It uses [mono/debugger-libs](https://github.com/mono/debugger-libs), which `premake5.lua` fetches into `External/`.

## Changes from upstream
Upstream was written for VS Code, which is more lenient than Visual Studio's Debug Adapter Host.
All changes are marked with `EAGLE:` comments.

| Problem | Fix |
|---|---|
| Every request was answered twice (VS: *Unable to locate a pending request...*) | `Protocol.cs`: a response is only sent once |
| Main thread reported as started twice (VS: *Attempting to add already-known thread*) | `MonoDebugSession.cs`: threads are announced once; exits only for known threads |
| Breakpoints returned without an `id` | `DebugSession.cs`, `MonoDebugSession.cs`: breakpoints carry an id |
| Stopping debugging didn't detach properly, so the engine couldn't be attached again (the second attach connected but breakpoints never bound) | `MonoDebugSession.cs`: `Disconnect` calls `Detach()` and waits for it to finish before replying, since Visual Studio ends the adapter right after. Only a proper detach makes the engine's debugger agent listen for a new debugger |
| Displaying values ran script code (property getters, `ToString()`) on the paused engine thread, which could freeze both VS and the engine | `MonoDebugSession.cs`: off unless the attach config sets `"allowPropertyEvaluation": true`; all evaluation waits time out after 5 s |
| Every property of every listed variable was evaluated on each stop, before anything was expanded | `MonoDebugSession.cs`: children are evaluated only when a variable is expanded |
