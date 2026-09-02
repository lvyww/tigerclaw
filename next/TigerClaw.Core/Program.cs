using System;
using System.Diagnostics;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal static class Program
    {
        private static readonly ManualResetEvent ShutdownEvent = new ManualResetEvent(false);
        private static Mutex _singleInstanceMutex;

        private static int Main(string[] args)
        {
#if DEBUG
            Console.Title = RuntimeConstants.CoreProcessName;
#endif
            bool tsfRegistered = TsfRegistrationGuard.IsTsfRegistered(out _);
            bool launchPublishedNativeHook = false;
            if (!tsfRegistered)
            {
                var startupLauncher = new ProcessLauncher();
                if (startupLauncher.HasPublishedNativeHook())
                {
                    launchPublishedNativeHook = true;
                }
                else
                {
                    string installScript = TsfRegistrationGuard.FindInstallScriptPath(AppDomain.CurrentDomain.BaseDirectory);
                    string message = TsfRegistrationGuard.BuildNotRegisteredMessage(installScript);
                    try
                    {
                        NativeMessageBox.ShowWarning(message, RuntimeConstants.ProductName);
                    }
                    catch
                    {
                        LogLine(message);
                    }
                    return 0;
                }
            }

            int currentPid = Process.GetCurrentProcess().Id;
            if (!ProcessInstanceGuard.EnsureNoOtherInstances(RuntimeConstants.CoreProcessName, currentPid))
            {
                return 0;
            }

            if (!TryAcquireSingleInstance())
            {
                return 0;
            }

            bool silent = HasArg(args, "--silent");
            bool withOverlay = !HasArg(args, "--without-overlay");
            LogLine("[Core] starting...");

            using (var heartbeat = new HeartbeatBroadcaster())
            using (var uiStatePublisher = new UiStatePublisher())
            using (var pipeServer = new PipeServer())
            {
                var launcher = new ProcessLauncher();
                using (var overlaySupervisor = new OverlayLaunchSupervisor(launcher))
                {
                    var state = new CoreRuntimeState();
                    state.Initialize();

                    var protocolHandler = new ProtocolHandler(command =>
                    {
                        switch (command)
                        {
                            case CoreUiCommand.ShowAddCi:
                                launcher.TryLaunchDialog(addCi: true);
                                break;
                            case CoreUiCommand.ShowMenu:
                                overlaySupervisor.RequestLaunch();
                                OverlayMenuSignal.Trigger();
                                break;
                            case CoreUiCommand.ShowConfig:
                                launcher.TryLaunchDialog(addCi: false);
                                break;
                            case CoreUiCommand.ExitCore:
                                ShutdownEvent.Set();
                                break;
                            default:
                                break;
                        }
                    }, state, uiStatePublisher);
                    pipeServer.MessageReceived += async (clientId, json) =>
                    {
                        string response = protocolHandler.HandleTransport(json, out bool publishUiAfterResponse);
                        if (!string.IsNullOrEmpty(response))
                        {
                            await pipeServer.SendResponseToClientAsync(clientId, response).ConfigureAwait(false);
                        }
                        if (publishUiAfterResponse)
                        {
                            protocolHandler.RequestDeferredUiStatePublish();
                        }
                    };

                    pipeServer.Start();
                    heartbeat.Start();
                    overlaySupervisor.Start(withOverlay);

                    if (launchPublishedNativeHook && !launcher.TryLaunchPublishedNativeHook())
                    {
                        const string message = "未检测到 TSF 注册，已切换为 Native Hook 启动模式，但 TigerClaw.exe 启动失败。";
                        try
                        {
                            NativeMessageBox.ShowWarning(message, RuntimeConstants.ProductName);
                        }
                        catch
                        {
                            LogLine(message);
                        }
                        return 0;
                    }

                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        ShutdownEvent.Set();
                    };

                    LogLine("[Core] heartbeat started.");
                    LogLine($"[Core] pipe={RuntimeConstants.TsfPipeName}");
                    LogLine($"[Core] overlay={(withOverlay ? "on" : "off")}");
                    if (silent)
                    {
                        LogLine("[Core] args include --silent.");
                    }
                    LogLine("[Core] press Ctrl+C to exit.");
                    ShutdownEvent.WaitOne();
                    protocolHandler.Dispose();
                }
            }

            SignalHookNativeExitNoThrow();
            Thread.Sleep(300);
            ProcessInstanceGuard.TryTerminateOtherInstances(RuntimeConstants.OverlayProcessName, currentPid);
            ProcessInstanceGuard.TryTerminateOtherInstances(RuntimeConstants.DialogProcessName, currentPid);
            ProcessInstanceGuard.TryTerminateOtherInstances(RuntimeConstants.SentenceProcessName, currentPid);
            ProcessInstanceGuard.TryTerminateOtherInstances(RuntimeConstants.HookNativeProcessName, currentPid);
            ProcessInstanceGuard.TryTerminateOtherInstances(RuntimeConstants.HookNativePublishedProcessName, currentPid);
            LogLine("[Core] stopped.");
            return 0;
        }

        private static void LogLine(string text)
        {
#if DEBUG
            Console.WriteLine(text);
#endif
        }

        private static bool TryAcquireSingleInstance()
        {
            string mutexName = @"Local\TigerClaw.Core.SingleInstance";
            bool createdNew = false;
            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            if (createdNew)
            {
                return true;
            }

            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        private static bool HasArg(string[] args, string expected)
        {
            if (args == null || string.IsNullOrEmpty(expected))
            {
                return false;
            }

            foreach (string arg in args)
            {
                if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SignalHookNativeExitNoThrow()
        {
            try
            {
                using (var exitEvent = EventWaitHandle.OpenExisting(RuntimeConstants.HookNativeExitEventName))
                {
                    exitEvent.Set();
                }
            }
            catch
            {
            }
        }
    }
}
