using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Represents a single Named Pipe connection that uses Length-Prefixed framing.
    /// Implements IMcpTransport to feed JSON messages to McpSession.
    /// </summary>
    public class NamedPipeTransport : IMcpTransport
    {
        private readonly NamedPipeServerStream _pipe;
        
        public string Name => "ipc (named pipe)";
        public bool IsConnected => _pipe != null && _pipe.IsConnected;

        public event Action<string> OnMessageReceived;
        public event Action<string> OnError;

        private Task _receiveTask;
        private CancellationTokenSource _cts;

        public NamedPipeTransport(NamedPipeServerStream pipe)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        }

        public Task<bool> ConnectAsync(CancellationToken token = default)
        {
            // For this transport, we assume the pipe is already connected (handed over by Host).
            // We just start the receive loop.
            if (!_pipe.IsConnected)
            {
                return Task.FromResult(false);
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            
            return Task.FromResult(true);
        }

        public async Task SendAsync(string message, CancellationToken token = default)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            try
            {
                byte[] responseBytes = Encoding.UTF8.GetBytes(message);
                byte[] respHeader = new byte[8];
                WriteUInt64BigEndian(respHeader, (ulong)responseBytes.LongLength);

                // Write header then payload
                await _pipe.WriteAsync(respHeader, 0, 8, token);
                await _pipe.WriteAsync(responseBytes, 0, responseBytes.Length, token);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            
            if (_receiveTask != null)
            {
                try { await _receiveTask; } catch { }
            }

            try 
            { 
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
                await _pipe.DisposeAsync(); 
#else
                _pipe.Dispose();
#endif
            } 
            catch { }
        }

        public void Dispose()
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                // Send handshake if needed? 
                // Original IpcHost sent "WELCOME...". 
                // We should probably do that here or let the Host do it before creating Transport?
                // Let's assume Host did it or we do it here. 
                // Original code: after WaitForConnection, sends Welcome, then loop.
                // We'll move the Welcome to the constructor or Connect? 
                // Better to let this class send it on Connect.

                string welcome = "WELCOME UNITY-MCP 1 FRAMING=1\n";
                byte[] welcomeBytes = Encoding.ASCII.GetBytes(welcome);
                await _pipe.WriteAsync(welcomeBytes, 0, welcomeBytes.Length, token);

                while (IsConnected && !token.IsCancellationRequested)
                {
                    // 1. Read Header
                    byte[] header = await ReadExactAsync(_pipe, 8, token);
                    if (header == null) break; 

                    ulong payloadLen = ReadUInt64BigEndian(header);
                    
                    // 2. Read Payload
                    byte[] payload = await ReadExactAsync(_pipe, (int)payloadLen, token);
                    if (payload == null) break;

                    string commandText = Encoding.UTF8.GetString(payload);

                    // 3. Emit
                    OnMessageReceived?.Invoke(commandText);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsConnected)
                {
                    OnError?.Invoke(ex.Message);
                }
            }
            finally
            {
                // Auto-dispose on loop exit?
                // Usually yes for server-side client handling.
            }
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, token);
                if (read == 0) return null; 
                totalRead += read;
            }
            return buffer;
        }

        private static void WriteUInt64BigEndian(byte[] dest, ulong value)
        {
            dest[0] = (byte)(value >> 56);
            dest[1] = (byte)(value >> 48);
            dest[2] = (byte)(value >> 40);
            dest[3] = (byte)(value >> 32);
            dest[4] = (byte)(value >> 24);
            dest[5] = (byte)(value >> 16);
            dest[6] = (byte)(value >> 8);
            dest[7] = (byte)(value);
        }

        private static ulong ReadUInt64BigEndian(byte[] buffer)
        {
            return ((ulong)buffer[0] << 56) | ((ulong)buffer[1] << 48) |
                   ((ulong)buffer[2] << 40) | ((ulong)buffer[3] << 32) |
                   ((ulong)buffer[4] << 24) | ((ulong)buffer[5] << 16) |
                   ((ulong)buffer[6] << 8) | buffer[7];
        }
    }
}
