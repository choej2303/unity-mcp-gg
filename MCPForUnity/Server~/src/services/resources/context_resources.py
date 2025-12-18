from fastmcp import Context
from services.registry import mcp_for_unity_resource
from services.tools.custom_tool_service import CustomToolService, resolve_project_id_for_unity_instance
from services.tools import get_unity_instance_from_context

@mcp_for_unity_resource(
    uri="unity://context/selection",
    name="Selection Context",
    description="Returns the currently selected GameObject in the active Unity Editor.",
)
async def get_selection_context(ctx: Context) -> str:
    unity_instance = get_unity_instance_from_context(ctx)
    if not unity_instance:
        return "No active Unity instance. Please select an instance first."

    project_id = resolve_project_id_for_unity_instance(unity_instance)
    if not project_id:
        return f"Could not resolve project ID for instance: {unity_instance}"
    
    service = CustomToolService.get_instance()
    
    # We call the C# tool we just created: "get_selection_context"
    try:
        response = await service.execute_tool(
            project_id=project_id,
            tool_name="get_selection_context",
            unity_instance=unity_instance,
            params={}
        )
        
        if response.success:
            # We return the data part as a string (JSON representation)
            return str(response.data)
        else:
            return f"Error fecthing selection: {response.message}"
            
    except Exception as e:
        return f"Failed to fetch selection context: {str(e)}"
