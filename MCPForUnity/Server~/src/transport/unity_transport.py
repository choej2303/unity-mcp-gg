"""Transport helpers for routing commands to Unity."""

from __future__ import annotations

import asyncio
import inspect
import os
from typing import Awaitable, Callable, TypeVar

from fastmcp import Context

from models.unity_response import normalize_unity_response
from services.tools import get_unity_instance_from_context
from transport.plugin_hub import PluginHub

T = TypeVar("T")


def _is_http_transport() -> bool:
    return os.environ.get("UNITY_MCP_TRANSPORT", "stdio").lower() == "http"


def _current_transport() -> str:
    """Expose the active transport mode as a simple string identifier."""
    return "http" if _is_http_transport() else "stdio"


async def send_with_unity_instance(
    send_fn: Callable[..., Awaitable[T]],
    unity_instance: str | None,
    *args,
    **kwargs,
) -> T:
    if _is_http_transport():
        if not args:
            raise ValueError("HTTP transport requires command arguments")
        command_type = args[0]
        params = args[1] if len(args) > 1 else kwargs.get("params")
        if params is None:
            params = {}
        if not isinstance(params, dict):
            raise TypeError("Command parameters must be a dict for HTTP transport")
        raw = await PluginHub.send_command_for_instance(
            unity_instance,
            command_type,
            params,
        )
        return normalize_unity_response(raw)

    if unity_instance:
        kwargs.setdefault("instance_id", unity_instance)
    return await send_fn(*args, **kwargs)
