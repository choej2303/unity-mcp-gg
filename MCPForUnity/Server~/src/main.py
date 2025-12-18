# Ensure src folder is in sys.path for direct script execution
import sys
import os
_src_dir = os.path.dirname(os.path.abspath(__file__))
if _src_dir not in sys.path:
    sys.path.insert(0, _src_dir)

import argparse
import asyncio
import logging
from contextlib import asynccontextmanager
import os
import threading
import time
from typing import AsyncIterator, Any
from urllib.parse import urlparse

from fastmcp import FastMCP
from logging.handlers import RotatingFileHandler
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import WebSocketRoute

from core.config import config
from services.tools.custom_tool_service import CustomToolService
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from services.resources import register_all_resources
from core.telemetry import record_milestone, record_telemetry, MilestoneType, RecordType, get_package_version
from services.tools import register_all_tools
from transport.legacy.unity_connection import get_unity_connection_pool, UnityConnectionPool
from transport.unity_instance_middleware import (
    UnityInstanceMiddleware,
    get_unity_instance_middleware
)

# Configure logging using separated module
from core.logging_setup import setup_logging, silence_stdio_loggers
_fh = setup_logging()
logger = logging.getLogger("mcp-for-unity-server")

# Import telemetry only after logging is configured to ensure its logs use stderr and proper levels
# Ensure a slightly higher telemetry timeout unless explicitly overridden by env
try:

    # Ensure generous timeout unless explicitly overridden by env
    if not os.environ.get("UNITY_MCP_TELEMETRY_TIMEOUT"):
        os.environ["UNITY_MCP_TELEMETRY_TIMEOUT"] = "5.0"
except Exception:
    pass

# Global connection pool
_unity_connection_pool: UnityConnectionPool | None = None
_plugin_registry: PluginRegistry | None = None

# In-memory custom tool service initialized after MCP construction
custom_tool_service: CustomToolService | None = None


@asynccontextmanager
async def server_lifespan(server: FastMCP) -> AsyncIterator[dict[str, Any]]:
    """Handle server startup and shutdown."""
    global _unity_connection_pool
    logger.info("MCP for Unity Server starting up")

    # Register custom tool management endpoints with FastMCP
    # Routes are declared globally below after FastMCP initialization

    # Note: When using HTTP transport, FastMCP handles the HTTP server
    # Tool registration will be handled through FastMCP endpoints
    enable_http_server = os.environ.get(
        "UNITY_MCP_ENABLE_HTTP_SERVER", "").lower() in ("1", "true", "yes", "on")
    if enable_http_server:
        http_host = os.environ.get("UNITY_MCP_HTTP_HOST", "localhost")
        http_port = int(os.environ.get("UNITY_MCP_HTTP_PORT", "8080"))
        logger.info(
            f"HTTP tool registry will be available on http://{http_host}:{http_port}")

    global _plugin_registry
    if _plugin_registry is None:
        _plugin_registry = PluginRegistry()
        loop = asyncio.get_running_loop()
        PluginHub.configure(_plugin_registry, loop)

    # Record server startup telemetry
    start_time = time.time()
    start_clk = time.perf_counter()
    server_version = get_package_version()
    # Defer initial telemetry by 1s to avoid stdio handshake interference

    def _emit_startup():
        try:
            record_telemetry(RecordType.STARTUP, {
                "server_version": server_version,
                "startup_time": start_time,
            })
            record_milestone(MilestoneType.FIRST_STARTUP)
        except Exception:
            logger.debug("Deferred startup telemetry failed", exc_info=True)
    threading.Timer(1.0, _emit_startup).start()

    try:
        skip_connect = os.environ.get(
            "UNITY_MCP_SKIP_STARTUP_CONNECT", "").lower() in ("1", "true", "yes", "on")
        if skip_connect:
            logger.info(
                "Skipping Unity connection on startup (UNITY_MCP_SKIP_STARTUP_CONNECT=1)")
        else:
            # Initialize connection pool and discover instances
            _unity_connection_pool = get_unity_connection_pool()
            instances = _unity_connection_pool.discover_all_instances()

            if instances:
                logger.info(
                    f"Discovered {len(instances)} Unity instance(s): {[i.id for i in instances]}")

                # Try to connect to default instance
                try:
                    _unity_connection_pool.get_connection()
                    logger.info(
                        "Connected to default Unity instance on startup")

                    # Record successful Unity connection (deferred)
                    threading.Timer(1.0, lambda: record_telemetry(
                        RecordType.UNITY_CONNECTION,
                        {
                            "status": "connected",
                            "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
                            "instance_count": len(instances)
                        }
                    )).start()
                except Exception as e:
                    logger.warning(
                        f"Could not connect to default Unity instance: {e}")
            else:
                logger.warning("No Unity instances found on startup")

    except ConnectionError as e:
        logger.warning(f"Could not connect to Unity on startup: {e}")

        # Record connection failure (deferred)
        _err_msg = str(e)[:200]
        threading.Timer(1.0, lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        )).start()
    except Exception as e:
        logger.warning(f"Unexpected error connecting to Unity on startup: {e}")
        _err_msg = str(e)[:200]
        threading.Timer(1.0, lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        )).start()

    try:
        # Yield shared state for lifespan consumers (e.g., middleware)
        yield {
            "pool": _unity_connection_pool,
            "plugin_registry": _plugin_registry,
        }
    finally:
        if _unity_connection_pool:
            _unity_connection_pool.disconnect_all()
        logger.info("MCP for Unity Server shut down")

