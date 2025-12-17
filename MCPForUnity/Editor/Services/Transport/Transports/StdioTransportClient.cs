using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR_WIN
using System.IO;
#endif

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Adapts the IPC/Stdio bridge into the transport abstraction.
    /// Manages multiple sessions if multiple clients connect via IPC.
    /// </summary>
    public class StdioTransportClient : IMcpTransportClient, IDisposable
    {
        private TransportState _state = TransportState.Disconnected("stdio");
        private readonly UnityMcpCommandExecutor _executor;
        
        // Track active sessions to dispose them on stop
        private readonly ConcurrentBag<McpSession> _sessions = new ConcurrentBag<McpSession>();
        private bool _disposed;

        public StdioTransportClient()
        {
            _executor = new UnityMcpCommandExecutor();
        }

#if UNITY_EDITOR_WIN
        public bool IsConnected => IpcHost.IsRunning;
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        public bool IsConnected => UdsHost.IsRunning;
#else
        public bool IsConnected => StdioBridgeHost.IsRunning;
#endif
        public string TransportName => "stdio";
        public TransportState State => _state;

        public Task<bool> StartAsync()
        {
            try
            {
#if UNITY_EDITOR_WIN
                IpcHost.OnClientConnected -= HandleNewClient; // ensure no double sub
                IpcHost.OnClientConnected += HandleNewClient;
                IpcHost.Start();
                _state = TransportState.Connected("stdio", details: $"Pipe: {IpcHost.PipeName}");
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                 // UdsHost refactoring is needed to support delegation, 
                 // for now leaving legacy logic or just starting simplified host?
                 // Since explicit requirement was windows, we focus there.
                 // Ideally UdsHost would be refactored similar to IpcHost.
                 // For now, let's assuming UdsHost is not fully refactored and might break if we used it like IpcHost.
                 // But sticking to the interface contract:
                UdsHost.Start();
                _state = TransportState.Connected("stdio", details: $"Sock: {UdsHost.SocketPath}");
#else
                StdioBridgeHost.StartAutoConnect();
                _state = TransportState.Connected("stdio", port: StdioBridgeHost.GetCurrentPort());
#endif
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _state = TransportState.Disconnected("stdio", ex.Message);
                return Task.FromResult(false);
            }
        }

        private void HandleNewClient(NamedPipeServerStream pipe)
        {
             // Create a new Transport -> Session stack for this connection
             var transport = new NamedPipeTransport(pipe);
             var session = new McpSession(transport, _executor);
             
             _sessions.Add(session);

             // Start listening on this transport
             // NamedPipeTransport.ConnectAsync expects pipe to be ready, which it is.
             // We fire and forget the connection loop
             _ = StartSessionAsync(session);
        }

        private async Task StartSessionAsync(McpSession session)
        {
            try
            {
                await session.ConnectAsync();
            }
            catch (Exception ex)
            {
                McpLog.Error($"[StdioTransportClient] Session error: {ex.Message}");
            }
            finally
            {
                // Remove from bag? ConcurrentBag doesn't support easy remove.
                // We'll clean up on StopAsync.
            }
        }

        public Task StopAsync()
        {
#if UNITY_EDITOR_WIN
            IpcHost.OnClientConnected -= HandleNewClient;
            IpcHost.Stop();
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            UdsHost.Stop();
#else
            StdioBridgeHost.Stop();
#endif
            
            // Dispose all sessions
            while (_sessions.TryTake(out var session))
            {
                session.Dispose();
            }

            _state = TransportState.Disconnected("stdio");
            return Task.CompletedTask;
        }

        public Task<bool> VerifyAsync()
        {
#if UNITY_EDITOR_WIN
            bool running = IpcHost.IsRunning;
            _state = running
                ? TransportState.Connected("stdio", details: $"Pipe: {IpcHost.PipeName}")
                : TransportState.Disconnected("stdio", "IPC Host not running");
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            bool running = UdsHost.IsRunning;
             _state = running
                ? TransportState.Connected("stdio", details: $"Sock: {UdsHost.SocketPath}")
                : TransportState.Disconnected("stdio", "UDS Host not running");
#else
            bool running = StdioBridgeHost.IsRunning;
             _state = running
                ? TransportState.Connected("stdio", port: StdioBridgeHost.GetCurrentPort())
                : TransportState.Disconnected("stdio", "Bridge not running");
#endif
            return Task.FromResult(running);
        }

        public Task SendNotificationAsync(string method, JObject parameters)
        {
            // We can iterate all sessions and valid ones send the notification
            // Or log warning if no sessions.
            bool sent = false;
            foreach (var session in _sessions)
            {
                if (session.IsConnected)
                {
                     // Fire and forget send
                     _ = session.SendNotificationAsync(method, parameters);
                     sent = true;
                }
            }

            if (!sent)
            {
                // McpLog.Warn("[Stdio] No active clients to send notification to.");
            }
            
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAsync();
        }
    }
}
