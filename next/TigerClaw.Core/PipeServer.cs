using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal sealed class PipeServer : IDisposable
    {
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<string, NamedPipeServerStream> _clients = new ConcurrentDictionary<string, NamedPipeServerStream>();
        private CancellationTokenSource _cts;
        private bool _running;
        private int _clientCounter;

        public event Func<string, string, Task> MessageReceived;

        public void Start()
        {
            lock (_lock)
            {
                if (_running)
                {
                    return;
                }

                _running = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                _cts?.Cancel();
                foreach (var pair in _clients)
                {
                    try
                    {
                        pair.Value.Dispose();
                    }
                    catch
                    {
                    }
                }
                _clients.Clear();
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                string clientId = null;
                try
                {
                    pipe = CreateServerPipe();
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    clientId = $"client_{Interlocked.Increment(ref _clientCounter)}";
                    _clients[clientId] = pipe;
                    _ = Task.Run(() => HandleClientAsync(clientId, pipe, token));
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    break;
                }
                catch
                {
                    pipe?.Dispose();
                    if (clientId != null)
                    {
                        _clients.TryRemove(clientId, out _);
                    }
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
        }

        private static NamedPipeServerStream CreateServerPipe()
        {
            try
            {
                return new NamedPipeServerStream(
                    RuntimeConstants.TsfPipeShortName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous,
                    4096,
                    4096,
                    BuildPipeSecurity());
            }
            catch
            {
                return new NamedPipeServerStream(
                    RuntimeConstants.TsfPipeShortName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);
            }
        }

        private static PipeSecurity BuildPipeSecurity()
        {
            var security = new PipeSecurity();
            var rights = PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance;

            var currentUser = WindowsIdentity.GetCurrent()?.User;
            if (currentUser != null)
            {
                security.AddAccessRule(new PipeAccessRule(currentUser, rights, AccessControlType.Allow));
            }

            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new PipeAccessRule(localSystem, rights, AccessControlType.Allow));

            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(authenticatedUsers, rights, AccessControlType.Allow));

            var builtinUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new PipeAccessRule(builtinUsers, rights, AccessControlType.Allow));

            var allAppPackages = new SecurityIdentifier("S-1-15-2-1");
            security.AddAccessRule(new PipeAccessRule(allAppPackages, rights, AccessControlType.Allow));

            var allRestrictedAppPackages = new SecurityIdentifier("S-1-15-2-2");
            security.AddAccessRule(new PipeAccessRule(allRestrictedAppPackages, rights, AccessControlType.Allow));

            return security;
        }

        private async Task HandleClientAsync(string clientId, NamedPipeServerStream pipe, CancellationToken token)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    int bytesRead = await pipe.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    string content = sb.ToString();
                    int newlineIndex;
                    while ((newlineIndex = content.IndexOf('\n')) >= 0)
                    {
                        string line = content.Substring(0, newlineIndex).Trim();
                        if (!string.IsNullOrEmpty(line))
                        {
                            Func<string, string, Task> handler = MessageReceived;
                            if (handler != null)
                            {
                                await handler(clientId, line).ConfigureAwait(false);
                            }
                        }

                        content = content.Substring(newlineIndex + 1);
                    }

                    sb.Clear();
                    sb.Append(content);
                }
            }
            catch
            {
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                try
                {
                    pipe.Dispose();
                }
                catch
                {
                }
            }
        }

        public async Task<bool> SendResponseToClientAsync(string clientId, string response)
        {
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(response))
            {
                return false;
            }

            if (!_clients.TryGetValue(clientId, out NamedPipeServerStream pipe) || !pipe.IsConnected)
            {
                return false;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(response + "\n");
                await pipe.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
