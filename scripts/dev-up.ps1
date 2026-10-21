# Starts the local stack (FD-02): PostgreSQL+PostGIS+pgvector, Redis, MinIO.
docker compose -f "$(Split-Path $PSScriptRoot -Parent)\infra\docker-compose.yml" up -d --build --wait
