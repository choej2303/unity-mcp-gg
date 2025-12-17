using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Config;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// A "dumb pipe" transport for Server-Sent Events (SSE).
    /// Responsible only for connection, data transmission, and raw event dispatch.
    /// Protocol logic is completely removed.
    /// </summary>
    public class SseTransport : IMcpTransport
    {
        public string Name => "http (sse)";
        public bool IsConnected { get; private set; }

        public event Action<string> OnMessageReceived;
        public event Action<string> OnError;

        private readonly HttpClient _httpClient; // Could be injected if needed
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        
        private string _baseUrl;
        private string _postEndpoint;

        public SseTransport()
        {
            // We use a dedicated HttpClient for the transport lifetime
            _httpClient = new HttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan; 
        }

        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            await DisconnectAsync(); // Ensure clean slate

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _baseUrl = HttpEndpointUtility.GetBaseUrl();
            
            // Start the receive loop
            // We use a TCS to wait for the initial connection to be established or failed
            var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _receiveTask = ReceiveLoopAsync(connectedTcs, _connectionCts.Token);

            // Wait for signal
            var completed = await Task.WhenAny(connectedTcs.Task, Task.Delay(5000, token)); // 5s timeout on handshake
            
            if (completed == connectedTcs.Task)
            {
                return await connectedTcs.Task;
            }
            
            McpLog.Warn("[SseTransport] Connection timed out.");
            return false;
        }

        public async Task SendAsync(string message, CancellationToken token = default)
        {
            if (!IsConnected || string.IsNullOrEmpty(_postEndpoint))
            {
                throw new InvalidOperationException("Transport is not connected or endpoint invalid.");
            }

            var content = new StringContent(message, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_postEndpoint, content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        public async Task DisconnectAsync()
        {
            if (_connectionCts != null)
            {
                _connectionCts.Cancel();
                _connectionCts.Dispose();
                _connectionCts = null;
            }
            
            if (_receiveTask != null)
            {
                try { await _receiveTask; } catch { /* Ignore cancellation */ }
                _receiveTask = null;
            }

            IsConnected = false;
        }

        public void Dispose()
        {
            DisconnectAsync().GetAwaiter().GetResult();
            _httpClient.Dispose();
        }

        private async Task ReceiveLoopAsync(TaskCompletionSource<bool> connectedTcs, CancellationToken token)
        {
            string sseUrl = $"{_baseUrl.TrimEnd('/')}/sse";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                // Send headers only request first
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    OnError?.Invoke($"HTTP {response.StatusCode}: {error}");
                    connectedTcs.TrySetResult(false);
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var sseReader = new SseEventReader(stream);

                // Wait for the first event "endpoint" to consider us connected?
                // Or consider connected once stream opens?
                // Standard MCP SSE flow: Client GET /sse -> Server sends endpoint event -> Client POSTs to endpoint.
                // We'll mark as connected once we have the POST endpoint.
                
                // But for the sake of "Transport is open", the stream is open. 
                // However, we can't send until we get the endpoint.
                
                while (!token.IsCancellationRequested)
                {
                    var evt = await sseReader.ReadNextEventAsync(token);
                    if (evt.IsEmpty) break;

                    if (evt.EventName == "endpoint")
                    {
                        HandleEndpointEvent(evt.Data);
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            // Trigger session ready if needed? 
                            // IMcpTransport just has ConnectAsync task return true.
                            connectedTcs.TrySetResult(true);
                        }
                    }
                    else if (evt.EventName == "message")
                    {
                        OnMessageReceived?.Invoke(evt.Data);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                connectedTcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[SseTransport] Error in receive loop: {ex.Message}");
                OnError?.Invoke(ex.Message);
                connectedTcs.TrySetResult(false);
            }
            finally
            {
                IsConnected = false;
            }
        }

        private void HandleEndpointEvent(string data)
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
            
            // NOTE: We stripped out session ID parsing and ProjectIdentityUtility.SetSessionId(_sessionId) 
            // as those seem like side-effects that might belong in a session manager or higher level service?
            // However, McpSession doesn't know about HTTP implementation details like post endpoints.
            // For now, if SessionID is just for internal ID, maybe we can ignore it in the dumb pipe? 
            // BUT ProjectIdentityUtility seems important. We should verify if we need to keep that side-effect.
            // The original SseTransportClient parsed 'session_id' from the query string of the endpoint.
            
            ParseAndSetSessionId(_postEndpoint);
        }

        private void ParseAndSetSessionId(string url)
        {
             if (Uri.TryCreate(url, UriKind.Absolute, out var epUri))
             {
                 string queryString = epUri.Query;
                 if (!string.IsNullOrEmpty(queryString))
                 {
                     if (queryString.StartsWith("?")) queryString = queryString.Substring(1);
                     var parts = queryString.Split('&');
                     foreach (var part in parts)
                     {
                         var kv = part.Split('=');
                         if (kv.Length == 2 && kv[0] == "session_id")
                         {
                             string sessionId = Uri.UnescapeDataString(kv[1]);
                             if (!string.IsNullOrEmpty(sessionId))
                             {
                                 ProjectIdentityUtility.SetSessionId(sessionId);
                             }
                             break;
                         }
                     }
                 }
             }
        }
    }
}
