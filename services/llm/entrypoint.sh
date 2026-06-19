#!/bin/sh
set -e

# ---------------------------------------------------------------------------
# entrypoint.sh — llama.cpp model download + server startup
#
# Environment variables:
#   MODEL_FILE  (required) — filename of the GGUF model, e.g. llama-3.2-3b.gguf
#   MODEL_URL   (optional) — full URL to download the model if not present
#   N_CTX       (optional) — context window size (default: 4096)
#   N_THREADS   (optional) — CPU threads for inference (default: 4)
# ---------------------------------------------------------------------------

MODEL_PATH="/models/${MODEL_FILE}"

# Guard: MODEL_FILE must be set
if [ -z "${MODEL_FILE}" ]; then
    echo "[entrypoint] ERROR: MODEL_FILE environment variable is not set." >&2
    exit 1
fi

# If the model file is absent, attempt to download it
if [ ! -f "${MODEL_PATH}" ]; then
    if [ -n "${MODEL_URL}" ]; then
        echo "[entrypoint] Model file not found at ${MODEL_PATH}. Downloading from ${MODEL_URL} ..." >&2
        if ! wget -O "${MODEL_PATH}" "${MODEL_URL}"; then
            echo "[entrypoint] ERROR: Failed to download model from ${MODEL_URL}." >&2
            # Remove partial/empty file left by a failed wget
            rm -f "${MODEL_PATH}"
            exit 1
        fi
        echo "[entrypoint] Model downloaded successfully." >&2
    else
        echo "[entrypoint] ERROR: Model file not found at ${MODEL_PATH} and MODEL_URL is not set. Cannot start server." >&2
        exit 1
    fi
fi

# Resolve server binary location
SERVER_BIN="/server"
if [ ! -x "${SERVER_BIN}" ] && [ -x "/llama.cpp/server" ]; then
    SERVER_BIN="/llama.cpp/server"
fi
if [ ! -x "${SERVER_BIN}" ] && [ -x "/llama-server" ]; then
    SERVER_BIN="/llama-server"
fi

echo "[entrypoint] Starting llama.cpp server with model ${MODEL_PATH} ..." >&2

exec "${SERVER_BIN}" \
    -m "${MODEL_PATH}" \
    --host 0.0.0.0 \
    --port 8081 \
    --ctx-size "${N_CTX:-4096}" \
    --threads "${N_THREADS:-4}" \
    --no-gpu
