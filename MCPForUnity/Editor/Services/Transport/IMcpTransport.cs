using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Represents a raw data transport layer for MCP (Machine Context Protocol).
    /// Responsible ONLY for sending and receiving raw string messages types (dumb pipe).
    /// Protocol logic (JSON-RPC) should be handled by McpSession.
    /// </summary>
    public interface IMcpTransport : IDisposable
    {
        /// <summary>
        /// Display name of the transport (e.g. "http (sse)", "stdio").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether the transport is currently connected and ready to send/receive.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Fired when a full text message (JSON) is received from the transport.
        /// </summary>
        event Action<string> OnMessageReceived;

        /// <summary>
        /// Fired when the transport disconnects or encounters a critical error.
        /// Argument is the error message, or null if graceful disconnect.
        /// </summary>
        event Action<string> OnError;

        /// <summary>
        /// Establishes the connection.
        /// </summary>
        Task<bool> ConnectAsync(CancellationToken token = default);

        /// <summary>
        /// Sends a raw text message (JSON) through the transport.
        /// </summary>
        Task SendAsync(string message, CancellationToken token = default);

        /// <summary>
        /// Closes the connection.
        /// </summary>
        Task DisconnectAsync();
    }
}
