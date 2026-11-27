COMPOSE := docker compose -f infra/compose.yml -f infra/compose.override.yml
# Dev profile adds infra/compose.dev.yml: the Gateway also allows the Vite
# origin http://localhost:5173 (the browser hits Vite directly, /api is
# proxied to the Gateway). Never used outside development.
COMPOSE_DEV := $(COMPOSE) -f infra/compose.dev.yml
# HTTPS/LAN edge (step H, ADR 0016): Caddy (profile `https`) in front of the
# gateway and S3; only Caddy's ports are published, on $(BIND_ADDR) (default
# 127.0.0.1). The gateway/S3 keep their loopback-only ports.
BIND_ADDR ?= 127.0.0.1
COMPOSE_HTTPS := $(COMPOSE) -f infra/compose.https.yml --profile https
# Auth integration test stand (step 4.1, ADR 0018 — compose-stand strategy):
# a throwaway dedicated Postgres + the .NET test suite (xUnit +
# WebApplicationFactory) on a private network. The stand is removed after the
# run (volumes included), even when a test fails — no leftover containers,
# networks or volumes. Never part of the main/dev/prod stacks.
COMPOSE_AUTH_TEST := docker compose -p 3dpaint-auth-test -f infra/compose.auth-test.yml
AUTH_TEST_DOWN := $(COMPOSE_AUTH_TEST) down --volumes --remove-orphans
# Monolith integration test stand (step 4.3a, ADR 0018): a throwaway
# dedicated Postgres + SeaweedFS + the .NET 8 test suite (xUnit +
# WebApplicationFactory) on a private network. The JWKS endpoint is served
# in-process by the suite itself, so the Auth service is not in the stand.
# Removed after the run (volumes included), even when a test fails.
COMPOSE_MONOLITH_TEST := docker compose -p 3dpaint-monolith-test -f infra/compose.monolith-test.yml
MONOLITH_TEST_DOWN := $(COMPOSE_MONOLITH_TEST) down --volumes --remove-orphans
MONOLITH_CSPROJ := legacy/monolith/LoginAndReg.csproj
# The legacy monolith stays on .NET 8; new services use .NET 10 (ADR 0012).
DOTNET_SDK_8_IMG := mcr.microsoft.com/dotnet/sdk:8.0
DOTNET_SDK_10_IMG := mcr.microsoft.com/dotnet/sdk:10.0

# E2E stand (step 3c.2): a throwaway stack in its own compose project, on
# its own ports, with a raised auth rate limit (100/min). The main stack and
# the production defaults are never touched; the stand (volumes included) is
# removed after the run, even when the tests fail.
E2E_PORT ?= 18090
E2E_S3_PORT ?= 19000
E2E_FILER_PORT ?= 18888
# The monolith is exposed directly on this port (compose.e2e.yml) so the
# upload-limit test can verify it enforces MAX_UPLOAD_BYTES without the
# Gateway. The stand runs with a small upload limit (32 MiB) so the
# over-limit test files stay a few tens of MB.
E2E_WEB_PORT ?= 18081
E2E_MAX_UPLOAD_BYTES := 33554432
# Environment variables are prepended to the compose command: they take
# precedence over infra/.env and apply only to this stand. The S3 filer UI
# port (a dev convenience) gets its own published port so the stand can run
# alongside the main stack.
COMPOSE_E2E := WEB_PORT=$(E2E_PORT) S3_PUBLIC_PORT=$(E2E_S3_PORT) \
    S3_FILER_PORT=$(E2E_FILER_PORT) \
    RATE_LIMIT_REQUESTS_PER_MINUTE=100 MAX_UPLOAD_BYTES=$(E2E_MAX_UPLOAD_BYTES) \
    APP_NETWORK_SUBNET=172.29.0.0/16 \
    WEB_DEV_PORT=18098 AUTH_DEV_PORT=18099 \
    $(COMPOSE) -p 3dpaint-e2e -f infra/compose.e2e.yml
E2E_DOWN := $(COMPOSE) -p 3dpaint-e2e down --volumes --remove-orphans

