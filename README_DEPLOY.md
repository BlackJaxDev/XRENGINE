# Deployment & CI/CD

This repository uses GitHub Actions to build and deploy both the Editor (desktop) and Server (ASP.NET Core) apps with staged and production environments.

## Branching & environments

| Branch  | Environment | Notes |
|---------|-------------|-------|
| nightly | staging     | Scheduled nightly build + manual dispatch |
| beta    | staging     | Push/deploy on branch updates + manual dispatch |
| release | production  | Push/deploy on branch updates + manual dispatch (approval required) |

Production deployments are gated through a GitHub Environment approval. Staging deploys automatically.

## Versioning

`version/version.json` holds the base semantic version (major/minor/patch). `Tools/versioning/get-version.sh` produces a repo-wide version string:

- nightly: `X.Y.Z-nightly.<yyyymmdd>.<runNumber>`
- beta: `X.Y.Z-beta.<runNumber>`
- release: `X.Y.Z`
- feature/dev: `X.Y.Z-dev.<runNumber>`

The computed version is passed to both the Editor and Server builds and to Docker tags. The release workflow tags `vX.Y.Z` (if missing) and publishes a GitHub Release with generated notes.

## Workflows

`ci.yml` runs on PRs to nightly/beta/release and on pushes to feature branches. `nightly.yml`, `beta.yml`, `release.yml`, and `rollback.yml` cover automation and deployment. All workflows restore, build, and test with warnings treated as errors, and publish test results as artifacts when available.

## Server deployment layout

`deploy/` contains a two-slot deployment setup for zero-downtime publishes.

- `deploy/docker-compose.yml` defines `server_a` and `server_b` containers (ports 5001/5002 by default) that run the same image.
- `deploy/nginx/nginx.conf` proxies to either slot using an include file `active_upstream.conf` (managed by the deployment script).
- `deploy/deploy.sh` runs on the target host, pulls the requested image, brings up the inactive slot, waits for `/healthz`, switches Nginx, stops the old slot, and persists state for rollbacks.

Expected directory on the target host: `/opt/xrengine` (configurable via `DEPLOY_DIR`). Nginx include lives at `/etc/nginx/conf.d/active_upstream.conf`.

Server prerequisites:

- Docker Engine with Compose plugin.
- Nginx installed and reloadable via `nginx -s reload`.
- `curl` available for health checks.
- The deploy user must have permission to run Docker and reload Nginx (sudo as configured on the host).

### Required secrets/variables

Set these GitHub Secrets for deployment workflows:

- `DEPLOY_HOST_STAGING`
- `DEPLOY_HOST_PROD`
- `DEPLOY_USER`
- `DEPLOY_SSH_KEY`
- `DEPLOY_SSH_PORT` (optional; defaults to 22)
- `PUBLIC_BASEURL_STAGING` (optional; used for health verification)
- `PUBLIC_BASEURL_PROD` (optional; used for health verification)

The Docker registry is GitHub Container Registry (GHCR) using the `GITHUB_TOKEN`. Workflows request `contents: write`, `packages: write`, and `id-token: write` (for OIDC) permissions as needed.

### Deployment script inputs (remote host)

`deploy/deploy.sh` expects environment variables:

- `APP_IMAGE` (required): the full image tag (e.g., `ghcr.io/<owner>/<repo>/server:1.2.3`).
- `ENVIRONMENT`: `staging` or `production` (sets `ASPNETCORE_ENVIRONMENT`).
- `PUBLIC_BASEURL`: public URL used by the app (optional but recommended).
- `DEPLOY_DIR`: path to the deployment folder (default `/opt/xrengine`).
- `SERVER_A_PORT` / `SERVER_B_PORT`: backend ports (defaults 5001/5002).
- `STOP_OLD_SLOT`: set to `false` to keep the previous slot running.

The script keeps track of the active slot in `.active_slot` and the previously used image in `.previous_image`.

### Rollback

`rollback.yml` accepts a `version` and `environment` input and redeploys the chosen image tag using the same two-slot switch logic, reusing the stored active slot information on the host.
