using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Represents a single Server-Sent Event.
    /// </summary>
    public struct SseEvent
    {
        public string EventName;
        public string Data;

        public bool IsEmpty => string.IsNullOrEmpty(EventName) && string.IsNullOrEmpty(Data);
        public static SseEvent Empty => new SseEvent();

        public SseEvent(string eventName, string data)
        {
            EventName = eventName;
            Data = data;
        }
    }

    /// <summary>
    /// dedicated reader for parsing Server-Sent Events streams.
    /// Handles the "event:" and "data:" line parsing logic.
    /// </summary>
    public class SseEventReader : IDisposable
    {
        private readonly StreamReader _reader;
        private bool _disposed;

        public SseEventReader(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _reader = new StreamReader(stream, Encoding.UTF8);
        }

        /// <summary>
        /// Reads the next complete event from the stream.
        /// Returns SseEvent.Empty if end of stream is reached.
        /// </summary>
        public async Task<SseEvent> ReadNextEventAsync(CancellationToken token = default)
        {
            string currentEvent = null;
            StringBuilder dataBuilder = null;

            while (!_reader.EndOfStream && !token.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await _reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Stream closed or broken
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (line == null) break;

                // Empty line acts as a separator between events in some SSE specs, 
                // but standard MCP usually pushes data immediately.
                // However, standard SSE uses double newline to dispatch event.
                // Our implementation in SseTransportClient handled one event per block/line group.
                // Let's mimic the robustness: if we have data and hit empty line, we *could* emit.
                // BUT, MCP typically sends "event:..." then "data:..." lines.

                if (string.IsNullOrWhiteSpace(line))
                {
                    // If we have accumulated an event, return it
                    if (dataBuilder != null || !string.IsNullOrEmpty(currentEvent))
                    {
                        return new SseEvent(
                            currentEvent ?? "message", 
                            dataBuilder?.ToString() ?? string.Empty
                        );
                    }
                    continue;
                }

                if (line.StartsWith("event:"))
                {
                    currentEvent = line.Substring("event:".Length).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    string chunk = line.Substring("data:".Length).Trim();
                    if (dataBuilder == null) dataBuilder = new StringBuilder();
                    else dataBuilder.Append("\n"); // Append newline for multi-line data
                    
                    dataBuilder.Append(chunk);
                    
                    // In the original code, it dispatched immediately upon finding "data:".
                    // "await ProcessEventAsync(currentEvent, data, token)"
                    // This implies one data line = one message in the MCP context so far context.
                    // But strictly SSE allows multi-line data.
                    // To stay safe and strictly 1:1 with previous logic:
                    // Previous logic:
                    // else if (line.StartsWith("data:")) {
                    //    string data = ...;
                    //    await ProcessEventAsync(...);
                    //    currentEvent = null; 
                    // }
                    // It reset currentEvent after data! So it assumes event -> data pairing per message.
                    
                    return new SseEvent(
                        currentEvent ?? "message", 
                        dataBuilder.ToString()
                    );
                }
            }

            return SseEvent.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _reader.Dispose(); } catch { }
        }
    }
}
