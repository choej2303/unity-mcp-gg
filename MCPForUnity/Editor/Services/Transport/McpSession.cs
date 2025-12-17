using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Manages an MCP session over a given transport.
    /// Handles JSON-RPC 2.0 protocol: parsing messages, matching requests to responses,
    /// and dispatching incoming notifications/requests to the executor.
    /// </summary>
    public class McpSession : IDisposable
    {
        private readonly IMcpTransport _transport;
        private readonly ICommandExecutor _executor;
        
        // Pending requests sent BY US to the server (waiting for response)
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pendingRequests 
            = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();

        public bool IsConnected => _transport != null && _transport.IsConnected;

        public event Action<string> OnSessionReady;
        public event Action<string> OnSessionError;

        public McpSession(IMcpTransport transport, ICommandExecutor executor)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));

            _transport.OnMessageReceived += HandleMessageReceived;
            _transport.OnError += HandleTransportError;
        }

        public async Task ConnectAsync(CancellationToken token = default)
        {
            await _transport.ConnectAsync(token);
        }

        public async Task DisconnectAsync()
        {
            await _transport.DisconnectAsync();
            _pendingRequests.Clear();
        }

        /// <summary>
        /// Sends a JSON-RPC Request and waits for a Response.
        /// </summary>
        public async Task<JObject> SendRequestAsync(string method, JObject parameters, CancellationToken token = default)
        {
            string id = Guid.NewGuid().ToString();
            var request = McpJsonRpcFactory.CreateRequest(method, parameters, id);
            
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[id] = tcs;

            try
            {
                await _transport.SendAsync(request.ToString(Formatting.None), token); // Compact JSON
                
                // Wait for response or timeout/cancellation
                using (token.Register(() => tcs.TrySetCanceled()))
                {
                   return await tcs.Task;
                }
            }
            catch (Exception)
            {
                _pendingRequests.TryRemove(id, out _);
                throw;
            }
        }

        /// <summary>
        /// Sends a JSON-RPC Notification (no response expected).
        /// </summary>
        public async Task SendNotificationAsync(string method, JObject parameters, CancellationToken token = default)
        {
            var notification = McpJsonRpcFactory.CreateNotification(method, parameters);
            await _transport.SendAsync(notification.ToString(Formatting.None), token);
        }

        private void HandleMessageReceived(string rawJson)
        {
            // Offload parsing to ThreadPool to avoid blocking transport IO, 
            // but ensure heavy logic rejoins main thread if needed (CommandExecutor handles that).
            Task.Run(async () => await ProcessMessageAsync(rawJson));
        }

        private async Task ProcessMessageAsync(string rawJson)
        {
            JObject message;
            try
            {
                message = JObject.Parse(rawJson);
            }
            catch (Exception ex)
            {
                McpLog.Error($"[McpSession] Failed to parse JSON: {ex.Message}");
                return;
            }

            // Check if it's a response to one of our requests
            if (message.TryGetValue("id", out var idToken) && idToken.Type == JTokenType.String)
            {
                string id = idToken.Value<string>();
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (message.ContainsKey("error"))
                    {
                        tcs.TrySetException(new Exception(message["error"].ToString()));
                    }
                    else
                    {
                        tcs.TrySetResult(message["result"] as JObject ?? new JObject());
                    }
                    return;
                }
            }

            // It's a Request or Notification from the server (Server -> Client)
            if (message.TryGetValue("method", out var methodToken))
            {
                string method = methodToken.Value<string>();
                string id = message["id"]?.Value<string>(); // Null if notification
                JObject parameters = message["params"] as JObject ?? new JObject();

                try
                {
                    // Execute command using the injected executor
                    // NOTE: We assume executor handles thread affinity if needed (e.g. Unity Main Thread)
                    var result = await _executor.ExecuteCommandAsync(method, parameters);

                    // If it was a request (has ID), send success response
                    if (id != null)
                    {
                        JObject resultObj = result != null ? JObject.FromObject(result) : new JObject();
                        var response = McpJsonRpcFactory.CreateResponse(id, resultObj);
                        await _transport.SendAsync(response.ToString(Formatting.None));
                    }
                }
                catch (Exception ex)
                {
                    if (id != null)
                    {
                        var errorResponse = McpJsonRpcFactory.CreateErrorResponse(id, -32603, ex.Message); // Internal error
                        await _transport.SendAsync(errorResponse.ToString(Formatting.None));
                    }
                    McpLog.Error($"[McpSession] Error handling method '{method}': {ex.Message}");
                }
            }
        }

        private void HandleTransportError(string errorMsg)
        {
             McpLog.Warn($"[McpSession] Transport error: {errorMsg}");
             OnSessionError?.Invoke(errorMsg);
        }

        public void Dispose()
        {
            if (_transport != null)
            {
                _transport.OnMessageReceived -= HandleMessageReceived;
                _transport.OnError -= HandleTransportError;
                _transport.Dispose();
            }
            
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }
}
