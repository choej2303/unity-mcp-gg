import asyncio
import logging
import time
from hashlib import sha256
from typing import Optional

from fastmcp import FastMCP
from pydantic import BaseModel, Field, ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse

from models.models import MCPResponse, ToolDefinitionModel, ToolParameterModel
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import (
    async_send_command_with_retry,
    get_unity_connection_pool,
)
from transport.plugin_hub import PluginHub

logger = logging.getLogger("mcp-for-unity-server")

_DEFAULT_POLL_INTERVAL = 1.0
_MAX_POLL_SECONDS = 600


class RegisterToolsPayload(BaseModel):
    project_id: str
    project_hash: str | None = None
    tools: list[ToolDefinitionModel]


class ToolRegistrationResponse(BaseModel):
    success: bool
    registered: list[str]
    replaced: list[str]
    message: str


class CustomToolService:
    _instance: "CustomToolService | None" = None

    def __init__(self, mcp: FastMCP):
        CustomToolService._instance = self
        self._mcp = mcp
        self._project_tools: dict[str, dict[str, ToolDefinitionModel]] = {}
        self._hash_to_project: dict[str, str] = {}
        self._register_http_routes()

    @classmethod
    def get_instance(cls) -> "CustomToolService":
        if cls._instance is None:
            raise RuntimeError("CustomToolService has not been initialized")
        return cls._instance

    # --- HTTP Routes -----------------------------------------------------
    def _register_http_routes(self) -> None:
        @self._mcp.custom_route("/register-tools", methods=["POST"])
        async def register_tools(request: Request) -> JSONResponse:
            try:
                payload = RegisterToolsPayload.model_validate(await request.json())
            except ValidationError as exc:
                return JSONResponse({"success": False, "error": exc.errors()}, status_code=400)

            registered: list[str] = []
            replaced: list[str] = []
            for tool in payload.tools:
                if self._is_registered(payload.project_id, tool.name):
                    replaced.append(tool.name)
                self._register_tool(payload.project_id, tool)
                registered.append(tool.name)

            if payload.project_hash:
                self._hash_to_project[payload.project_hash.lower(
                )] = payload.project_id

            message = f"Registered {len(registered)} tool(s)"
            if replaced:
                message += f" (replaced: {', '.join(replaced)})"

            response = ToolRegistrationResponse(
                success=True,
                registered=registered,
                replaced=replaced,
                message=message,
            )
            return JSONResponse(response.model_dump())

    # --- Public API for MCP tools ---------------------------------------
    async def list_registered_tools(self, project_id: str) -> list[ToolDefinitionModel]:
        # Sync tools from Unity (Stdio bridge)
        # We need a way to map project_id back to specific unity instance if possible,
        # but for now we might sync from ALL active instances or default one?
        # Actually list_registered_tools is often called without project_id if it's GLOBAL.
        # But here it takes project_id.
        
        # Determine the Unity instance for this project_id would be hard unless we track it.
        # However, CustomToolService is global.
        
        # Try to sync tools from Unity Stdio Bridge
        await self._sync_all_unity_tools()

        legacy = list(self._project_tools.get(project_id, {}).values())
        hub_tools = await PluginHub.get_tools_for_project(project_id)
        return legacy + hub_tools

    async def get_tool_definition(self, project_id: str, tool_name: str) -> ToolDefinitionModel | None:
        # Check cache first
        tool = self._project_tools.get(project_id, {}).get(tool_name)
        if tool:
            return tool
            
        # If not found, maybe sync?
        if not tool:
             # Just-in-time sync (maybe a specific tool was just added)
             # But limit frequency
             await self._sync_all_unity_tools(force=False)
             tool = self._project_tools.get(project_id, {}).get(tool_name)
             if tool:
                 return tool

        return await PluginHub.get_tool_definition(project_id, tool_name)

    async def execute_tool(
        self,
        project_id: str,
        tool_name: str,
        unity_instance: str | None,
        params: dict[str, object] | None = None,
    ) -> MCPResponse:
        params = params or {}
        logger.info(
            f"Executing tool '{tool_name}' for project '{project_id}' (instance={unity_instance}) with params: {params}"
        )

        definition = await self.get_tool_definition(project_id, tool_name)
        if definition is None:
            # One last try to sync
            await self._sync_all_unity_tools(force=True)
            definition = await self.get_tool_definition(project_id, tool_name)

        if definition is None:
            return MCPResponse(
                success=False,
                message=f"Tool '{tool_name}' not found for project {project_id}",
            )

        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            tool_name,
            params,
        )

        if not definition.requires_polling:
            result = self._normalize_response(response)
            logger.info(f"Tool '{tool_name}' immediate response: {result}")
            return result

        result = await self._poll_until_complete(
            tool_name,
            unity_instance,
            params,
            response,
            definition.poll_action or "status",
        )
        logger.info(f"Tool '{tool_name}' polled response: {result}")
        return result

    # --- Internal helpers ------------------------------------------------
    def _is_registered(self, project_id: str, tool_name: str) -> bool:
        return tool_name in self._project_tools.get(project_id, {})

    def _register_tool(self, project_id: str, definition: ToolDefinitionModel) -> None:
        self._project_tools.setdefault(project_id, {})[
            definition.name] = definition

    _last_sync_time: float = 0
    _sync_interval: float = 5.0 # Seconds

    async def _sync_all_unity_tools(self, force: bool = False):
        if not force and (time.time() - self._last_sync_time < self._sync_interval):
            return

        self._last_sync_time = time.time()
        
        try:
            pool = get_unity_connection_pool()
            instances = pool.discover_all_instances()
            
            for inst in instances:
                try:
                    # Request tool list from this instance
                    response = await async_send_command_with_retry(
                        "list_csharp_tools", 
                        {}, 
                        instance_id=inst.id
                    )
                    
                    if not isinstance(response, dict) or "tools" not in response:
                        continue
                        
                    # Calculate project_id for this instance
                    pid = compute_project_id(inst.name, inst.path)
                    
                    # Register tools
                    for tool_data in response["tools"]:
                        # Validate basic fields
                        if "name" not in tool_data:
                            continue
                            
                        # Convert to ToolDefinitionModel
                        try:
                            # Map fields manually to ensure compatibility
                            params_models = []
                            for p in tool_data.get("parameters", []):
                                params_models.append(ToolParameterModel(
                                    name=p["name"],
                                    description=p.get("description"),
                                    type=p.get("type", "string"),
                                    required=p.get("required", True),
                                    default_value=p.get("default_value")
                                ))

                            defn = ToolDefinitionModel(
                                name=tool_data["name"],
                                description=tool_data.get("description"),
                                structured_output=tool_data.get("structured_output", True),
                                requires_polling=tool_data.get("requires_polling", False),
                                poll_action=tool_data.get("poll_action", "status"),
                                parameters=params_models
                            )
                            
                            # Only auto-register if the tool says so
                            if tool_data.get("auto_register", True):
                                self._register_tool(pid, defn)
                                
                        except ValidationError as ve:
                            logger.warn(f"Failed to validate tool {tool_data.get('name')}: {ve}")
                            
                except Exception as ex:
                    logger.debug(f"Failed to sync tools from instance {inst.id}: {ex}")
                    
        except Exception as e:
            logger.warn(f"Error during Unity tool sync: {e}")


    def get_project_id_for_hash(self, project_hash: str | None) -> str | None:
        if not project_hash:
            return None
        return self._hash_to_project.get(project_hash.lower())

    async def _poll_until_complete(
        self,
        tool_name: str,
        unity_instance,
        initial_params: dict[str, object],
        initial_response,
        poll_action: str,
    ) -> MCPResponse:
        poll_params = dict(initial_params)
        poll_params["action"] = poll_action or "status"

        deadline = time.time() + _MAX_POLL_SECONDS
        response = initial_response

        while True:
            status, poll_interval = self._interpret_status(response)

            if status in ("complete", "error", "final"):
                return self._normalize_response(response)

            if time.time() > deadline:
                return MCPResponse(
                    success=False,
                    message=f"Timeout waiting for {tool_name} to complete",
                    data=self._safe_response(response),
                )

            await asyncio.sleep(poll_interval)

            try:
                response = await send_with_unity_instance(
                    async_send_command_with_retry, unity_instance, tool_name, poll_params
                )
            except Exception as exc:  # pragma: no cover - network/domain reload variability
                logger.debug(f"Polling {tool_name} failed, will retry: {exc}")
                # Back off modestly but stay responsive.
                response = {
                    "_mcp_status": "pending",
                    "_mcp_poll_interval": min(max(poll_interval * 2, _DEFAULT_POLL_INTERVAL), 5.0),
                    "message": f"Retrying after transient error: {exc}",
                }

    def _interpret_status(self, response) -> tuple[str, float]:
        if response is None:
            return "pending", _DEFAULT_POLL_INTERVAL

        if not isinstance(response, dict):
            return "final", _DEFAULT_POLL_INTERVAL

        status = response.get("_mcp_status")
        if status is None:
            if len(response.keys()) == 0:
                return "pending", _DEFAULT_POLL_INTERVAL
            return "final", _DEFAULT_POLL_INTERVAL

        if status == "pending":
            interval_raw = response.get(
                "_mcp_poll_interval", _DEFAULT_POLL_INTERVAL)
            try:
                interval = float(interval_raw)
            except (TypeError, ValueError):
                interval = _DEFAULT_POLL_INTERVAL

            interval = max(0.1, min(interval, 5.0))
            return "pending", interval

        if status == "complete":
            return "complete", _DEFAULT_POLL_INTERVAL

        if status == "error":
            return "error", _DEFAULT_POLL_INTERVAL

        return "final", _DEFAULT_POLL_INTERVAL

    def _normalize_response(self, response) -> MCPResponse:
        if isinstance(response, MCPResponse):
            return response
        if isinstance(response, dict):
            return MCPResponse(
                success=response.get("success", True),
                message=response.get("message"),
                error=response.get("error"),
                data=response.get(
                    "data", response) if "data" not in response else response["data"],
            )

        success = True
        message = None
        error = None
        data = None

        if isinstance(response, dict):
            success = response.get("success", True)
            if "_mcp_status" in response and response["_mcp_status"] == "error":
                success = False
            message = str(response.get("message")) if response.get(
                "message") else None
            error = str(response.get("error")) if response.get(
                "error") else None
            data = response.get("data")
            if "success" not in response and "_mcp_status" not in response:
                data = response
        else:
            success = False
            message = str(response)

        return MCPResponse(success=success, message=message, error=error, data=data)

    def _safe_response(self, response):
        if isinstance(response, dict):
            return response
        if response is None:
            return None
        return {"message": str(response)}