# Initialize MCP server
mcp = FastMCP(
    name="mcp-for-unity-server",
    lifespan=server_lifespan,
    instructions="""
This server provides tools to interact with the Unity Game Engine Editor.

I have a dynamic tool system. Always check the unity://custom-tools resource first to see what special capabilities are available for the current project.

Targeting Unity instances:
- Use the resource unity://instances to list active Unity sessions (Name@hash).
- When multiple instances are connected, call set_active_instance with the exact Name@hash before using tools/resources. The server will error if multiple are connected and no active instance is set.

Important Workflows:

Resources vs Tools:
- Use RESOURCES to read editor state (editor_state, project_info, project_tags, tests, etc)
- Use TOOLS to perform actions and mutations (manage_editor for play mode control, tag/layer management, etc)
- Always check related resources before modifying the engine state with tools

Script Management:
- After creating or modifying scripts (by your own tools or the `manage_script` tool) use `read_console` to check for compilation errors before proceeding
- Only after successful compilation can new components/types be used
- You can poll the `editor_state` resource's `isCompiling` field to check if the domain reload is complete

Scene Setup:
- Always include a Camera and main Light (Directional Light) in new scenes
- Create prefabs with `manage_asset` for reusable GameObjects
- Use `manage_scene` to load, save, and query scene information

Path Conventions:
- Unless specified otherwise, all paths are relative to the project's `Assets/` folder
- Use forward slashes (/) in paths for cross-platform compatibility

Console Monitoring:
- Check `read_console` regularly to catch errors, warnings, and compilation status
- Filter by log type (Error, Warning, Log) to focus on specific issues

Menu Items:
- Use `execute_menu_item` when you have read the menu items resource
- This lets you interact with Unity's menu system and third-party tools
"""
)

custom_tool_service = CustomToolService(mcp)


@mcp.custom_route("/health", methods=["GET"])
async def health_http(_: Request) -> JSONResponse:
    return JSONResponse({
        "status": "healthy",
        "timestamp": time.time(),
        "message": "MCP for Unity server is running"
    })


@mcp.custom_route("/plugin/sessions", methods=["GET"])
async def plugin_sessions_route(_: Request) -> JSONResponse:
    data = await PluginHub.get_sessions()
    return JSONResponse(data.model_dump())


# Initialize and register middleware for session-based Unity instance routing
# Using the singleton getter ensures we use the same instance everywhere
unity_middleware = get_unity_instance_middleware()
mcp.add_middleware(unity_middleware)
logger.info("Registered Unity instance middleware for session-based routing")

# Mount plugin websocket hub at /hub/plugin when HTTP transport is active
existing_routes = [
    route for route in mcp._get_additional_http_routes()
    if isinstance(route, WebSocketRoute) and route.path == "/hub/plugin"
]
if not existing_routes:
    mcp._additional_http_routes.append(
        WebSocketRoute("/hub/plugin", PluginHub))

# Register all tools
register_all_tools(mcp)

# Register all resources
register_all_resources(mcp)