# HTTPS E2E stand (step H): the same full suite, but the browser talks to
# Caddy (internal CA) over https: the site on E2E_HTTPS_PORT and the S3
# presigned endpoint on E2E_S3_HTTPS_PORT (both via Caddy, own project and
# ports so the stand can run alongside `make e2e`). Chromium trusts Caddy's
# root CA via --ignore-certificate-errors-spki-list (the SHA-256 of its
# SPKI, extracted from the caddy container after the first TLS handshake —
# see E2E_CUSTOM_CA in frontend/playwright.config.ts).
E2E_HTTPS_PORT ?= 18443
E2E_S3_HTTPS_PORT ?= 19443
E2E_HTTPS_WEB_PORT ?= 18083
# Origins are localhost only (no 127.0.0.1): in lan mode Caddy's internal
# cert covers the SNI host MAIN_HOST, so a browser on 127.0.0.1 would fail
# the TLS handshake.
COMPOSE_E2E_HTTPS := \
    BIND_ADDR=127.0.0.1 \
    HTTP_PORT=18082 HTTPS_PORT=$(E2E_HTTPS_PORT) S3_HTTPS_PORT=$(E2E_S3_HTTPS_PORT) \
    MAIN_HOST=localhost S3_HOST=localhost TLS_MODE=lan \
    PUBLIC_ORIGIN=https://localhost:$(E2E_HTTPS_PORT) \
    PUBLIC_S3_ENDPOINT=https://localhost:$(E2E_S3_HTTPS_PORT) \
    S3_CORS_ORIGINS=https://localhost:$(E2E_HTTPS_PORT) \
    WEB_PORT=18091 S3_PUBLIC_PORT=19001 S3_FILER_PORT=18899 \
    WEB_DEV_PORT=18100 AUTH_DEV_PORT=18101 \
    E2E_WEB_PORT=$(E2E_HTTPS_WEB_PORT) \
    RATE_LIMIT_REQUESTS_PER_MINUTE=100 MAX_UPLOAD_BYTES=$(E2E_MAX_UPLOAD_BYTES) \
    APP_NETWORK_SUBNET=172.30.0.0/16 APP_NETWORK_CADDY_IP=172.30.0.10 \
    $(COMPOSE) -p 3dpaint-e2e-https -f infra/compose.e2e.yml -f infra/compose.https.yml --profile https
E2E_HTTPS_DOWN := $(COMPOSE) -p 3dpaint-e2e-https -f infra/compose.e2e.yml -f infra/compose.https.yml --profile https down --volumes --remove-orphans

.PHONY: up up-dev down logs build test test-auth e2e e2e-https up-https down-https check-prod

up: ## Build (if needed) and start the full stack
	$(COMPOSE) up -d --build

up-dev: ## Start the stack with the Vite dev server (HMR) for frontend work
	COMPOSE_PROFILES=dev FRONTEND_UPSTREAM=http://frontend-dev:5173 $(COMPOSE_DEV) up -d --build

# COMPOSE_PROFILES=dev so that profile-gated services (frontend-dev) are
# included in the configuration and actually removed; without it `down`
# leaves the Vite dev container running (and it writes root-owned files into
# frontend/ through the volume mount).
down: ## Stop the stack
	COMPOSE_PROFILES=dev $(COMPOSE) down

logs: ## Tail stack logs
	$(COMPOSE) logs -f

build: ## Build stack images
	$(COMPOSE) build

test: ## Build the monolith, run monolith + gateway tests (in containers; host SDK not required)
	docker run --rm -v "$(CURDIR):/work" -w /work/legacy/monolith $(DOTNET_SDK_8_IMG) dotnet build
	$(MAKE) test-monolith
	docker run --rm -v "$(CURDIR):/work" -w /work/services/gateway/tests $(DOTNET_SDK_10_IMG) dotnet test

test-monolith: ## Run the monolith integration tests (throwaway Postgres+S3 stand, ADR 0018)
	-@$(MONOLITH_TEST_DOWN) >/dev/null 2>&1
	@echo "Running the monolith integration tests (Postgres+S3 stand)..."
	@status=0; \
	$(COMPOSE_MONOLITH_TEST) up --abort-on-container-exit --exit-code-from monolith-test || status=$$?; \
	$(MONOLITH_TEST_DOWN); \
	exit $$status

test-auth: ## Run the Auth service integration tests (throwaway Postgres stand, ADR 0018)
	-@$(AUTH_TEST_DOWN) >/dev/null 2>&1
	@echo "Running the Auth integration tests (Postgres stand)..."
	@status=0; \
	$(COMPOSE_AUTH_TEST) up --abort-on-container-exit --exit-code-from auth-test || status=$$?; \
	$(AUTH_TEST_DOWN); \
	exit $$status

up-https: ## Start the stack with the Caddy TLS edge (https profile, ADR 0016)
	$(COMPOSE_HTTPS) up -d --build

down-https: ## Stop the HTTPS stack (the profile must be on: caddy otherwise is not in the config)
	$(COMPOSE_HTTPS) down

