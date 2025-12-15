using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Factory for creating standard MCP JSON-RPC messages.
    /// Enforces the JSON-RPC 2.0 structure and MCP protocol conventions.
    /// </summary>
    public static class McpJsonRpcFactory
    {
        public static JObject CreateRequest(string method, JObject parameters, string id = null)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? Guid.NewGuid().ToString(),
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };
        }

        public static JObject CreateNotification(string method, JObject parameters)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };
        }

        public static JObject CreateResponse(string id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result ?? new JObject()
            };
        }

        public static JObject CreateErrorResponse(string id, int code, string message, JObject data = null)
        {
            var errorObj = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };
            
            if (data != null) errorObj["data"] = data;

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = errorObj
            };
        }

        public static JObject CreateToolCallResult(string id, string textContent, bool isError = false)
        {
            return CreateResponse(id, new JObject
            {
                ["content"] = new JArray 
                { 
                    new JObject 
                    { 
                        ["type"] = "text", 
                        ["text"] = textContent 
                    } 
                },
                ["isError"] = isError
            });
        }
        
        public static JObject CreateInitializeRequest()
        {
            return CreateRequest("initialize", new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject
                {
                    ["name"] = "UnityMCP",
                    ["version"] =Application.unityVersion
                }
            });
        }
    }
}
