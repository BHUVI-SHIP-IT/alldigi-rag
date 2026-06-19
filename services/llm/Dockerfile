FROM ghcr.io/ggml-org/llama.cpp:server

# Install curl for the healthcheck defined in docker-compose
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Copy the entrypoint script and make it executable
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8081

ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
