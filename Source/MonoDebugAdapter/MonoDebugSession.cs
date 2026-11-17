/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Net;
using Mono.Debugging.Client;


namespace VSCodeDebug
{
	public class MonoDebugSession : DebugSession
	{
		private const string MONO = "mono";
		private readonly string[] MONO_EXTENSIONS = new String[] {
			".cs", ".csx",
			".cake",
			".fs", ".fsi", ".ml", ".mli", ".fsx", ".fsscript",
			".hx",
			".vb"
		};
		private const int MAX_CHILDREN = 100;
		private const int MAX_CONNECTION_ATTEMPTS = 10;
		private const int CONNECTION_ATTEMPT_INTERVAL = 500;

		private AutoResetEvent _resumeEvent = new AutoResetEvent(false);
		private bool _debuggeeExecuting = false;
		private readonly object _lock = new object();
		private Mono.Debugging.Soft.SoftDebuggerSession _session;
		private volatile bool _debuggeeKilled = true;
		private ProcessInfo _activeProcess;
		private Mono.Debugging.Client.StackFrame _activeFrame;
		private long _nextBreakpointId = 0;
		private SortedDictionary<long, BreakEvent> _breakpoints;
		private List<Catchpoint> _catchpoints;
		private DebuggerSessionOptions _debuggerSessionOptions;

		private System.Diagnostics.Process _process;
		// EAGLE: handles store a function that produces the children, so a variable's children (and the property
		// getters they need) are only evaluated when Visual Studio actually asks for them (the variable is expanded).
		// Upstream evaluated every child of every listed variable up front, running many getters on each stop.
		private Handles<Func<ObjectValue[]>> _variableHandles;
		private Handles<Mono.Debugging.Client.StackFrame> _frameHandles;
		private ObjectValue _exception;
		// EAGLE: index of the frame that shows the "Exception" scope (the frame that threw it)
		private int _exceptionFrameIndex;
		// EAGLE: thread that stopped in the engine's exception handler, and how many frames on top of its stack belong to that handler
		private long _scriptExceptionThreadId = -1;
		private int _scriptExceptionHandlerFrames;
		private string _scriptExceptionText;
		// EAGLE: backtrace indices of the frames shown to VS, per thread. Reset on each stop
		private readonly Dictionary<long, List<int>> _visibleFrames = new Dictionary<long, List<int>>();
		private Dictionary<int, Thread> _seenThreads = new Dictionary<int, Thread>();
		private const int EVALUATION_WAIT_MS = 5000; // EAGLE: upper bound for waiting on a value evaluation
		private const int DETACH_TIMEOUT_MS = 5000; // EAGLE: upper bound for waiting on Detach() when disconnecting
		private bool _attachMode = false;
		// EAGLE: set while attaching waits for the debuggee to publish its port (see WaitForDebuggee). Cleared by Disconnect to cancel the wait
		private volatile bool _waitingForDebuggee = false;
		private const int DEBUGGEE_POLL_INTERVAL_MS = 250;
		private bool _terminated = false;
		private bool _stderrEOF = true;
		private bool _stdoutEOF = true;

		private dynamic exceptionOptionsFromDap;


		public MonoDebugSession() : base()
		{
			_variableHandles = new Handles<Func<ObjectValue[]>>();
			_frameHandles = new Handles<Mono.Debugging.Client.StackFrame>();
			_seenThreads = new Dictionary<int, Thread>();

			_debuggerSessionOptions = new DebuggerSessionOptions {
				EvaluationOptions = EvaluationOptions.DefaultOptions
			};

			_session = new Mono.Debugging.Soft.SoftDebuggerSession();
			_session.Breakpoints = new BreakpointStore();

			_breakpoints = new SortedDictionary<long, BreakEvent>();
			_catchpoints = new List<Catchpoint>();

			DebuggerLoggingService.CustomLogger = new CustomLogger();

			_session.ExceptionHandler = ex => {
				return true;
			};

			_session.LogWriter = (isStdErr, text) => {
			};

			_session.TargetStopped += (sender, e) => {
				Stopped();
				// EAGLE: the engine calls `Debugger.Break()` from `Eagle.RuntimeException` when a script doesn't handle an exception.
				// Report that as an exception, at the frame that threw it
				var scriptException = GetScriptException(e.Thread);
				if (scriptException != null) {
					_exception = scriptException.Instance;
					var visible = GetVisibleFrames(e.Thread);
					_exceptionFrameIndex = visible.Count > 0 ? visible[0] : 0;
					SendEvent(CreateStoppedEvent("exception", e.Thread, _scriptExceptionText ?? scriptException.Type));
				} else {
					SendEvent(CreateStoppedEvent("step", e.Thread));
				}
				_resumeEvent.Set();
			};

			_session.TargetHitBreakpoint += (sender, e) => {
				Stopped();
				SendEvent(CreateStoppedEvent("breakpoint", e.Thread));
				_resumeEvent.Set();
			};

			_session.TargetExceptionThrown += (sender, e) => {
				Stopped();
				var ex = DebuggerActiveException();
				if (ex != null) {
					_exception = ex.Instance;
					SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
				}
				_resumeEvent.Set();
			};

			_session.TargetUnhandledException += (sender, e) => {
				Stopped ();
				var ex = DebuggerActiveException();
				if (ex != null) {
					_exception = ex.Instance;
					SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
				}
				_resumeEvent.Set();
			};

			_session.TargetStarted += (sender, e) => {
				_activeFrame = null;
			};

			_session.TargetReady += (sender, e) => {
				SetExceptionBreakpointsFromDap(exceptionOptionsFromDap);
				_activeProcess = _session.GetProcesses().SingleOrDefault();
			};

			_session.TargetExited += (sender, e) => {

				DebuggerKill();

				_debuggeeKilled = true;

				Terminate("target exited");

				_resumeEvent.Set();
			};

			_session.TargetInterrupted += (sender, e) => {
				_resumeEvent.Set();
			};

			_session.TargetEvent += (sender, e) => {
			};

			// EAGLE: Mono.Debugging reports the main thread twice (VM start + ThreadStart event), and
			// Visual Studio's Debug Adapter Host treats a duplicate "started" thread event as fatal.
			_session.TargetThreadStarted += (sender, e) => {
				AnnounceThread((int)e.Thread.Id, e.Thread.Name);
			};

			_session.TargetThreadStopped += (sender, e) => {
				int tid = (int)e.Thread.Id;
				bool known;
				lock (_seenThreads) {
					known = _seenThreads.Remove(tid);
				}
				if (known) {
					SendEvent(new ThreadEvent("exited", tid));
				}
			};

			_session.OutputWriter = (isStdErr, text) => {
				SendOutput(isStdErr ? "stderr" : "stdout", text);
			};

			// Messages from System.Diagnostics.Debugger.Log() in the debuggee (Eagle's C# Log class sends script logs this way) go to Visual Studio's Output window
			_session.DebugWriter = (level, category, message) => {
				SendOutput("stdout", message);
			};
		}

