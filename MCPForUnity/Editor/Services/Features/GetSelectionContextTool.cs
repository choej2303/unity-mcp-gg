using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Tools;

namespace MCPForUnity.Editor.Services.Features
{
    [McpForUnityTool("get_selection_context", "Get details about the currently selected GameObject in the Unity Editor.", AutoRegister = true)]
    public class GetSelectionContextTool
    {
        public class Parameters
        {
            [ToolParameter("Include components details", false)]
            public bool detailed { get; set; } = false;
        }

        public object Execute(Parameters parameters)
        {
            var activeObject = Selection.activeGameObject;
            if (activeObject == null)
            {
                return new
                {
                    hasSelection = false,
                    message = "No GameObject is currently selected."
                };
            }

            // Basic Info
            var info = new Dictionary<string, object>
            {
                { "name", activeObject.name },
                { "instanceId", activeObject.GetInstanceID() },
                { "position", activeObject.transform.position },
                { "rotation", activeObject.transform.eulerAngles },
                { "scale", activeObject.transform.localScale },
                { "tag", activeObject.tag },
                { "layer", LayerMask.LayerToName(activeObject.layer) },
                { "activeSelf", activeObject.activeSelf },
                { "hierarchyPath", GetHierarchyPath(activeObject.transform) }
            };

            // Components
            var components = activeObject.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToList();
            info["components"] = components;

            if (parameters.detailed)
            {
                // In a real scenario, we might serialize public fields of components here.
                // For now, we just indicate it's requested.
                info["note"] = "Detailed component serialization is not yet implemented.";
            }

            return new
            {
                hasSelection = true,
                selection = info
            };
        }

        private string GetHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }
    }
}