# Production profile validation (step 3d.0, smoke suite): resolves
# infra/compose.yml + infra/compose.https.yml (profile https) and asserts that
# only Caddy is published on the external address, the monolith publishes no
# ports, and no dev/localhost origins leak. Pass CHECK_PROD_ARGS to model a
# specific deployment, e.g.
#   make check-prod CHECK_PROD_ARGS="--bind 192.168.1.100 --origin https://3dpaint.example.com --s3-origin https://3dpaint.example.com --tls prod"
CHECK_PROD_ARGS ?= --tls prod
check-prod: ## Validate the production compose profile (ports + origins)
	./infra/check-prod-stack.sh $(CHECK_PROD_ARGS)

e2e: ## Run Playwright against a throwaway stack (own ports, raised rate limit)
	-@$(E2E_DOWN) >/dev/null 2>&1
	@echo "Starting the e2e stand (gateway http://localhost:$(E2E_PORT), S3 http://localhost:$(E2E_S3_PORT))..."
	@$(COMPOSE_E2E) up -d --build
	@echo "Waiting for the e2e stack to become ready..."
	@ready=0; \
	for i in $$(seq 1 120); do \
		if curl -fsS http://127.0.0.1:$(E2E_PORT)/health/ready >/dev/null 2>&1; then ready=1; break; fi; \
		sleep 2; \
	done; \
	if [ "$$ready" != "1" ]; then \
		echo "error: e2e stand did not become ready in time" >&2; \
		exit 1; \
	fi
	@status=0; \
	(cd frontend && E2E_BASE_URL=http://localhost:$(E2E_PORT) S3_FILER_PORT=$(E2E_S3_PORT) E2E_MONOLITH_URL=http://127.0.0.1:$(E2E_WEB_PORT) npx playwright test) || status=$$?; \
	$(E2E_DOWN); \
	exit $$status

e2e-https: ## Run the full Playwright suite against the HTTPS stand (Caddy, internal CA)
	-@$(E2E_HTTPS_DOWN) >/dev/null 2>&1
	@echo "Starting the https e2e stand (site https://localhost:$(E2E_HTTPS_PORT), S3 https://localhost:$(E2E_S3_HTTPS_PORT))..."
	@$(COMPOSE_E2E_HTTPS) up -d --build
	@echo "Waiting for the https e2e stack to become ready..."
	# SNI must be the cert hostname (localhost): in lan mode the internal cert
	# covers only MAIN_HOST, so https://127.0.0.1 fails the TLS handshake.
	@ready=0; \
	for i in $$(seq 1 120); do \
		if curl -fsSk https://localhost:$(E2E_HTTPS_PORT)/health/ready >/dev/null 2>&1; then ready=1; break; fi; \
		sleep 2; \
	done; \
	if [ "$$ready" != "1" ]; then \
		echo "error: https e2e stand did not become ready in time" >&2; \
		$(E2E_HTTPS_DOWN); \
		exit 1; \
	fi
	# The internal CA materializes after the first TLS handshake (the curl
	# above); Chromium then trusts exactly these certs (SPKI list,
	# playwright.config.ts). Caddy's internal PKI is a root -> intermediate
	# -> leaf chain and it presents only [leaf, intermediate], so the SPKI
	# list needs the intermediate (the root is never presented); Node-side
	# requests (APIRequestContext) do not see Chromium's spki-list flag, so
	# they get the root via NODE_EXTRA_CA_CERTS. Keep the whole block below
	# on one continuation chain with NO shell comments inside: a "#" line
	# swallows the newline and breaks the chain (variables ca/status are
	# lost across separate shell invocations).
	@ca=$$(mktemp -d); \
	$(COMPOSE_E2E_HTTPS) cp caddy:/data/caddy/pki/authorities/local/root.crt $$ca/caddy-root.crt; \
	$(COMPOSE_E2E_HTTPS) cp caddy:/data/caddy/pki/authorities/local/intermediate.crt $$ca/caddy-intermediate.crt; \
	status=0; \
	(cd frontend && E2E_BASE_URL=https://localhost:$(E2E_HTTPS_PORT) \
		E2E_MONOLITH_URL=http://127.0.0.1:$(E2E_HTTPS_WEB_PORT) \
		E2E_S3_HTTPS_URL=https://localhost:$(E2E_S3_HTTPS_PORT) \
		E2E_CUSTOM_CA=$$ca/caddy-root.crt,$$ca/caddy-intermediate.crt \
		NODE_EXTRA_CA_CERTS=$$ca/caddy-root.crt npx playwright test) || status=$$?; \
	rm -rf $$ca; \
	$(E2E_HTTPS_DOWN); \
	exit $$status
