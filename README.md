# Eagle Debug Tools

A Visual Studio 2022 extension for debugging Eagle Engine C# scripts: set breakpoints, step, and inspect variables
in your game's scripts while the Eagle Editor is running.

It adds:
* **Debug > Attach to Eagle** (`Ctrl+Shift+F7`), which attaches the debugger to a running Eagle editor
* **Add > New Item > Eagle Entity Script**, an item template for a new `Eagle.Entity` script
* **Tools > Options > Eagle Debug Tools**, the extension's settings

## Requirements
* Windows with **Visual Studio 2022** (17.3 or newer) and these Visual Studio Installer items:
  * Workload **.NET desktop development**
  * Workload **Visual Studio extension development**
  * Individual component **.NET Framework 4.7.2 targeting pack** (the debug adapter targets 4.7.2)
* **Git** on `PATH` (premake uses it to fetch `mono/debugger-libs`)
* Internet access for the first setup and build (premake download, NuGet packages)

## Building the extension
1. Run **`Scripts\Setup.bat`**.
   It downloads premake into `vendor\premake` if `premake5` isn't on your `PATH`, fetches `External\debugger-libs`,
   and generates `EagleDebugTools.sln`.
   If you already have premake: run `premake5 vs2022` in the repository folder instead.
2. Build the solution:
   * In Visual Studio: open `EagleDebugTools.sln`, select **Release**, then **Build > Build Solution**, or
   * from the command line: **`Scripts\Build.bat`** (Release; pass `Debug` for a debug build).
3. The installer is **`bin\Release\EagleDebugTools.vsix`**. Close Visual Studio and double-click it to install.

Run `Scripts\Setup.bat` again whenever `premake5.lua` changes. The `.sln` is generated, so it isn't committed.

## Using it
1. Start the Eagle Editor and open your project.
2. Open the project's C# solution in Visual Studio. The command only appears when a C# project is open.
3. Choose **Debug > Attach to Eagle** (`Ctrl+Shift+F7`), then set breakpoints and play.
4. **Debug > Stop Debugging** detaches and leaves the editor running, so you can attach again later.

### Options (Tools > Options > Eagle Debug Tools > General)
| Option | Default | |
|---|---|---|
| Connection Port | `2550` | Must match the `address=127.0.0.1:<port>` in `ScriptEngine::Init()` |
| Allow Property Evaluation | `True` | Lets the debugger run script code (property getters, `ToString()`, method calls in Watch) to display values. Getters only run for variables you expand. That code runs on the paused engine thread; if it calls into the engine and blocks, Visual Studio and the engine freeze together (see Troubleshooting). With it off, fields are still shown. |
