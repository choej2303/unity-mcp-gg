using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Adapts the existing TCP bridge into the transport abstraction.
    /// </summary>
    public class StdioTransportClient : IMcpTransportClient
    {
        private TransportState _state = TransportState.Disconnected("stdio");

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
                IpcHost.Start();
                _state = TransportState.Connected("stdio", details: $"Pipe: {IpcHost.PipeName}");
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
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

        public Task StopAsync()
        {
#if UNITY_EDITOR_WIN
            IpcHost.Stop();
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            UdsHost.Stop();
#else
            StdioBridgeHost.Stop();
#endif
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

        public Task SendNotificationAsync(string method, Newtonsoft.Json.Linq.JObject parameters)
        {
            McpLog.Warn("[Stdio] SendNotificationAsync is not implemented for Stdio transport.");
            return Task.CompletedTask;
        }

    }
}
