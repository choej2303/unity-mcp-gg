import sys
import time
import struct
import json
import logging
import threading
from typing import Any, Optional
from dataclasses import dataclass

logger = logging.getLogger("mcp-for-unity-server")

# Try to import win32file/win32pipe for Named Pipe support (Windows only)
try:
    import win32file
    import win32pipe
    import pywintypes
    _HAS_WIN32 = True
except ImportError:
    _HAS_WIN32 = False


@dataclass
class UnityPipeConnection:
    """Manages a Named Pipe connection to the Unity Editor (Windows IPC)."""
    project_hash: str
    pipe_handle: Any = None
    _io_lock: threading.Lock = None 

    def __post_init__(self):
        self._io_lock = threading.Lock()
        self.pipe_name = f"\\\\.\\pipe\\UnityMCP.{self.project_hash}"

    def connect(self) -> bool:
        if not _HAS_WIN32:
            logger.error("win32 packages (pywin32) not installed; cannot use IPC.")
            return False
            
        if self.pipe_handle:
            return True
            
        try:
            # Wait for pipe to be available (timeout 1s)
            try:
                win32pipe.WaitNamedPipe(self.pipe_name, 1000)
            except pywintypes.error as e:
                # Error 2 = File not found (pipe doesn't exist yet)
                if e.winerror == 2:
                    return False
                raise

            self.pipe_handle = win32file.CreateFile(
                self.pipe_name,
                win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                0, None, 
                win32file.OPEN_EXISTING,
                0, None
            )
            
            logger.info(f"Connected to Named Pipe: {self.pipe_name}")
            
            # Read welcome message (handshake)
            # We expect "WELCOME UNITY-MCP 1 FRAMING=1\n"
            # Just read a bit to clear it.
            _, _ = win32file.ReadFile(self.pipe_handle, 64)
            
            return True
        except Exception as e:
            logger.debug(f"Failed to connect to pipe {self.pipe_name}: {e}")
            self.pipe_handle = None
            return False

    def disconnect(self):
        if self.pipe_handle:
            try:
                win32file.CloseHandle(self.pipe_handle)
            except Exception:
                pass
            self.pipe_handle = None

    def send_command(self, command_type: str, params: dict[str, Any] = None) -> dict[str, Any]:
        if not self.pipe_handle:
            if not self.connect():
                raise ConnectionError("Not connected to Unity Pipe")

        if command_type == 'ping':
             payload = b'ping' # Special handling if host supports it, or standard JSON
             # Our IpcHost expects JSON for everything, stick to JSON for consistency or update Host.
             # Let's wrap ping in JSON to be safe with TransportCommandDispatcher
             payload = json.dumps({'type': 'ping', 'params': {}}).encode('utf-8')
        else:
             payload = json.dumps({'type': command_type, 'params': params or {}}).encode('utf-8')

        with self._io_lock:
            try:
                # Write Header (8 bytes BE length)
                header = struct.pack('>Q', len(payload))
                win32file.WriteFile(self.pipe_handle, header)
                win32file.WriteFile(self.pipe_handle, payload)

                # Read Header
                # Win32 ReadFile returns (err, data)
                _, resp_header = win32file.ReadFile(self.pipe_handle, 8)
                if len(resp_header) < 8:
                    raise ConnectionError("Pipe closed or incomplete header")
                
                resp_len = struct.unpack('>Q', resp_header)[0]
                
                # Read Body
                _, resp_body = win32file.ReadFile(self.pipe_handle, int(resp_len))
                
                response_text = resp_body.decode('utf-8')
                resp_json = json.loads(response_text)
                
                if resp_json.get('status') == 'error':
                    raise Exception(resp_json.get('error'))
                
                if command_type == 'ping':
                     return {"message": "pong"}

                return resp_json.get('result', {})

            except pywintypes.error as e:
                # ERROR_BROKEN_PIPE = 109
                logger.warning(f"Pipe error: {e}")
                self.disconnect()
                raise ConnectionError(f"Pipe broken: {e}")
            except Exception as e:
                logger.error(f"Pipe communication error: {e}")
                self.disconnect()
                raise