def compute_project_id(project_name: str, project_path: str) -> str:
    combined = f"{project_name}:{project_path}"
    return sha256(combined.encode("utf-8")).hexdigest().upper()[:16]


def resolve_project_id_for_unity_instance(unity_instance: str | None) -> str | None:
    if unity_instance is None:
        return None

    # stdio transport: resolve via discovered instances with name+path
    try:
        pool = get_unity_connection_pool()
        instances = pool.discover_all_instances()
        target = None
        if "@" in unity_instance:
            name_part, _, hash_hint = unity_instance.partition("@")
            target = next(
                (
                    inst for inst in instances
                    if inst.name == name_part and inst.hash.startswith(hash_hint)
                ),
                None,
            )
        else:
            target = next(
                (
                    inst for inst in instances
                    if inst.id == unity_instance or inst.hash.startswith(unity_instance)
                ),
                None,
            )

        if target:
            return compute_project_id(target.name, target.path)
    except Exception:
        logger.debug(
            f"Failed to resolve project id via connection pool for {unity_instance}")

    # HTTP/WebSocket transport: resolve via PluginHub using project_hash
    try:
        hash_part: Optional[str] = None
        if "@" in unity_instance:
            _, _, suffix = unity_instance.partition("@")
            hash_part = suffix or None
        else:
            hash_part = unity_instance

        if hash_part:
            lowered = hash_part.lower()
            mapped: Optional[str] = None
            try:
                service = CustomToolService.get_instance()
                mapped = service.get_project_id_for_hash(lowered)
            except RuntimeError:
                mapped = None
            if mapped:
                return mapped
            return lowered
    except Exception:
        logger.debug(
            f"Failed to resolve project id via plugin hub for {unity_instance}")

    return None