		public override void Initialize(Response response, dynamic args)
		{
			OperatingSystem os = Environment.OSVersion;
			if (os.Platform != PlatformID.MacOSX && os.Platform != PlatformID.Unix && os.Platform != PlatformID.Win32NT) {
				SendErrorResponse(response, 3000, "Mono Debug is not supported on this platform ({_platform}).", new { _platform = os.Platform.ToString() }, true, true);
				return;
			}

			SendResponse(response, new Capabilities() {
				// This debug adapter does not need the configurationDoneRequest.
				supportsConfigurationDoneRequest = false,

				// This debug adapter does not support function breakpoints.
				supportsFunctionBreakpoints = false,

				// This debug adapter doesn't support conditional breakpoints.
				supportsConditionalBreakpoints = false,

				// This debug adapter does not support a side effect free evaluate request for data hovers.
				supportsEvaluateForHovers = false,

				supportsExceptionFilterOptions = true,
				exceptionBreakpointFilters = new dynamic[] {
					new { filter = "always", label = "All Exceptions", @default=false, supportsCondition=true, description="Break when an exception is thrown, even if it is caught later.",
						  conditionDescription = "Comma-separated list of exception types to break on"},
					new { filter = "uncaught", label = "Uncaught Exceptions", @default=false, supportsCondition=false, description="Breaks only on exceptions that are not handled."}
					}
			});

			// Mono Debug is ready to accept breakpoints immediately
			SendEvent(new InitializedEvent());
		}

