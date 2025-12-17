using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Executes MCP commands by delegating to the static CommandRegistry.
    /// Acts as a bridge between the new McpSession architecture and the existing tool discovery system.
    /// </summary>
    public class UnityMcpCommandExecutor : ICommandExecutor
    {
        public UnityMcpCommandExecutor()
        {
            // Ensure registry is initialized
            CommandRegistry.Initialize();
        }

        public async Task<object> ExecuteCommandAsync(string commandType, JObject parameters)
        {
            if (string.IsNullOrEmpty(commandType))
            {
                throw new ArgumentException("Command type cannot be null or empty", nameof(commandType));
            }
            
            // Map MCP method names to internal command names if necessary
            // For now, we assume direct mapping or simple tool lookups
            // Standard MCP 'tools/call' might need to be unwrapped if command names are just tool names
            // But checking CommandRegistry, it seems to handle tool names directly.
            
            // If the method is 'tools/call', we unwrap the 'name' and 'arguments' params
            if (string.Equals(commandType, "tools/call", StringComparison.OrdinalIgnoreCase))
            {
                string toolName = parameters?.Value<string>("name");
                JObject arguments = parameters?["arguments"] as JObject ?? new JObject();
                
                if (string.IsNullOrEmpty(toolName))
                {
                    throw new ArgumentException("Tool name is required for tools/call");
                }
                
                return await CommandRegistry.InvokeCommandAsync(toolName, arguments);
            }

            // For other notifications or custom commands, try invoking directly
            return await CommandRegistry.InvokeCommandAsync(commandType, parameters);
        }
    }
}