def main():
    """Entry point for uvx and console scripts."""
    parser = argparse.ArgumentParser(
        description="MCP for Unity Server",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Environment Variables:
  UNITY_MCP_DEFAULT_INSTANCE   Default Unity instance to target (project name, hash, or 'Name@hash')
  UNITY_MCP_SKIP_STARTUP_CONNECT   Skip initial Unity connection attempt (set to 1/true/yes/on)
  UNITY_MCP_TELEMETRY_ENABLED   Enable telemetry (set to 1/true/yes/on)
  UNITY_MCP_TRANSPORT   Transport protocol: stdio or http (default: stdio)
  UNITY_MCP_HTTP_URL   HTTP server URL (default: http://localhost:8080)
  UNITY_MCP_HTTP_HOST   HTTP server host (overrides URL host)
  UNITY_MCP_HTTP_PORT   HTTP server port (overrides URL port)

Examples:
  # Use specific Unity project as default
  python -m src.server --default-instance "MyProject"

  # Start with HTTP transport
  python -m src.server --transport http --http-url http://localhost:8080

  # Start with stdio transport (default)
  python -m src.server --transport stdio

  # Use environment variable for transport
  UNITY_MCP_TRANSPORT=http UNITY_MCP_HTTP_URL=http://localhost:9000 python -m src.server
        """
    )
    parser.add_argument(
        "--default-instance",
        type=str,
        metavar="INSTANCE",
        help="Default Unity instance to target (project name, hash, or 'Name@hash'). "
             "Overrides UNITY_MCP_DEFAULT_INSTANCE environment variable."
    )
    parser.add_argument(
        "--transport",
        type=str,
        choices=["stdio", "http", "sse"],
        default="stdio",
        help="Transport protocol to use: stdio or http (default: stdio). "
             "Overrides UNITY_MCP_TRANSPORT environment variable."
    )
    parser.add_argument(
        "--http-url",
        type=str,
        default="http://localhost:8080",
        metavar="URL",
        help="HTTP server URL (default: http://localhost:8080). "
             "Can also set via UNITY_MCP_HTTP_URL environment variable."
    )
    parser.add_argument(
        "--http-host",
        type=str,
        default=None,
        metavar="HOST",
        help="HTTP server host (overrides URL host). "
             "Overrides UNITY_MCP_HTTP_HOST environment variable."
    )
    parser.add_argument(
        "--http-port",
        type=int,
        default=None,
        metavar="PORT",
        help="HTTP server port (overrides URL port). "
             "Overrides UNITY_MCP_HTTP_PORT environment variable."
    )

    args = parser.parse_args()

    # Set environment variables from command line args
    if args.default_instance:
        os.environ["UNITY_MCP_DEFAULT_INSTANCE"] = args.default_instance
        logger.info(
            f"Using default Unity instance from command-line: {args.default_instance}")

    # Set transport mode
    transport_mode = args.transport or os.environ.get(
        "UNITY_MCP_TRANSPORT", "stdio")
    os.environ["UNITY_MCP_TRANSPORT"] = transport_mode
    logger.info(f"Transport mode: {transport_mode}")

    http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
    parsed_url = urlparse(http_url)

    # Allow individual host/port to override URL components
    http_host = args.http_host or os.environ.get(
        "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "localhost"
    # Parse HTTP port safely
    env_port = os.environ.get("UNITY_MCP_HTTP_PORT")
    parsed_port = int(env_port) if env_port else None
    
    http_port = args.http_port or parsed_port or parsed_url.port or 8080

    os.environ["UNITY_MCP_HTTP_HOST"] = http_host
    os.environ["UNITY_MCP_HTTP_PORT"] = str(http_port)

    if args.http_url != "http://localhost:8080":
        logger.info(f"HTTP URL set to: {http_url}")
    if args.http_host:
        logger.info(f"HTTP host override: {http_host}")
    if args.http_port:
        logger.info(f"HTTP port override: {http_port}")

    # Determine transport mode
    if transport_mode == 'http' or transport_mode == 'sse':
        # Use SSE transport for Unity compatibility (standard /sse endpoint)
        transport = 'sse'
        # Use the parsed host and port from URL/args
        http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
        parsed_url = urlparse(http_url)
        host = args.http_host or os.environ.get(
            "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "localhost"
        
        # Safe port parsing for transport logic
        env_port_t = os.environ.get("UNITY_MCP_HTTP_PORT")
        env_port_val = int(env_port_t) if env_port_t else None
        
        port = args.http_port or env_port_val or parsed_url.port or 8080
            
        logger.info(f"Starting FastMCP with SSE transport on {host}:{port}")
        mcp.run(transport=transport, host=host, port=port)
    else:
        
        # Use stdio transport for traditional MCP
        logger.info("Starting FastMCP with stdio transport")
        # 🚨 [CRITICAL] In STDIO mode only, suppress related loggers.
        # Silence Uvicorn and related loggers to prevent stdout pollution.
        # Use the helper from logging_setup
        silence_stdio_loggers(_fh)

        mcp.run(transport='stdio')


# Run the server
if __name__ == "__main__":
    main()
