import logging
import os
import sys
from logging.handlers import RotatingFileHandler

from core.config import config

# Global handler reference to be shared
_file_handler = None


def setup_logging():
    """
    Configures the root logger and sets up a rotating file handler.
    Returns the file handler instance so it can be attached to other loggers.
    """
    global _file_handler

    # Configure root logger
    logging.basicConfig(
        level=getattr(logging, config.log_level),
        format=config.log_format,
        stream=None,  # None -> defaults to sys.stderr
        force=True,
    )

    logger = logging.getLogger("mcp-for-unity-server")

    # Set up rotating file handler
    try:
        if os.name == "nt":
            _base_dir = os.environ.get("APPDATA") or os.path.expanduser(
                "~\\AppData\\Roaming"
            )
            _log_dir = os.path.join(_base_dir, "UnityMCP", "Logs")
        else:
            _log_dir = os.path.join(
                os.path.expanduser("~/Library/Application Support/UnityMCP"), "Logs"
            )
        os.makedirs(_log_dir, exist_ok=True)

        _file_path = os.path.join(_log_dir, "unity_mcp_server.log")
        _file_handler = RotatingFileHandler(
            _file_path, maxBytes=512 * 1024, backupCount=2, encoding="utf-8"
        )
        _file_handler.setFormatter(logging.Formatter(config.log_format))
        _file_handler.setLevel(getattr(logging, config.log_level))

        logger.addHandler(_file_handler)
        logger.propagate = False  # Prevent double logging to root logger

        # Route telemetry logger to the same file
        try:
            tlog = logging.getLogger("unity-mcp-telemetry")
            tlog.setLevel(getattr(logging, config.log_level))
            tlog.addHandler(_file_handler)
            tlog.propagate = False
        except Exception as exc:
            logger.debug("Failed to configure telemetry logger", exc_info=exc)

    except Exception as exc:
        logger.debug("Failed to configure main logger file handler", exc_info=exc)

    # Quieten noisy third-party loggers
    for noisy in ("httpx", "urllib3", "mcp.server.lowlevel.server"):
        try:
            logging.getLogger(noisy).setLevel(
                max(logging.WARNING, getattr(logging, config.log_level))
            )
            logging.getLogger(noisy).propagate = False
        except Exception:
            pass

    return _file_handler


def silence_stdio_loggers(file_handler=None):
    """
    Silences noisy loggers when running in stdio mode to prevent stdout pollution.
    Attaches the file handler to them so logs are not lost, just redirected.
    """
    for name in (
        "uvicorn",
        "uvicorn.error",
        "uvicorn.access",
        "starlette",
        "docket",
        "docket.worker",
        "fastmcp",
    ):
        lg = logging.getLogger(name)
        lg.setLevel(logging.WARNING)
        lg.propagate = False

        if file_handler and file_handler not in lg.handlers:
            lg.addHandler(file_handler)
