using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Named Pipe IPC Host for efficient local communication on Windows.
    /// Replaces the TCP-based StdioBridge on supported platforms.
    /// </summary>
    [InitializeOnLoad]
    public static class IpcHost
    {
        private static bool isRunning = false;
        private static readonly object lockObj = new();
        private static CancellationTokenSource cts;
        private static Task acceptLoopTask;
        private static string pipeName;
        private static readonly ConcurrentDictionary<NamedPipeServerStream, Task> activeConnections = new();

        private const int MaxInstances = 16;
        private const int InBufferSize = 1024 * 1024;
        private const int OutBufferSize = 1024 * 1024;

        public static bool IsRunning => isRunning;
        public static string PipeName => pipeName;

        static IpcHost()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                pipeName = $"UnityMCP.{ComputeProjectHash(Application.dataPath)}";
                EditorApplication.quitting += Stop;
                // Auto-start logic could go here or be managed by TransportManager
            }
        }

        public static void Start()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                McpLog.Warn("IPC Transport is only supported on Windows.");
                return;
            }

            lock (lockObj)
            {
                if (isRunning) return;
                isRunning = true;
                cts = new CancellationTokenSource();
                
                McpLog.Info($"Starting IPC Host on pipe: {pipeName}");
                acceptLoopTask = Task.Run(() => AcceptLoopAsync(cts.Token));
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

            // Cleanup all active pipes
            foreach (var pipe in activeConnections.Keys)
            {
                try { pipe.Close(); pipe.Dispose(); } catch { }
            }
            activeConnections.Clear();
            McpLog.Info("IPC Host stopped.");
        }

        private static async Task AcceptLoopAsync(CancellationToken token)
        {
            while (isRunning && !token.IsCancellationRequested)
            {
                try
                {
                    var pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        MaxInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        InBufferSize,
                        OutBufferSize
                    );

                    await pipe.WaitForConnectionAsync(token);
                    
                    // Fire and forget connection handler
                    var task = HandleConnectionAsync(pipe, token);
                    activeConnections.TryAdd(pipe, task);
                    
                    _ = task.ContinueWith(t => 
                    {
                        activeConnections.TryRemove(pipe, out _);
                        try { pipe.Dispose(); } catch { }
                    }, TaskScheduler.Default);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isRunning) McpLog.Error($"IPC Accept Error: {ex.Message}");
                    await Task.Delay(1000, token); // Backoff on error
                }
            }
        }

        private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                McpLog.Debug("IPC Client connected.");
                
                // Handshake (Optional compatibility with StdioBridge)
                // We'll skip strict framing negotiation for IPC as we control both ends,
                // but for protocol consistency we can send the welcome message.
                string welcome = "WELCOME UNITY-MCP 1 FRAMING=1\n";
                byte[] welcomeBytes = System.Text.Encoding.ASCII.GetBytes(welcome);
                await pipe.WriteAsync(welcomeBytes, 0, welcomeBytes.Length, token);

                while (pipe.IsConnected && !token.IsCancellationRequested)
                {
                    // Read Length-Prefixed Frame
                    // 1. Read 8 byte header (Big Endian ulong)
                    byte[] header = await ReadExactAsync(pipe, 8, token);
                    if (header == null) break; // End of stream

                    ulong payloadLen = ReadUInt64BigEndian(header);
                    
                    // 2. Read Payload
                    byte[] payload = await ReadExactAsync(pipe, (int)payloadLen, token);
                    if (payload == null) break;

                    string commandText = System.Text.Encoding.UTF8.GetString(payload);
                    
                    // 3. Process
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

                    await pipe.WriteAsync(respHeader, 0, 8, token);
                    await pipe.WriteAsync(responseBytes, 0, responseBytes.Length, token);
                }
            }
            catch (Exception)
            {
                if (isRunning && !token.IsCancellationRequested)
                {
                    // Expected disconnection or error
                }
            }
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, token);
                if (read == 0) return null; // Disconnected
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

        private static string ComputeProjectHash(string input)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input ?? ""));
            return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLowerInvariant();
        }
    }
}
