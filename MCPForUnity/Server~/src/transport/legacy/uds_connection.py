import json
import logging
import os
import socket
import struct
import sys
import threading
from dataclasses import dataclass
from typing import Any

logger = logging.getLogger("mcp-for-unity-server")


@dataclass
class UnityUdsConnection:
    """Manages a Unix Domain Socket connection to the Unity Editor (macOS/Linux)."""

    project_hash: str
    sock: socket.socket = None
    _io_lock: threading.Lock = None

    def __post_init__(self):
        self._io_lock = threading.Lock()
        self.socket_path = f"/tmp/UnityMCP.{self.project_hash}.sock"

    def connect(self) -> bool:
        if self.sock:
            return True

        if not os.path.exists(self.socket_path):
            return False

        try:
            self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.sock.connect(self.socket_path)

            logger.info(f"Connected to UDS: {self.socket_path}")

            # Read welcome message
            # "WELCOME UNITY-MCP 1 FRAMING=1\n"
            data = self.sock.recv(64)
            # We don't strictly need to parse it for UDS as we control endpoint

            return True
        except Exception as e:
            logger.debug(f"Failed to connect to UDS {self.socket_path}: {e}")
            self.sock = None
            return False

    def disconnect(self):
        if self.sock:
            try:
                self.sock.close()
            except Exception:
                pass
            self.sock = None

    def send_command(
        self, command_type: str, params: dict[str, Any] = None
    ) -> dict[str, Any]:
        if not self.sock:
            if not self.connect():
                raise ConnectionError("Not connected to Unity UDS")

        if command_type == "ping":
            payload = json.dumps({"type": "ping", "params": {}}).encode("utf-8")
        else:
            payload = json.dumps({"type": command_type, "params": params or {}}).encode(
                "utf-8"
            )

        with self._io_lock:
            try:
                # Write Header (8 bytes BE length)
                header = struct.pack(">Q", len(payload))
                self.sock.sendall(header)
                self.sock.sendall(payload)

                # Read Header
                resp_header = self._read_exact(8)
                resp_len = struct.unpack(">Q", resp_header)[0]

                # Read Body
                resp_body = self._read_exact(int(resp_len))

                response_text = resp_body.decode("utf-8")
                resp_json = json.loads(response_text)

                if resp_json.get("status") == "error":
                    raise Exception(resp_json.get("error"))

                if command_type == "ping":
                    return {"message": "pong"}

                return resp_json.get("result", {})

            except (BrokenPipeError, ConnectionResetError) as e:
                logger.warning(f"UDS Pipe error: {e}")
                self.disconnect()
                raise ConnectionError(f"UDS broken: {e}")
            except Exception as e:
                logger.error(f"UDS communication error: {e}")
                self.disconnect()
                raise

    def _read_exact(self, count: int) -> bytes:
        data = bytearray()
        while len(data) < count:
            chunk = self.sock.recv(count - len(data))
            if not chunk:
                raise ConnectionError("Connection closed during read")
            data.extend(chunk)
        return bytes(data)
