using System;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Implements the standard MCP HTTP Transport using Server-Sent Events (SSE).
    /// Acts as a legacy adapter (facade) for the new McpSession architecture.
    /// </summary>
    public class SseTransportClient : IMcpTransportClient, IDisposable
    {
        private const string TransportDisplayName = "http (sse)";

        private readonly SseTransport _transport;
        private readonly McpSession _session;
        private readonly UnityMcpCommandExecutor _executor;
        
        // We keep track of connection state for the interface
        private TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private bool _disposed;

        public SseTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            // Note: toolDiscoveryService is not used directly here anymore, 
            // as command execution is handled by UnityMcpCommandExecutor (which uses CommandRegistry).
            
            _transport = new SseTransport();
            _executor = new UnityMcpCommandExecutor();
            _session = new McpSession(_transport, _executor);

            _session.OnSessionError += error =>
            {
                _state = _state.WithError(error);
            };
        }

        public bool IsConnected => _session.IsConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;

        public async Task<bool> StartAsync()
        {
            try
            {
                // Connect
                await _session.ConnectAsync();
                
                if (_session.IsConnected)
                {
                    _state = TransportState.Connected(TransportDisplayName);
                    
                    // Send initialization handshake
                    // In the original code, this was done inside HandleEndpointEvent -> SendRegisterToolsAsync
                    // Now we do it explicitly after connection.
                    await SendRegisterToolsAsync();
                    
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[SseTransportClient] Start failed: {ex.Message}");
                _state = TransportState.Disconnected(TransportDisplayName, ex.Message);
                return false;
            }
        }

        public async Task StopAsync()
        {
            await _session.DisconnectAsync();
            _state = TransportState.Disconnected(TransportDisplayName);
        }

        public Task<bool> VerifyAsync()
        {
             return Task.FromResult(IsConnected);
        }

        // Facade for sending notifications
        public async Task SendNotificationAsync(string method, JObject parameters)
        {
            await _session.SendNotificationAsync(method, parameters);
        }
        
        private async Task SendRegisterToolsAsync()
        {
             // McpJsonRpcFactory is still used helper
             var initRequest = McpJsonRpcFactory.CreateInitializeRequest();
             // We can use SendRequestAsync because McpSession handles the response correlation (even if we ignore result)
             // Or if existing server expects request but returns void/ack? 
             // The original code used SendJsonAsync (fire and forget for this specific call?).
             // Let's use SendRequest so we know if it failed.
             
             // Wait, original was: SendJsonAsync(initRequest, token)
             // Initialize is a request, so it expects a response.
             try 
             {
                await _session.SendRequestAsync("initialize", initRequest["params"] as JObject);
             }
             catch(Exception ex)
             {
                 McpLog.Warn($"[SseTransportClient] Initialize warning: {ex.Message}");
             }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose(); // Disposes transport too
        }
    }
}
