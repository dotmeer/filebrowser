# User guide

filebrowser is a file manager for one owner or a small group on a **trusted network**. Do not expose it to the internet. The project was created through vibe coding with AI assistance.

## Features and limitations

Multiple roots and a combined tree, name search, folder creation and rename; background copy, move and permanent delete with progress, cancellation and history; file upload without overwriting, file or streaming ZIP download, image, PDF and text preview (up to 512 KiB). The interface supports English, Russian and themes. Refresh to see external file changes.

Symbolic links cannot be opened or modified; ZIP skips links inside folders after confirmation. Special files are unsupported. There is no trash, editor, archive extraction, folder upload, resumable upload, groups, quotas or audit log. Incomplete tasks do not resume after restart. Search is limited to 500 results and 60 seconds. Completed items are not rolled back when a batch operation fails.

## Docker Compose

Image: [`dotmeer/filebrowser`](https://hub.docker.com/r/dotmeer/filebrowser). On Linux, create `config.yaml` and a `data` directory next to `compose.yaml`. Files in `data` must be accessible to the container UID/GID (1654 by default); change both `user` and `chown` if needed.

`config.yaml`:

```yaml
workers: 2
uploads:
  maxFileBytes: 8589934592
roots:
  - name: files
    path: /data
```

`compose.yaml`:

```yaml
services:
  config-permissions:
    image: dotmeer/filebrowser:latest
    user: "0:0"
    entrypoint: ["/bin/sh", "-c", "chown -R 1654:1654 /config"]
    volumes:
      - config:/config
  filebrowser:
    image: dotmeer/filebrowser:latest
    user: "1654:1654"
    environment:
      AUTH_MODE: LOCAL
    ports:
      - "127.0.0.1:8080:8080"
    depends_on:
      config-permissions:
        condition: service_completed_successfully
    volumes:
      - config:/config
      - ./config.yaml:/config/config.yaml:ro
      - ./data:/data
volumes:
  config:
```

Run `docker compose up -d` and open `http://127.0.0.1:8080/`. To allow LAN access, change the binding and restrict access at the network level. Stop with `docker compose down` without `-v`: deleting the volume destroys the database. To build from source, clone the [GitHub repository](https://github.com/dotmeer/filebrowser) and run its included Compose file with `docker compose up --build`.

## Access and configuration

Set `AUTH_MODE` at startup: `LOCAL` enables accounts, while `ANONYMOUS` allows access without login to all roots, settings and shared history. **If unset, ANONYMOUS is used.** An empty or invalid value is rejected. On first LOCAL startup the first visitor sets the `admin` password; restrict server access until setup is complete. Admin manages users and read or write permissions per root; new users must change their temporary password. Switching to ANONYMOUS retains accounts in the database but no longer requires login.

YAML is read once at startup. `workers` is 1–16, default 2; `uploads.maxFileBytes` is a positive per-file limit in bytes, default 8 GiB. At least one root is required: `name` matches `[a-z0-9][a-z0-9_-]{0,63}`, names are case-insensitively unique, and paths are absolute and non-overlapping. The name identifies the root in links and permissions: changing it changes that identity. Container paths must match mounts. An unavailable root does not prevent startup and is rechecked on access; invalid configuration or an unavailable database stops startup.

Configuration defaults to `/config/config.yaml`, and the database to `app.db` next to it; override with `ConfigPath` and `DatabasePath`. Do not put configuration or the database inside an accessible root: administrators and anonymous visitors can see service files, and alternate paths through mounts or hardlinks are not isolated. Paths and OS permissions do not protect against a hostile local process replacing files between checks and access.

## Operations

Persist `/config`, keep SQLite on a local filesystem, and use one application instance per database. Back up YAML and the database while the application is stopped, and user files separately; there is no automatic database recovery. Recreating the container loses Data Protection keys: existing sessions end and users must sign in again; accounts remain.

Reset the `admin` password with the application stopped (enter the password on the console):

```sh
docker compose stop filebrowser
docker compose run --rm -it filebrowser reset-admin-password
docker compose up -d filebrowser
```

`/health/live` and `/health/ready` respond while the server is running but do not check the database or roots. Logs are JSON on stdout; OTLP is optional and enabled with standard variables such as `OTEL_EXPORTER_OTLP_ENDPOINT`. Behind a reverse proxy, adjust upload and ZIP limits and timeouts; trust forwarded headers only from explicitly configured proxies (`ForwardedHeaders__Enabled`, `ForwardedHeaders__KnownProxies__0`).

## Development

.NET 10, ASP.NET Core, Blazor Interactive Server, Minimal API, SQLite, YamlDotNet, OpenTelemetry and xUnit. Checks: `dotnet build`, `dotnet test`, `docker build -t filemanager .`, `docker compose config`. See the [architecture](SPEC.md) and [manual scenarios](MANUAL_TEST_SCENARIOS.md), both in Russian. Full manual acceptance of authentication, including mode switching and revoking access during long operations, remains incomplete; there is no dedicated regression test for `SQLITE_BUSY`/`SQLITE_LOCKED`.
