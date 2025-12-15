using System.Linq;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool(
        Name = "list_csharp_tools",
        Description = "Internal tool used by the Python server to discover C# tools dynamically.",
        AutoRegister = true
    )]
    public static class ToolSynchronizationTool
    {
        public static object HandleCommand(JObject parameters)
        {
            // Simply use the existing discovery service to fetch all tools
            var discoveryService = new ToolDiscoveryService();
            var allTools = discoveryService.DiscoverAllTools();

            // Filter out tools that shouldn't be auto-registered if needed, 
            // but for now we send everything and let Python decide.
            // We specifically want to ensure we return the metadata in a format Python expects.
            
            return new
            {
                tools = allTools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    structured_output = t.StructuredOutput,
                    auto_register = t.AutoRegister,
                    requires_polling = t.RequiresPolling,
                    poll_action = t.PollAction,
                    parameters = t.Parameters.Select(p => new
                    {
                        name = p.Name,
                        description = p.Description,
                        type = p.Type,
                        required = p.Required,
                        default_value = p.DefaultValue
                    }).ToList()
                }).ToList()
            };
        }
    }
}
