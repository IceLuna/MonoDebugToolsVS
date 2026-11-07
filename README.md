# Eagle Debug Tools

A Visual Studio 2022 extension for debugging Eagle Engine C# scripts: set breakpoints, step, and inspect variables
in your game's scripts while the Eagle Editor or a game build is running.

It adds:
* **F5 attaches to the Eagle Editor** when the startup project is an Eagle scripts project
* **Debug > Attach to Eagle Game...**, which attaches to a running game (games built without `EG_RELEASE`)
* Script logs (`Eagle.Log()`) in Visual Studio's **Output** window, and a stop in the debugger when a script throws an exception
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
1. Open the project's C# solution in Visual Studio.
2. Press **F5** (*Attach to Eagle*). It builds the scripts and attaches to the Eagle Editor that has the project open;
   the editor reloads the rebuilt scripts by itself. Then set breakpoints and play.
   * If no editor has the project open, an info bar appears instead: **Run Eagle Editor** starts the editor with the project
     (found through the `.egproj` file association, see the engine's `Win-SetupFileAssociation.bat`). Press F5 again once it has opened the project.
3. **Debug > Stop Debugging** detaches and leaves the editor running, so you can attach again later.

F5 attaches when the startup project is the Eagle scripts project: a project in a folder with a `.egproj` file (or below it)
whose assembly name is the Eagle project's name. For other startup projects F5 works as usual.

**Several editors:** each editor picks its own free port and writes it to `<Project>/Cache/Scripts/Debugger.yaml`,
which it keeps locked while the project is open. Visual Studio attaches to the editor that has the solution's project open.

**Script logs and exceptions:** `Eagle.Log()` messages also appear in Visual Studio's **Output** window while attached.
When a script throws an exception, the debugger stops in `Eagle.RuntimeException.OnException`.

### Debugging a game
Games can be debugged unless they were built with a Release build of Eagle (`EG_RELEASE`). Non-Release game builds
also get the scripts' `.pdb` files, which the debugger needs.
1. Start the game.
2. Choose **Debug > Attach to Eagle Game...**. If exactly one game of the solution's project is running, it attaches to it;
   otherwise it lists the running games (the solution's project first) to pick from.
   * If you press F5 while no editor has the project open but a game of the project is running, the info bar also offers
     **Attach to Running Game**.
3. **Debug > Stop Debugging** detaches and leaves the game running.

A game starts its first scene right away, so startup code (for example `OnCreate`) has usually run before you can attach.
To debug it, start the game with **`--wait-for-debugger`**: it waits at the splash screen until a debugger attaches.

Breakpoints only hit where the game's scripts match your source code. Before attaching to a game of the solution's project,
the extension warns if the scripts changed since they were last built, or if the game was built before the scripts' last build
(its `<Project>.pdb` differs from `Binaries\<Project>.pdb`). Rebuild the scripts, then the game.

Each running game picks its own free port and writes it to `%LOCALAPPDATA%\Eagle\Debugger\Games\Game-<ProcessId>.yaml`,
locked while the game runs. Files left behind by crashed games are removed automatically.

### Options (Tools > Options > Eagle Debug Tools > General)
| Option | Default | |
|---|---|---|
| Build Before Attaching | `True` | F5 builds the scripts project before attaching. |
| Allow Property Evaluation | `True` | Lets the debugger run script code (property getters, `ToString()`, method calls in Watch) to display values. Getters only run for variables you expand. That code runs on the paused engine thread; if it calls into the engine and blocks, Visual Studio and the engine freeze together. With it off, fields are still shown. |
