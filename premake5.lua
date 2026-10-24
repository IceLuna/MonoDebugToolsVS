-- Eagle Debug Tools: generates EagleDebugTools.sln (Visual Studio 2022)
--
--   Scripts\Setup.bat         (or: premake5 vs2022)
--
-- Building the solution produces bin\<Configuration>\EagleDebugTools.vsix
--
-- The projects are regular hand-written .csproj files (VSIX projects need build steps premake can't express),
-- so premake only creates the solution and fetches the external dependency.

-- mono/debugger-libs: the Mono soft-debugger client libraries used by the Mono debug adapter.
-- Pinned to the commit the adapter (Source/MonoDebugAdapter) was taken from.
local DEBUGGER_LIBS_URL    = "https://github.com/mono/debugger-libs.git"
local DEBUGGER_LIBS_COMMIT = "c0353072de4a6cd4813964a0536c48df3e9ceb4e"
local DEBUGGER_LIBS_DIR    = "External/debugger-libs"

local function run(cmd)
	local ok = os.execute(cmd)
	if not ok then
		error("Command failed: " .. cmd, 0)
	end
end

local function fetchDebuggerLibs()
	local dir = path.join(_MAIN_SCRIPT_DIR, DEBUGGER_LIBS_DIR)

	if not os.isdir(path.join(dir, ".git")) then
		print("Fetching mono/debugger-libs into " .. DEBUGGER_LIBS_DIR .. " ...")
		run('git clone --quiet "' .. DEBUGGER_LIBS_URL .. '" "' .. dir .. '"')
	end

	-- Make sure the pinned commit is checked out (fetch first if an older clone doesn't have it yet)
	local hasCommit = os.execute('git -C "' .. dir .. '" cat-file -e ' .. DEBUGGER_LIBS_COMMIT)
	if not hasCommit then
		run('git -C "' .. dir .. '" fetch --quiet origin')
	end
	run('git -C "' .. dir .. '" -c advice.detachedHead=false checkout --quiet ' .. DEBUGGER_LIBS_COMMIT)
end

if _ACTION ~= nil and _ACTION ~= "clean" then
	fetchDebuggerLibs()
end

workspace "EagleDebugTools"
	location "."
	configurations { "Debug", "Release" }
	startproject "EagleDebugTools"

-- The Visual Studio extension (VSIX). Packages the adapter and the item template.
externalproject "EagleDebugTools"
	location "Source/EagleDebugTools"
	uuid "1CBDE3A1-7340-4FC9-9562-39AB63C41CE0"
	kind "SharedLib"
	language "C#"
	-- The VSIX build packs the adapter's output, so the adapter has to be built first
	dependson { "MonoDebugAdapter", "EagleScriptTemplate" }

-- "Eagle Entity Script" item template (Add > New Item)
externalproject "EagleScriptTemplate"
	location "Source/EagleScriptTemplate"
	uuid "262D0C5F-CA62-4E65-8F0E-8DE844513707"
	kind "SharedLib"
	language "C#"

-- Debug Adapter Protocol server for the Mono soft debugger (mono-debug.exe)
externalproject "MonoDebugAdapter"
	location "Source/MonoDebugAdapter"
	uuid "C3A5E1B0-4D6F-4E8A-9B2C-7D1E0F3A5B69"
	kind "ConsoleApp"
	language "C#"
	dependson { "Mono.Debugger.Soft", "Mono.Debugging", "Mono.Debugging.Soft" }

group "External"
	externalproject "Mono.Debugger.Soft"
		location (DEBUGGER_LIBS_DIR .. "/Mono.Debugger.Soft")
		uuid "372E8E3E-29D5-4B4D-88A2-4711CD628C4E"
		kind "SharedLib"
		language "C#"

	externalproject "Mono.Debugging"
		location (DEBUGGER_LIBS_DIR .. "/Mono.Debugging")
		uuid "90C99ADB-7D4B-4EB4-98C2-40BD1B14C7D2"
		kind "SharedLib"
		language "C#"

	externalproject "Mono.Debugging.Soft"
		location (DEBUGGER_LIBS_DIR .. "/Mono.Debugging.Soft")
		uuid "DE40756E-57F6-4AF2-B155-55E3A88CAB8E"
		kind "SharedLib"
		language "C#"
		dependson { "Mono.Debugger.Soft", "Mono.Debugging" }
group ""
