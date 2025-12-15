using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Unix Domain Socket Host for generic POSIX local communication (macOS/Linux).
    /// Replaces the TCP-based StdioBridge on supported platforms.
    /// </summary>
    [InitializeOnLoad]
    public static class UdsHost
    {
        private static bool isRunning = false;
        private static readonly object lockObj = new();
        private static CancellationTokenSource cts;
        private static string socketPath;
        private static readonly ConcurrentDictionary<Socket, Task> activeConnections = new();

        public static bool IsRunning => isRunning;
        public static string SocketPath => socketPath;

        static UdsHost()
        {
            if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.LinuxEditor)
            {
                // Store in /tmp for standard POSIX access
                socketPath = $"/tmp/UnityMCP.{ComputeProjectHash(Application.dataPath)}.sock";
                EditorApplication.quitting += Stop;
            }
        }

        public static void Start()
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.LinuxEditor)
            {
                // Silently ignore on Windows to allow mixed codebase
                return;
            }

            lock (lockObj)
            {
                if (isRunning) return;
                isRunning = true;
                cts = new CancellationTokenSource();
                
                McpLog.Info($"Starting UDS Host on: {socketPath}");
                // Fire and forget the accept loop
                _ = Task.Run(() => AcceptLoopAsync(cts.Token));
            }
        }

        public static void Stop()
        {
            lock (lockObj)
            {
                if (!isRunning) return;
                isRunning = false;
                cts?.Cancel();
            }

            foreach (var socket in activeConnections.Keys)
            {
                try { socket.Close(); socket.Dispose(); } catch { }
            }
            activeConnections.Clear();
            
            // Clean up the socket file
            try 
            {
                if (File.Exists(socketPath))
                    File.Delete(socketPath);
            } 
            catch { }
            
            McpLog.Info("UDS Host stopped.");
        }

        private static async Task AcceptLoopAsync(CancellationToken token)
        {
            Socket listener = null;
            try
            {
                // Ensure fresh socket file
                if (File.Exists(socketPath))
                    File.Delete(socketPath);

                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                
                // UnixDomainSocketEndPoint is available in .NET Standard 2.1+ / .NET Core
                // If this fails compile on older Unity .NET 4.x profile, we might need a workaround,
                // but most MCP users are on modern Unity.
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(16);

                while (isRunning && !token.IsCancellationRequested)
                {
                    try
                    {
                        Socket client = await listener.AcceptAsync();
                        
                        // Fire and forget connection handler
                        var task = HandleConnectionAsync(client, token);
                        activeConnections.TryAdd(client, task);
                        
                        _ = task.ContinueWith(t => 
                        {
                            activeConnections.TryRemove(client, out _);
                            try { client.Close(); client.Dispose(); } catch { }
                        }, TaskScheduler.Default);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (isRunning) McpLog.Error($"UDS Accept Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to start UDS listener: {ex}");
                isRunning = false;
            }
            finally
            {
                try { listener?.Close(); } catch { }
            }
        }

        private static async Task HandleConnectionAsync(Socket socket, CancellationToken token)
        {
            try
            {
                McpLog.Debug("UDS Client connected.");
                
                // Welcome Message / Handshake
                string welcome = "WELCOME UNITY-MCP 1 FRAMING=1\n";
                byte[] welcomeBytes = System.Text.Encoding.ASCII.GetBytes(welcome);
                await SendBytesAsync(socket, welcomeBytes, token);

                byte[] headerBuffer = new byte[8];

                while (socket.Connected && !token.IsCancellationRequested)
                {
                    // 1. Read Header (8 bytes BE)
                    if (!await ReadExactAsync(socket, headerBuffer, 8, token)) break;
                    ulong payloadLen = ReadUInt64BigEndian(headerBuffer);

                    // 2. Read Payload
                    byte[] payload = new byte[payloadLen];
                    if (!await ReadExactAsync(socket, payload, (int)payloadLen, token)) break;

                    string commandText = System.Text.Encoding.UTF8.GetString(payload);

                    // 3. Execute
                    string responseText;
                    try
                    {
                        responseText = await TransportCommandDispatcher.ExecuteCommandJsonAsync(commandText, token);
                    }
                    catch (Exception ex)
                    {
                        responseText = JsonConvert.SerializeObject(new { status = "error", error = ex.Message });
                    }

                    // 4. Send Response (Framed)
                    byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(responseText);
                    byte[] respHeader = new byte[8];
                    WriteUInt64BigEndian(respHeader, (ulong)responseBytes.LongLength);

                    await SendBytesAsync(socket, respHeader, token);
                    await SendBytesAsync(socket, responseBytes, token);
                }
            }
            catch (Exception)
            {
                // Connection closed or error
            }
        }

        private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, int count, CancellationToken token)
        {
            int totalRead = 0;
            try
            {
                while (totalRead < count)
                {
                    // Use ArraySegment for async socket calls
                    var segment = new ArraySegment<byte>(buffer, totalRead, count - totalRead);
                    int read = await socket.ReceiveAsync(segment, SocketFlags.None);
                    if (read == 0) return false;
                    totalRead += read;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task SendBytesAsync(Socket socket, byte[] data, CancellationToken token)
        {
            // Simple send wrapper
            int sent = 0;
            while (sent < data.Length)
            {
                var segment = new ArraySegment<byte>(data, sent, data.Length - sent);
                int count = await socket.SendAsync(segment, SocketFlags.None);
                sent += count;
            }
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

        private static string ComputeProjectHash(string input)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input ?? ""));
            return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLowerInvariant();
        }
    }
}
