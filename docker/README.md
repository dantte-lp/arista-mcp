# arista-mcp Postgres container

Bundles PostgreSQL 18 + pgvector + VectorChord + vchord_bm25 + pg_tokenizer + pg_trgm on
top of the upstream `tensorchord/vchord-suite:pg18-latest` image. `init.sql` creates all
extensions, registers `english_analyzer`, and applies HNSW / memory defaults at the
database level so they survive restarts.

## Build

    cd docker
    podman-compose build postgres

## Start

    podman-compose up -d postgres
    # wait for healthcheck
    until pg_isready -h localhost -p 5434 -U arista; do sleep 1; done

## Connect

    psql -h localhost -p 5434 -U arista -d arista

### Windows / Podman (WSL2 backend) caveat

With `UserModeNetworking=false` (the default on older Podman machines) the `5434:5432`
bind lives inside the podman-machine WSL distro only — `localhost:5434` from Windows
fails. Two fixes:

1. **Preferred**: one-time enable user-mode networking (gvproxy auto-forwards to Windows
   localhost):

       podman machine stop
       podman machine set --user-mode-networking
       podman machine start

2. **Temporary**: target the WSL IP directly. Find it with
   `wsl -d podman-machine-default -- ip -4 addr show eth0 | grep inet` and use it as `Host=`
   in connection strings.

The `compose.yaml` uses `0.0.0.0:5434:5432` so the port binds to the WSL network interface,
which Windows reaches via the `vEthernet (WSL)` adapter.

## Verify extensions

    psql -h localhost -p 5434 -U arista arista -c "\dx"

Expected rows: `vector`, `vchord`, `vchord_bm25`, `pg_tokenizer`, `pg_trgm`.

## Backup / restore

    podman exec arista-mcp-postgres pg_dump -U arista arista | gzip > backup.sql.gz
    gunzip < backup.sql.gz | podman exec -i arista-mcp-postgres psql -U arista arista

## Reset

    podman-compose down -v
    podman-compose up -d postgres
