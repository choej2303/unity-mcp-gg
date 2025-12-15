using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Config;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Implements the standard MCP HTTP Transport using Server-Sent Events (SSE).
    /// Connects to /sse for events and POSTs to the received endpoint for messages.
    /// Refactored to use SseEventReader and McpJsonRpcFactory.
    /// </summary>
    public class SseTransportClient : IMcpTransportClient, IDisposable
    {
        private const string TransportDisplayName = "http (sse)";

        private readonly IToolDiscoveryService _toolDiscoveryService;
        private readonly HttpClient _httpClient;
        
        private CancellationTokenSource _lifecycleCts;
        private Task _receiveTask;
        
        private string _baseUrl;
        private string _postEndpoint;
        private string _sessionId;
        
        private volatile bool _isConnected;
        private TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private bool _disposed;

        private TaskCompletionSource<bool> _connectionTcs;

        public SseTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
            _httpClient = new HttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;


        public async Task<bool> StartAsync()
        {
            await StopAsync();

            _lifecycleCts = new CancellationTokenSource();
            _baseUrl = HttpEndpointUtility.GetBaseUrl();
            _sessionId = null;
            _postEndpoint = null;
            _connectionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _receiveTask = ReceiveLoopAsync(_lifecycleCts.Token);
                
                // Wait for connection or timeout
                var completedTask = await Task.WhenAny(_connectionTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                
                if (completedTask == _connectionTcs.Task && _connectionTcs.Task.Result)
                {
                    return true;
                }
                
                McpLog.Warn("[SSE] Startup timed out or failed to connect.");
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[SSE] Start failed: {ex.Message}");
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (_lifecycleCts != null)
            {
                _lifecycleCts.Cancel();
                _lifecycleCts.Dispose();
                _lifecycleCts = null;
            }

            // Clean up receive task
            if (_receiveTask != null)
            {
                try 
                { 
                    await Task.WhenAny(_receiveTask, Task.Delay(1000)).ConfigureAwait(false);
                } 
                catch { }
                _receiveTask = null;
            }

            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);
            _postEndpoint = null;
        }

        public Task<bool> VerifyAsync()
        {
             return Task.FromResult(_isConnected && !string.IsNullOrEmpty(_postEndpoint));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAsync().GetAwaiter().GetResult();
            _httpClient.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            string sseUrl = $"{_baseUrl.TrimEnd('/')}/sse";
            McpLog.Info($"[SSE] Connecting to {sseUrl}...");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                // Note: We deliberately do NOT dispose this response inside the 'using' block 
                // because we need the stream to stay open until StopAsync is called or cancellation happens.
                // However, we wrap the processing in a try/finally to ensure disposal upon exit.
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    McpLog.Error($"[SSE] Connection failed with status {response.StatusCode}: {errorContent}");
                    _connectionTcs?.TrySetResult(false);
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var sseReader = new SseEventReader(stream);

                McpLog.Info("[SSE] Stream connected.");
                _isConnected = true;
                _state = TransportState.Connected(TransportDisplayName, sessionId: "negotiating...", details: sseUrl);
                _connectionTcs?.TrySetResult(true);

                while (!token.IsCancellationRequested)
                {
                    var evt = await sseReader.ReadNextEventAsync(token);
                    if (evt.IsEmpty)
                    {
                        // End of stream or broken pipe
                        break;
                    }

                    await ProcessEventAsync(evt.EventName, evt.Data, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _connectionTcs?.TrySetCanceled();
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                     McpLog.Warn($"[SSE] Disconnected: {ex.Message}");
                    _state = _state.WithError(ex.Message);
                    _connectionTcs?.TrySetResult(false);
                }
            }
            finally
            {
                _isConnected = false;
                if (_state.Error == null)
                {
                    _state = TransportState.Disconnected(TransportDisplayName);
                }
            }
        }

        private async Task ProcessEventAsync(string eventName, string data, CancellationToken token)
        {
            if (string.IsNullOrEmpty(eventName)) eventName = "message";

            switch (eventName)
            {
                case "endpoint":
                    HandleEndpointEvent(data, token);
                    break;
                case "message":
                    await HandleMessageAsync(data, token).ConfigureAwait(false);
                    break;
            }
        }

        private void HandleEndpointEvent(string data, CancellationToken token)
        {
            if (Uri.TryCreate(data, UriKind.Absolute, out _))
            {
                _postEndpoint = data;
            }
            else
            {
                var baseUri = new Uri(_baseUrl);
                var newUri = new Uri(baseUri, data);
                _postEndpoint = newUri.ToString();
            }

            // Extract session ID from query
            if (Uri.TryCreate(_postEndpoint, UriKind.Absolute, out var epUri))
            {
                var query = System.Web.HttpUtility.ParseQueryString(epUri.Query);
                _sessionId = query["session_id"];

                if (!string.IsNullOrEmpty(_sessionId))
                {
                    ProjectIdentityUtility.SetSessionId(_sessionId);
                    _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _postEndpoint);
                    McpLog.Info($"[SSE] Endpoint received: {_postEndpoint}");
                    // Fire and forget initialization
                    _ = SendRegisterToolsAsync(token);
                }
            }
        }

        private async Task HandleMessageAsync(string json, CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(json);
            }
            catch { return; }

            string method = payload.Value<string>("method");
            
            // Dispatch known protocol messages
            if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(method, "notifications/message", StringComparison.OrdinalIgnoreCase)) 
            {
                await HandleToolCallAsync(payload, token).ConfigureAwait(false);
            }
        }

        private async Task HandleToolCallAsync(JObject request, CancellationToken token)
        {
            var requestParams = request["params"] as JObject;
            string toolName = requestParams?.Value<string>("name");
            string id = request.Value<string>("id");
            
            if (string.IsNullOrEmpty(toolName) || string.IsNullOrEmpty(id)) return;

            // Execute the tool (Inter-service communication)
            // We still depend on TransportCommandDispatcher for now; refactoring this "Dispatcher" is a separate task.
            // However, we construct the request/response using our Factory.
            
            var internalEnvelope = new JObject
            {
                ["type"] = toolName,
                ["params"] = requestParams?["arguments"] as JObject ?? new JObject()
            };

            string responseJson;
            bool isError = false;
            try 
            {
                responseJson = await TransportCommandDispatcher.ExecuteCommandJsonAsync(internalEnvelope.ToString(Formatting.None), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new { error = ex.Message });
                isError = true;
            }

            // Parse result to get inner text
            string resultText;
            try 
            {
                var internalResult = JObject.Parse(responseJson);
                resultText = internalResult.ToString(Formatting.None);
                if (internalResult["status"]?.ToString() == "error") isError = true;
            } 
            catch { resultText = "{}"; }

            // Construct standard MCP response
            var rpcResponse = McpJsonRpcFactory.CreateToolCallResult(id, resultText, isError);
            
            await SendJsonAsync(rpcResponse, token).ConfigureAwait(false);
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
             var initRequest = McpJsonRpcFactory.CreateInitializeRequest();
             await SendJsonAsync(initRequest, token).ConfigureAwait(false);
        }

        private async Task SendJsonAsync(JObject payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(_postEndpoint)) return;
            
            var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_postEndpoint, content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        
        public Task SendNotificationAsync(string method, JObject parameters)
        {
            var notification = McpJsonRpcFactory.CreateNotification(method, parameters);
            return SendJsonAsync(notification, _lifecycleCts?.Token ?? CancellationToken.None);
        }
    }
}