		public override async void Launch(Response response, dynamic args)
		{
			_attachMode = false;

			SetExceptionBreakpoints(args.__exceptionOptions);

			// validate argument 'program'
			string programPath = getString(args, "program");
			if (programPath == null) {
				SendErrorResponse(response, 3001, "Property 'program' is missing or empty.", null);
				return;
			}
			programPath = ConvertClientPathToDebugger(programPath);
			if (!File.Exists(programPath) && !Directory.Exists(programPath)) {
				SendErrorResponse(response, 3002, "Program '{path}' does not exist.", new { path = programPath });
				return;
			}

			// validate argument 'cwd'
			var workingDirectory = (string)args.cwd;
			if (workingDirectory != null) {
				workingDirectory = workingDirectory.Trim();
				if (workingDirectory.Length == 0) {
					SendErrorResponse(response, 3003, "Property 'cwd' is empty.");
					return;
				}
				workingDirectory = ConvertClientPathToDebugger(workingDirectory);
				if (!Directory.Exists(workingDirectory)) {
					SendErrorResponse(response, 3004, "Working directory '{path}' does not exist.", new { path = workingDirectory });
					return;
				}
			}

			// validate argument 'runtimeExecutable'
			var runtimeExecutable = (string)args.runtimeExecutable;
			if (runtimeExecutable != null) {
				runtimeExecutable = runtimeExecutable.Trim();
				if (runtimeExecutable.Length == 0) {
					SendErrorResponse(response, 3005, "Property 'runtimeExecutable' is empty.");
					return;
				}
				runtimeExecutable = ConvertClientPathToDebugger(runtimeExecutable);
				if (!File.Exists(runtimeExecutable)) {
					SendErrorResponse(response, 3006, "Runtime executable '{path}' does not exist.", new { path = runtimeExecutable });
					return;
				}
			}


			// validate argument 'env'
			Dictionary<string, string> env = new Dictionary<string, string>();
			var environmentVariables = args.env;
			if (environmentVariables != null) {
				foreach (var entry in environmentVariables) {
					env.Add((string)entry.Name, (string)entry.Value);
				}
			}

			const string host = "127.0.0.1";
			int port = Utilities.FindFreePort(55555);

			string mono_path = runtimeExecutable;
			if (mono_path == null) {
				if (!Utilities.IsOnPath(MONO)) {
					SendErrorResponse(response, 3011, "Can't find runtime '{_runtime}' on PATH.", new { _runtime = MONO });
					return;
				}
				mono_path = MONO;     // try to find mono through PATH
			}


			var cmdLine = new List<String>();

			bool debug = !getBool(args, "noDebug", false);

			if (debug) {
				bool passDebugOptionsViaEnvironmentVariable = getBool(args, "passDebugOptionsViaEnvironmentVariable", false);

				if (passDebugOptionsViaEnvironmentVariable) {
					if (!env.ContainsKey("MONO_ENV_OPTIONS"))
						env["MONO_ENV_OPTIONS"] = $" --debug --debugger-agent=transport=dt_socket,server=y,address={host}:{port}";
					else
						env["MONO_ENV_OPTIONS"] = $" --debug --debugger-agent=transport=dt_socket,server=y,address={host}:{port} " + env["MONO_ENV_OPTIONS"];
				}
				else {
					cmdLine.Add("--debug");
					cmdLine.Add($"--debugger-agent=transport=dt_socket,server=y,address={host}:{port}");
				}
			}

			if (env.Count == 0) {
				env = null;
			}

			// add 'runtimeArgs'
			if (args.runtimeArgs != null) {
				string[] runtimeArguments = args.runtimeArgs.ToObject<string[]>();
				if (runtimeArguments != null && runtimeArguments.Length > 0) {
					cmdLine.AddRange(runtimeArguments);
				}
			}

			// add 'program'
			if (workingDirectory == null) {
				// if no working dir given, we use the direct folder of the executable
				workingDirectory = Path.GetDirectoryName(programPath);
				cmdLine.Add(Path.GetFileName(programPath));
			}
			else {
				// if working dir is given and if the executable is within that folder, we make the program path relative to the working dir
				cmdLine.Add(Utilities.MakeRelativePath(workingDirectory, programPath));
			}

			// add 'args'
			if (args.args != null) {
				string[] arguments = args.args.ToObject<string[]>();
				if (arguments != null && arguments.Length > 0) {
					cmdLine.AddRange(arguments);
				}
			}

			// what console?
			var console = getString(args, "console", null);
			if (console == null) {
				// continue to read the deprecated "externalConsole" attribute
				bool externalConsole = getBool(args, "externalConsole", false);
				if (externalConsole) {
					console = "externalTerminal";
				}
			}

			if (console == "externalTerminal" || console == "integratedTerminal") {

				cmdLine.Insert(0, mono_path);
				var termArgs = new {
					kind = console == "integratedTerminal" ? "integrated" : "external",
					title = "Node Debug Console",
					cwd = workingDirectory,
					args = cmdLine.ToArray(),
					env
				};

				var resp = await SendRequest("runInTerminal", termArgs);
				if (!resp.success) {
					SendErrorResponse(response, 3011, "Cannot launch debug target in terminal ({_error}).", new { _error = resp.message });
					return;
				}

			} else { // internalConsole

				_process = new System.Diagnostics.Process();
				_process.StartInfo.CreateNoWindow = true;
				_process.StartInfo.UseShellExecute = false;
				_process.StartInfo.WorkingDirectory = workingDirectory;
				_process.StartInfo.FileName = mono_path;
				_process.StartInfo.Arguments = Utilities.ConcatArgs(cmdLine.ToArray());

				_stdoutEOF = false;
				_process.StartInfo.RedirectStandardOutput = true;
				_process.OutputDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stdoutEOF = true;
					}
					SendOutput("stdout", e.Data);
				};

