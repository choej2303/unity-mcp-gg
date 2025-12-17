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
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Named Pipe IPC Host for efficient local communication on Windows.
    /// Acts as a server that accepts connections and delegates them via OnClientConnected.
    /// </summary>
    [InitializeOnLoad]
    public static class IpcHost
    {
        private static bool isRunning = false;
        private static readonly object lockObj = new();
        private static CancellationTokenSource cts;
        private static Task acceptLoopTask;
        private static string pipeName;
        
        // Event fired when a new client connects.
        // The subscriber is responsible for creating a Transport/Session for this stream.
        public static event Action<NamedPipeServerStream> OnClientConnected;

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
            }
        }

        public static void Start()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                // McpLog.Warn("IPC Transport is only supported on Windows.");
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
            // Note: We don't track active pipes here anymore, 
            // the NamedPipeTransport (created by the subscriber) is responsible for Disposing them.
            
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
                    
                    // Delegate the connected pipe to listeners (e.g. StdioTransportClient)
                    // If no one is listening, we close it to avoid leaks
                    if (OnClientConnected != null)
                    {
                        // Fire event on a separate task/thread to not block accept loop
                        // or just invoke? Invoking synchronously might block if the handler is slow.
                        // But creating NamedPipeTransport is fast.
                        try 
                        {
                            OnClientConnected.Invoke(pipe);
                        }
                        catch(Exception ex)
                        {
                            McpLog.Error($"Error in OnClientConnected handler: {ex}");
                            pipe.Dispose();
                        }
                    }
                    else
                    {
                        // No handler, close immediately
                        pipe.Dispose();
                    }
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

        private static string ComputeProjectHash(string input)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input ?? ""));
            return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLowerInvariant();
        }
    }
}
