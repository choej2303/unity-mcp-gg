using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Interface for executing MCP commands and tools.
    /// Abstracts the command registry and execution logic.
    /// </summary>
    public interface ICommandExecutor
    {
        /// <summary>
        /// Executes a requested command or tool.
        /// </summary>
        /// <param name="commandType">The method/command name (e.g. "tools/call").</param>
        /// <param name="parameters">Arguments for the command.</param>
        /// <returns>The result object (will be serialized to JSON).</returns>
        Task<object> ExecuteCommandAsync(string commandType, JObject parameters);
    }
}
