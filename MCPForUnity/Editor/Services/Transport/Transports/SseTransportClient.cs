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
    /// </summary>
    public class SseTransportClient : IMcpTransportClient, IDisposable
    {
        private const string TransportDisplayName = "http (sse)";
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

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

        public SseTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
            _httpClient = new HttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;

        private TaskCompletionSource<bool> _connectionTcs;

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
                
                // Timeout or failure during startup
                McpLog.Warn("[SSE] Startup timed out or failed to connect.");
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[SSE] Start failed: {ex.Message}");
                return false;
            }
        }

        private HttpResponseMessage _currentResponse;

        public async Task StopAsync()
        {
            if (_lifecycleCts != null)
            {
                _lifecycleCts.Cancel();
                _lifecycleCts.Dispose();
                _lifecycleCts = null;
            }

            // Force close the stream to unblock ReadLineAsync immediately
            if (_currentResponse != null)
            {
                try { _currentResponse.Dispose(); } catch { }
                _currentResponse = null;
            }

            if (_receiveTask != null)
            {
                try 
                { 
                    // Wait with a short timeout to ensure we don't block the UI thread forever
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
            // FastMCP server URL says .../mcp, and /mcp gave 400 (Msg endpoint).
            // FastMCP server with transport='sse' defaults to /sse
            string sseUrl = $"{_baseUrl.TrimEnd('/')}/sse";
            McpLog.Info($"[SSE] Connecting to {sseUrl}...");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                // We don't use 'using' here because we need to dispose it in StopAsync to unblock
                _currentResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                
                if (!_currentResponse.IsSuccessStatusCode)
                {
                    string errorContent = await _currentResponse.Content.ReadAsStringAsync();
                    McpLog.Error($"[SSE] Connection failed with status {_currentResponse.StatusCode}: {errorContent}");
                    _connectionTcs?.TrySetResult(false);
                    return;
                }

                // response.EnsureSuccessStatusCode(); // Handled manually above

                using var stream = await _currentResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                McpLog.Info("[SSE] Stream connected.");
                _isConnected = true;
                _state = TransportState.Connected(TransportDisplayName, sessionId: "negotiating...", details: sseUrl);
                _connectionTcs?.TrySetResult(true);

                string currentEvent = null;

                while (!reader.EndOfStream && !token.IsCancellationRequested)
                {
                    string line = null;
                    try 
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) 
                    {
                        // Normal shutdown when StopAsync disposes the stream
                        break;
                    }
                    catch (IOException)
                    {
                         // Normal shutdown
                         break;
                    }

                    if (line == null) break;

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (line.StartsWith("event:"))
                    {
                        currentEvent = line.Substring("event:".Length).Trim();
                    }
                    else if (line.StartsWith("data:"))
                    {
                        string data = line.Substring("data:".Length).Trim();
                        await ProcessEventAsync(currentEvent, data, token).ConfigureAwait(false);
                        currentEvent = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _connectionTcs?.TrySetCanceled();
            }
            catch (Exception ex)
            {
                // Ignore errors if we are intentionally stopping
                if (!token.IsCancellationRequested)
                {
                    McpLog.Warn($"[SSE] Disconnected: {ex.Message}");
                    _state = _state.WithError(ex.Message);
                    _connectionTcs?.TrySetResult(false);
                }
            }
            finally
            {
                if (_currentResponse != null)
                {
                    try { _currentResponse.Dispose(); } catch { }
                    _currentResponse = null;
                }

                _isConnected = false;
                if (_state.Error == null) // Fixed: Accessing Error instead of Status
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

                    if (Uri.TryCreate(_postEndpoint, UriKind.Absolute, out var epUri))
                    {
                        string query = epUri.Query;
                        // Fixed: Manual query parsing
                        if (query.Length > 1)
                        {
                             var parts = query.Substring(1).Split('&');
                             foreach (var p in parts)
                             {
                                 var kv = p.Split('=');
                                 if (kv.Length == 2 && kv[0] == "session_id")
                                 {
                                     _sessionId = Uri.UnescapeDataString(kv[1]);
                                     break;
                                 }
                             }
                        }

                        if (!string.IsNullOrEmpty(_sessionId))
                        {
                            ProjectIdentityUtility.SetSessionId(_sessionId);
                            _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _postEndpoint);
                            McpLog.Info($"[SSE] Endpoint received: {_postEndpoint}");
                            await SendRegisterToolsAsync(token).ConfigureAwait(false);
                        }
                    }
                    break;

                case "message":
                    await HandleMessageAsync(data, token).ConfigureAwait(false);
                    break;
                
                default:
                    break;
            }
        }

        private async Task HandleMessageAsync(string json, CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(json);
            }
            catch
            {
                return; 
            }

            string method = payload.Value<string>("method");
            
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

            var internalEnvelope = new JObject
            {
                ["type"] = toolName,
                ["params"] = requestParams?["arguments"] as JObject ?? new JObject()
            };

            string responseJson;
            try 
            {
                responseJson = await TransportCommandDispatcher.ExecuteCommandJsonAsync(internalEnvelope.ToString(Formatting.None), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new { error = ex.Message });
            }

            JObject internalResult;
            try { internalResult = JObject.Parse(responseJson); } 
            catch { internalResult = new JObject(); }

            var rpcResponse = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["content"] = new JArray 
                    { 
                        new JObject 
                        { 
                            ["type"] = "text", 
                            ["text"] = internalResult.ToString(Formatting.None) 
                        } 
                    },
                    ["isError"] = internalResult["status"]?.ToString() == "error"
                }
            };
            
            await SendJsonAsync(rpcResponse, token).ConfigureAwait(false);
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
             var initRequest = new JObject
             {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString(),
                ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "UnityMCP",
                        ["version"] = Application.unityVersion
                    }
                }
             };
             
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
            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };
            return SendJsonAsync(notification, _lifecycleCts?.Token ?? CancellationToken.None);
        }

        // Fixed: Removed 'async' to fix CS1998
        private Task<List<ToolMetadata>> GetEnabledToolsOnMainThreadAsync(CancellationToken token)
        {
            var tcs = new TaskCompletionSource<List<ToolMetadata>>();
             var registration = token.Register(() => tcs.TrySetCanceled());
             EditorApplication.delayCall += () =>
             {
                 try
                 {
                     if (tcs.Task.IsCompleted) return;
                     var tools = _toolDiscoveryService?.GetEnabledTools() ?? new List<ToolMetadata>();
                     tcs.TrySetResult(tools);
                 }
                 catch (Exception ex) { tcs.TrySetException(ex); }
                 finally { registration.Dispose(); }
             };
             return tcs.Task;
        }
    }
}
