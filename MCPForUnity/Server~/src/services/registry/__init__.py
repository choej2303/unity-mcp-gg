"""
Registry package for MCP tool auto-discovery.
"""

from .resource_registry import (
    clear_resource_registry,
    get_registered_resources,
    mcp_for_unity_resource,
)
from .tool_registry import clear_tool_registry, get_registered_tools, mcp_for_unity_tool

__all__ = [
    "mcp_for_unity_tool",
    "get_registered_tools",
    "clear_tool_registry",
    "mcp_for_unity_resource",
    "get_registered_resources",
    "clear_resource_registry",
]