				_stderrEOF = false;
				_process.StartInfo.RedirectStandardError = true;
				_process.ErrorDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stderrEOF = true;
					}
					SendOutput("stderr", e.Data);
				};

				_process.EnableRaisingEvents = true;
				_process.Exited += (object sender, EventArgs e) => {
					Terminate("runtime process exited");
				};

				if (env != null) {
					// we cannot set the env vars on the process StartInfo because we need to set StartInfo.UseShellExecute to true at the same time.
					// instead we set the env vars on MonoDebug itself because we know that MonoDebug lives as long as a debug session.
					foreach (var entry in env) {
						System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);
					}
				}

				var cmd = string.Format("{0} {1}", mono_path, _process.StartInfo.Arguments);
				SendOutput("console", cmd);

				try {
					_process.Start();
					_process.BeginOutputReadLine();
					_process.BeginErrorReadLine();
				}
				catch (Exception e) {
					SendErrorResponse(response, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
					return;
				}
			}

			if (debug) {
				Connect(IPAddress.Parse(host), port);
			}

			SendResponse(response);

			if (_process == null && !debug) {
				// we cannot track mono runtime process so terminate this session
				Terminate("cannot track mono runtime");
			}
		}

		public override void Attach(Response response, dynamic args)
		{
			_attachMode = true;

			// EAGLE: by default never run code in the debuggee to display values (property getters, ToString(),
			// method calls in Watch). Such calls run on the paused game thread; if one calls into the engine and
			// blocks, both Visual Studio and the engine hang. Enable with "allowPropertyEvaluation": true.
			bool allowEval = getBool(args, "allowPropertyEvaluation", false);
			var evalOptions = _debuggerSessionOptions.EvaluationOptions;
			evalOptions.AllowTargetInvoke = allowEval;
			evalOptions.AllowMethodEvaluation = allowEval;
			evalOptions.AllowToStringCalls = allowEval;

			SetExceptionBreakpoints(args.__exceptionOptions);

			// validate argument 'address'
			var host = getString(args, "address");
			if (host == null) {
				SendErrorResponse(response, 3007, "Property 'address' is missing or empty.");
				return;
			}

			// validate argument 'port'.
			// EAGLE: or 'debuggerInfoFile' instead: the file in which the debuggee (an Eagle Editor that is still starting)
			// will publish its port, see WaitForDebuggee
			var port = getInt(args, "port", -1);
			string debuggerInfoFile = getString(args, "debuggerInfoFile");
			if (port == -1 && debuggerInfoFile == null) {
				SendErrorResponse(response, 3008, "Property 'port' is missing.");
				return;
			}

			IPAddress address = Utilities.ResolveIPAddress(host);
			if (address == null) {
				SendErrorResponse(response, 3013, "Invalid address '{address}'.", new { address = address });
				return;
			}

			if (port == -1) {
				// Visual Studio waits for this response, so reply now and connect once the port is known.
				// Visual Studio shows the session as running meanwhile: Stop Debugging cancels the wait
				SendResponse(response);
				StartWaitingForDebuggee(address, debuggerInfoFile, getInt(args, "processId", 0), getString(args, "debuggeeName", "the debuggee"));
				return;
			}

			Connect(address, port);

			SendResponse(response);
		}

		// EAGLE: waits on a background thread until the file is published by a running process, then connects to its port.
		// The file is the one Eagle's ScriptDebuggerInfo writes (see DebuggerInfoFile, shared with the extension).
		// The wait ends without attaching if Disconnect cancels it, or if the process `processId` (when given) exits first
		private void StartWaitingForDebuggee(IPAddress address, string debuggerInfoFile, int processId, string debuggeeName)
		{
			System.Diagnostics.Process process = null;
			if (processId > 0) {
				try {
					// Kept open, so that HasExited stays about this process even if its id gets reused
					process = System.Diagnostics.Process.GetProcessById(processId);
				}
				catch (ArgumentException) {
					SendOutput("stderr", Capitalize(debuggeeName) + " isn't running anymore.");
					Terminate("debuggee exited");
					return;
				}
				catch (InvalidOperationException) {
					// Can't watch it. Then only Stop Debugging ends the wait
				}
			}

			_waitingForDebuggee = true;
			SendOutput("console", "Waiting for " + debuggeeName + " to open the project, then attaching." + Environment.NewLine +
				"Stop Debugging (Shift+F5) stops waiting; the editor keeps running.");

			var thread = new System.Threading.Thread(() => WaitForDebuggee(address, debuggerInfoFile, process, debuggeeName)) {
				IsBackground = true,
				Name = "Wait for debuggee"
			};
			thread.Start();
		}

		private void WaitForDebuggee(IPAddress address, string debuggerInfoFile, System.Diagnostics.Process process, string debuggeeName)
		{
			try {
				while (_waitingForDebuggee) {
					var info = EagleDebugTools.DebuggerInfoFile.TryRead(debuggerInfoFile, deleteIfStale: false);
					if (info != null) {
						lock (_lock) {
							// Disconnect clears _session under this lock: after it, there's nothing to connect
							if (!_waitingForDebuggee || _session == null)
								return;
							_waitingForDebuggee = false;
							Connect(address, info.Port);
						}
						SendOutput("console", Capitalize(debuggeeName) + " has opened the project. Attached.");
						return;
					}

					if (process != null && HasExited(process)) {
						_waitingForDebuggee = false;
						SendOutput("stderr", Capitalize(debuggeeName) + " exited before opening the project.");
						Terminate("debuggee exited");
						return;
					}

					System.Threading.Thread.Sleep(DEBUGGEE_POLL_INTERVAL_MS);
				}
			}
			finally {
				if (process != null)
					process.Dispose();
			}
		}

		private static bool HasExited(System.Diagnostics.Process process)
		{
			try {
				return process.HasExited;
			}
			catch (Exception) {
				return false; // Can't tell (no access): keep waiting
			}
		}

		private static string Capitalize(string text)
		{
			return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
		}

		public override void Disconnect(Response response, dynamic args)
		{
			// EAGLE: stops waiting for the debuggee, if attaching still waits (see WaitForDebuggee)
			_waitingForDebuggee = false;

			if (_attachMode) {

				// EAGLE: detach properly so the engine can be attached again.
				// The Mono debugger agent only starts listening for a new debugger after it receives VM_DISPOSE
				// (sent by Detach()). If the connection just drops, the engine resumes but can never be attached again
				// (a new attach "succeeds" but breakpoints never bind). Detach() only queues the work on a thread pool
				// thread, so wait for it to finish before replying: Visual Studio ends this process right after.
				// _session is cleared first, under the lock, so that the TargetExited handler triggered by the
				// disconnect doesn't call DebuggerKill() -> Exit() on it, which would terminate the engine.
				Mono.Debugging.Soft.SoftDebuggerSession session;
				lock (_lock) {
					session = _session;
					_session = null;
					if (session != null) {
						_debuggeeExecuting = true;
						_breakpoints.Clear();
						session.Breakpoints.Clear();
					}
				}

				if (session != null && session.IsConnected) {
					// EAGLE: resume before detaching. While the VM is suspended, the Mono debugger agent keeps every object
					// it reports (the thread, the exception, values shown in VS) alive in a GC root table, and only VM_RESUME
					// clears it; VM_DISPOSE resumes the VM but leaves the table as is. Those objects then outlive the script
					// AppDomain, and after recompiling scripts (domain unload) the engine crashes or freezes inside script code.
					// Breakpoints are removed first, at the protocol level (synchronously), so that nothing stops the VM again
					// between the resume and the detach.
					try {
						var vm = session.VirtualMachine;
						if (vm != null) {
							vm.ClearAllBreakpoints();
							vm.Resume();
						}
					}
					catch (Exception) {
						// Not suspended (VMNotSuspendedException), or the connection is already gone: nothing to release
					}

					try {
						session.Detach();
						var timer = System.Diagnostics.Stopwatch.StartNew();
						while (session.IsConnected && timer.ElapsedMilliseconds < DETACH_TIMEOUT_MS) {
							System.Threading.Thread.Sleep(10);
						}
					}
					catch (Exception) {
						// ignore, the connection is closed when this process exits
					}
				}

			} else {
				// Let's not leave dead Mono processes behind...
				if (_process != null) {
					_process.Kill();
					_process = null;
				} else {
					PauseDebugger();
					DebuggerKill();

					while (!_debuggeeKilled) {
						System.Threading.Thread.Sleep(10);
					}
				}
			}

			SendResponse(response);
		}

		public override void Continue(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.Continue();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void Next(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.NextLine();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void StepIn(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.StepLine();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void StepOut(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.Finish();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void Pause(Response response, dynamic args)
		{
			SendResponse(response);
			if (_waitingForDebuggee) {
				// EAGLE: nothing to pause yet
				SendOutput("console", "Can't break yet: still waiting for the project to be opened.");
				return;
			}
			PauseDebugger();
		}

		public override void SetExceptionBreakpoints(Response response, dynamic args)
		{
			if (args.filterOptions != null)
			{
				if (_activeProcess != null)
					SetExceptionBreakpointsFromDap(args.filterOptions);
				else
					exceptionOptionsFromDap = args.filterOptions;
			}
			else
				SetExceptionBreakpoints(args.exceptionOptions);
			SendResponse(response);
		}

		public override void SetBreakpoints(Response response, dynamic args)
		{
			string path = null;
			if (args.source != null) {
				string p = (string)args.source.path;
				if (p != null && p.Trim().Length > 0) {
					path = p;
				}
			}
			if (path == null) {
				SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or misformed", null, false, true);
				return;
			}
			path = ConvertClientPathToDebugger(path);

			if (!HasMonoExtension(path)) {
				// we only support breakpoints in files mono can handle
				SendResponse(response, new SetBreakpointsResponseBody());
				return;
			}

			var clientLines = args.lines.ToObject<int[]>();
			HashSet<int> lin = new HashSet<int>();
			for (int i = 0; i < clientLines.Length; i++) {
				lin.Add(ConvertClientLineToDebugger(clientLines[i]));
			}

			// find all breakpoints for the given path and remember their id and line number
			var bpts = new List<Tuple<int, int>>();
			foreach (var be in _breakpoints) {
				var bp = be.Value as Mono.Debugging.Client.Breakpoint;
				if (bp != null && bp.FileName == path) {
					bpts.Add(new Tuple<int,int>((int)be.Key, (int)bp.Line));
				}
			}

			HashSet<int> lin2 = new HashSet<int>();
			foreach (var bpt in bpts) {
				if (lin.Contains(bpt.Item2)) {
					lin2.Add(bpt.Item2);
				}
				else {
					// Program.Log("cleared bpt #{0} for line {1}", bpt.Item1, bpt.Item2);

					BreakEvent b;
					if (_breakpoints.TryGetValue(bpt.Item1, out b)) {
						_breakpoints.Remove(bpt.Item1);
						_session.Breakpoints.Remove(b);
					}
				}
			}

			for (int i = 0; i < clientLines.Length; i++) {
				var l = ConvertClientLineToDebugger(clientLines[i]);
				if (!lin2.Contains(l)) {
					var id = _nextBreakpointId++;
					_breakpoints.Add(id, _session.Breakpoints.Add(path, l));
					// Program.Log("added bpt #{0} for line {1}", id, l);
				}
			}

			var breakpoints = new List<Breakpoint>();
			foreach (var l in clientLines) {
				// EAGLE: report the adapter's breakpoint id (VS Debug Adapter Host expects one)
				int debuggerLine = ConvertClientLineToDebugger(l);
				int bpId = 0;
				foreach (var be in _breakpoints) {
					var bp = be.Value as Mono.Debugging.Client.Breakpoint;
					if (bp != null && bp.FileName == path && bp.Line == debuggerLine) {
						bpId = (int)be.Key + 1;
						break;
					}
				}
				breakpoints.Add(new Breakpoint(true, l, bpId));
			}

			SendResponse(response, new SetBreakpointsResponseBody(breakpoints));
		}

		public override void StackTrace(Response response, dynamic args)
		{
			// EAGLE: honor paging. `levels` 0 (or missing) means all frames
			int startFrame = Math.Max(0, getInt(args, "startFrame", 0));
			int maxLevels = getInt(args, "levels", 0);
			if (maxLevels <= 0)
				maxLevels = int.MaxValue;
			int threadReference = getInt(args, "threadId", 0);

			WaitForSuspend();

			ThreadInfo thread = DebuggerActiveThread();
			if (thread.Id != threadReference) {
				// Program.Log("stackTrace: unexpected: active thread should be the one requested");
				thread = FindThread(threadReference);
				if (thread != null) {
					thread.SetActive();
				}
			}

			var stackFrames = new List<StackFrame>();
			int totalFrames = 0;

			var bt = thread == null ? null : thread.Backtrace;
			if (bt != null && bt.FrameCount >= 0) {

				// EAGLE: skip [DebuggerHidden] frames (engine plumbing) and the engine's exception handler frames
				var visibleFrames = GetVisibleFrames(thread);
				totalFrames = visibleFrames.Count;

				int endFrame = (int)Math.Min((long)startFrame + maxLevels, totalFrames);
				for (var v = startFrame; v < endFrame; v++) {

					var frame = bt.GetFrame(visibleFrames[v]);

					string path = frame.SourceLocation.FileName;

					var hint = "subtle";
					Source source = null;
					if (!string.IsNullOrEmpty(path)) {
						string sourceName = Path.GetFileName(path);
						if (!string.IsNullOrEmpty(sourceName)) {
							if (File.Exists(path)) {
								source = new Source(sourceName, ConvertDebuggerPathToClient(path), 0, "normal");
								hint = "normal";
							} else {
								source = new Source(sourceName, null, 1000, "deemphasize");
							}
						}
					}

					var frameHandle = _frameHandles.Create(frame);
					string name = frame.SourceLocation.MethodName;
					int line = frame.SourceLocation.Line;
					stackFrames.Add(new StackFrame(frameHandle, name, source, ConvertDebuggerLineToClient(line), 0, hint));
				}
			}

			SendResponse(response, new StackTraceResponseBody(stackFrames, totalFrames));
		}

		public override void Source(Response response, dynamic arguments) {
			SendErrorResponse(response, 1020, "No source available");
		}

		public override void Scopes(Response response, dynamic args) {

			int frameId = getInt(args, "frameId", 0);
			var frame = _frameHandles.Get(frameId, null);

			var scopes = new List<Scope>();

			if (frame.Index == _exceptionFrameIndex && _exception != null) {
				var exceptionValues = new ObjectValue[] { _exception };
				scopes.Add(new Scope("Exception", _variableHandles.Create(() => exceptionValues)));
			}

			var locals = new[] { frame.GetThisReference() }.Concat(frame.GetParameters()).Concat(frame.GetLocalVariables()).Where(x => x != null).ToArray();
			if (locals.Length > 0) {
				scopes.Add(new Scope("Local", _variableHandles.Create(() => locals)));
			}

			SendResponse(response, new ScopesResponseBody(scopes));
		}

		public override void Variables(Response response, dynamic args)
		{
			int reference = getInt(args, "variablesReference", -1);
			if (reference == -1) {
				SendErrorResponse(response, 3009, "variables: property 'variablesReference' is missing", null, false, true);
				return;
			}

			WaitForSuspend();
			var variables = new List<Variable>();

			Func<ObjectValue[]> getChildren;
			if (_variableHandles.TryGet(reference, out getChildren)) {
				ObjectValue[] children;
				try {
					children = getChildren();
				}
				catch (Exception) {
					children = null;
				}
				if (children != null && children.Length > 0) {

					bool more = false;
					if (children.Length > MAX_CHILDREN) {
						children = children.Take(MAX_CHILDREN).ToArray();
						more = true;
					}

					if (children.Length < 20) {
						// Wait for all values at once.
						// EAGLE: never block the request thread forever on an evaluation
						WaitHandle.WaitAll(children.Select(x => x.WaitHandle).ToArray(), EVALUATION_WAIT_MS);
						foreach (var v in children) {
							variables.Add(CreateVariable(v));
						}
					}
					else {
						foreach (var v in children) {
							v.WaitHandle.WaitOne(EVALUATION_WAIT_MS); // EAGLE: bounded wait
							variables.Add(CreateVariable(v));
						}
					}

					if (more) {
						variables.Add(new Variable("...", null, null));
					}
				}
			}

			SendResponse(response, new VariablesResponseBody(variables));
		}

		public override void Threads(Response response, dynamic args)
		{
			var threads = new List<Thread>();
			var process = _activeProcess;
			if (process != null) {
				Dictionary<int, Thread> d;
				lock (_seenThreads) {
					d = new Dictionary<int, Thread>(_seenThreads);
				}
				foreach (var t in process.GetThreads()) {
					int tid = (int)t.Id;
					d[tid] = new Thread(tid, t.Name);
				}
				threads = d.Values.ToList();
			}
			SendResponse(response, new ThreadsResponseBody(threads));
		}

		public override void Evaluate(Response response, dynamic args)
		{
			string error = null;

			var expression = getString(args, "expression");
			if (expression == null) {
				error = "expression missing";
			} else {
				int frameId = getInt(args, "frameId", -1);
				var frame = _frameHandles.Get(frameId, null);
				if (frame != null) {
					if (frame.ValidateExpression(expression)) {
						var val = frame.GetExpressionValue(expression, _debuggerSessionOptions.EvaluationOptions);
						// EAGLE: bounded wait; report a timeout instead of hanging Visual Studio
						if (!val.WaitHandle.WaitOne(EVALUATION_WAIT_MS)) {
							SendErrorResponse(response, 3014, "Evaluate request failed ({_reason}).", new { _reason = "evaluation timed out" });
							return;
						}

						var flags = val.Flags;
						if (flags.HasFlag(ObjectValueFlags.Error) || flags.HasFlag(ObjectValueFlags.NotSupported)) {
							error = val.DisplayValue;
							if (error.IndexOf("reference not available in the current evaluation context") > 0) {
								error = "not available";
							}
						}
						else if (flags.HasFlag(ObjectValueFlags.Unknown)) {
							error = "invalid expression";
						}
						else if (flags.HasFlag(ObjectValueFlags.Object) && flags.HasFlag(ObjectValueFlags.Namespace)) {
							error = "not available";
						}
						else {
							int handle = 0;
							if (val.HasChildren) {
								handle = _variableHandles.Create(() => val.GetAllChildren()); // EAGLE: lazy
							}
							SendResponse(response, new EvaluateResponseBody(val.DisplayValue, handle));
							return;
						}
					}
					else {
						error = "invalid expression";
					}
				}
				else {
					error = "no active stackframe";
				}
			}
			SendErrorResponse(response, 3014, "Evaluate request failed ({_reason}).", new { _reason = error } );
		}

		//---- private ------------------------------------------

		private void SetExceptionBreakpointsFromDap(dynamic exceptionOptions)
		{
			if (exceptionOptions != null) {
				var exceptions = exceptionOptions.ToObject<dynamic[]>();
				for (int i = 0; i < exceptions.Length; i++) {
					var exception = exceptions[i];

					bool caught = exception.filterId == "always" ? true : false;
					if (exception.condition != null && exception.condition != "") {
						string[] conditionNames = exception.condition.ToString().Split(',');
						foreach (var conditionName in conditionNames)
							_session.EnableException(conditionName, caught);
					}
					else {
						_session.EnableException("System.Exception", caught);
					}
				}
			}
		}
		private void SetExceptionBreakpoints(dynamic exceptionOptions)
		{
			if (exceptionOptions != null) {

				// clear all existig catchpoints
				foreach (var cp in _catchpoints) {
					_session.Breakpoints.Remove(cp);
				}
				_catchpoints.Clear();

				var exceptions = exceptionOptions.ToObject<dynamic[]>();
				for (int i = 0; i < exceptions.Length; i++) {

					var exception = exceptions[i];

					string exName = null;
					string exBreakMode = exception.breakMode;

					if (exception.path != null) {
						var paths = exception.path.ToObject<dynamic[]>();
						var path = paths[0];
						if (path.names != null) {
							var names = path.names.ToObject<dynamic[]>();
							if (names.Length > 0) {
								exName = names[0];
							}
						}
					}

					if (exName != null && exBreakMode == "always") {
						_catchpoints.Add(_session.Breakpoints.AddCatchpoint(exName));
					}
				}
			}
		}

		private void SendOutput(string category, string data) {
			if (!String.IsNullOrEmpty(data)) {
				if (data[data.Length-1] != '\n') {
					data += '\n';
				}
				SendEvent(new OutputEvent(category, data));
			}
		}

		private void Terminate(string reason) {
			if (!_terminated) {

				// wait until we've seen the end of stdout and stderr
				for (int i = 0; i < 100 && (_stdoutEOF == false || _stderrEOF == false); i++) {
					System.Threading.Thread.Sleep(100);
				}

				SendEvent(new TerminatedEvent());

				_terminated = true;
				_process = null;
			}
		}

		private StoppedEvent CreateStoppedEvent(string reason, ThreadInfo ti, string text = null)
		{
			// EAGLE: make sure VS knows the thread before it is told the thread stopped
			AnnounceThread((int)ti.Id, ti.Name);
			return new StoppedEvent((int)ti.Id, reason, text);
		}

		// EAGLE: sends a "started" thread event only the first time a thread id is seen
		private void AnnounceThread(int tid, string name)
		{
			lock (_seenThreads) {
				if (_seenThreads.ContainsKey(tid)) {
					return;
				}
				_seenThreads[tid] = new Thread(tid, name);
			}
			SendEvent(new ThreadEvent("started", tid));
		}

		private ThreadInfo FindThread(int threadReference)
		{
			if (_activeProcess != null) {
				foreach (var t in _activeProcess.GetThreads()) {
					if (t.Id == threadReference) {
						return t;
					}
				}
			}
			return null;
		}

		private void Stopped()
		{
			_exception = null;
			_exceptionFrameIndex = 0;
			_scriptExceptionThreadId = -1;
			_scriptExceptionHandlerFrames = 0;
			_scriptExceptionText = null;
			lock (_visibleFrames) {
				_visibleFrames.Clear();
			}
			_variableHandles.Reset();
			_frameHandles.Reset();
		}

		private Variable CreateVariable(ObjectValue v)
		{
			var dv = v.DisplayValue;
			if (dv.Length > 1 && dv [0] == '{' && dv [dv.Length - 1] == '}') {
				dv = dv.Substring (1, dv.Length - 2);
			}
			// EAGLE: lazy, children are evaluated when the variable is expanded
			return new Variable(v.Name, dv, v.TypeName, v.HasChildren ? _variableHandles.Create(() => v.GetAllChildren()) : 0);
		}

		private bool HasMonoExtension(string path)
		{
			foreach (var e in MONO_EXTENSIONS) {
				if (path.EndsWith(e)) {
					return true;
				}
			}
			return false;
		}

		private static bool getBool(dynamic container, string propertyName, bool dflt = false)
		{
			try {
				return (bool)container[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static int getInt(dynamic container, string propertyName, int dflt = 0)
		{
			try {
				return (int)container[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static string getString(dynamic args, string property, string dflt = null)
		{
			var s = (string)args[property];
			if (s == null) {
				return dflt;
			}
			s = s.Trim();
			if (s.Length == 0) {
				return dflt;
			}
			return s;
		}

		//-----------------------

		private void WaitForSuspend()
		{
			if (_debuggeeExecuting) {
				_resumeEvent.WaitOne();
				_debuggeeExecuting = false;
			}
		}

		private ThreadInfo DebuggerActiveThread()
		{
			lock (_lock) {
				return _session == null ? null : _session.ActiveThread;
			}
		}

		private Backtrace DebuggerActiveBacktrace() {
			var thr = DebuggerActiveThread();
			return thr == null ? null : thr.Backtrace;
		}

		private Mono.Debugging.Client.StackFrame DebuggerActiveFrame() {
			if (_activeFrame != null)
				return _activeFrame;

			var bt = DebuggerActiveBacktrace();
			if (bt != null)
				return _activeFrame = bt.GetFrame(0);

			return null;
		}

		// EAGLE: the engine (Eagle-Scripts/Source/RuntimeException.cs) stops the debugger for exceptions that scripts don't handle from:
		//  - `BreakOnUnhandled`: an exception filter. The stack still has the frames that threw the exception (Mono builds the call stack
		//    from the throw site while a filter runs), so those frames are shown and the handler's own frames are skipped.
		//  - `OnException`: called after the stack was unwound, for exceptions that didn't pass through that filter. Shown as is.
		// Both have the exception in their `exception` parameter.
		private const string ScriptExceptionType = "Eagle.RuntimeException";
		private const string ScriptExceptionFilterMethod = "BreakOnUnhandled";
		private const string ScriptExceptionHandlerMethod = "OnException";
		private const int ScriptExceptionMaxDepth = 4; // `Debugger.Break()` frames can be above the handler

		private ExceptionInfo GetScriptException(ThreadInfo thread)
		{
			try {
				var bt = thread == null ? null : thread.Backtrace;
				if (bt == null)
					return null;

				int count = Math.Min(bt.FrameCount, ScriptExceptionMaxDepth);
				for (int i = 0; i < count; i++) {
					var frame = bt.GetFrame(i);
					if (frame.FullTypeName != ScriptExceptionType)
						continue;

					string method = frame.SourceLocation.MethodName ?? string.Empty;
					bool bFilter = method.EndsWith("." + ScriptExceptionFilterMethod);
					if (!bFilter && !method.EndsWith("." + ScriptExceptionHandlerMethod))
						continue;

					var exception = frame.GetParameters().FirstOrDefault(p => p.Name == "exception");
					if (exception == null || exception.IsNull)
						return null;

					_scriptExceptionThreadId = thread.Id;
					_scriptExceptionHandlerFrames = bFilter ? i + 1 : i; // Keep `OnException` itself: there's nothing below it to show
					var info = new ExceptionInfo(exception);
					_scriptExceptionText = GetExceptionText(frame, info);
					return info;
				}
			}
			catch (Exception) {
				// Not being able to inspect the frames just means it's reported as a normal stop
			}
			return null;
		}

		// "Type: message" for the stop notification. `Exception.Message` is a property, and calling it is disabled
		// (see `allowPropertyEvaluation`), so this reads the field behind it. That field is empty for most exceptions
		// thrown by the runtime (like NullReferenceException), which then show just their type
		private string GetExceptionText(Mono.Debugging.Client.StackFrame frame, ExceptionInfo exception)
		{
			string type = exception.Type;
			try {
				var options = _debuggerSessionOptions.EvaluationOptions.Clone();
				options.AllowTargetInvoke = false;
				options.AllowMethodEvaluation = false;
				options.AllowToStringCalls = false;
				var value = frame.GetExpressionValue("((System.Exception)exception)._message", options);
				if (value != null && !value.IsNull && !value.IsError && !value.IsEvaluating && !string.IsNullOrEmpty(value.Value)) {
					string message = value.Value;
					if (message.Length >= 2 && message[0] == '"' && message[message.Length - 1] == '"')
						message = message.Substring(1, message.Length - 2);
					if (message.Length > 0)
						return type + ": " + message;
				}
			}
			catch (Exception) {
				// Just the type then
			}
			return type;
		}

		// Backtrace indices of the frames to show for `thread`
		private List<int> GetVisibleFrames(ThreadInfo thread)
		{
			lock (_visibleFrames) {
				List<int> result;
				if (_visibleFrames.TryGetValue(thread.Id, out result))
					return result;

				result = new List<int>();
				var bt = thread.Backtrace;
				int frameCount = bt == null ? 0 : bt.FrameCount;
				int first = thread.Id == _scriptExceptionThreadId ? Math.Min(_scriptExceptionHandlerFrames, frameCount) : 0;
				for (int i = first; i < frameCount; i++) {
					if (!bt.GetFrame(i).IsDebuggerHidden)
						result.Add(i);
				}

				// Never hide everything
				if (result.Count == 0) {
					for (int i = 0; i < frameCount; i++)
						result.Add(i);
				}

				_visibleFrames[thread.Id] = result;
				return result;
			}
		}

		private ExceptionInfo DebuggerActiveException() {
			var bt = DebuggerActiveBacktrace();
			return bt == null ? null : bt.GetFrame(0).GetException();
		}

		private void Connect(IPAddress address, int port)
		{
			lock (_lock) {

				_debuggeeKilled = false;

				var args0 = new Mono.Debugging.Soft.SoftDebuggerConnectArgs(string.Empty, address, port) {
					MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
					TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
				};

				_session.Run(new Mono.Debugging.Soft.SoftDebuggerStartInfo(args0), _debuggerSessionOptions);

				_debuggeeExecuting = true;
			}
		}

		private void PauseDebugger()
		{
			lock (_lock) {
				if (_session != null && _session.IsRunning)
					_session.Stop();
			}
		}

		private void DebuggerKill()
		{
			lock (_lock) {
				if (_session != null) {

					_debuggeeExecuting = true;

					if (!_session.HasExited)
						_session.Exit();

					_session.Dispose();
					_session = null;
				}
			}
		}
	}
}
