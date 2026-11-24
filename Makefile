COMPOSE := docker compose -f infra/compose.yml -f infra/compose.override.yml
# Dev profile adds infra/compose.dev.yml: the Gateway also allows the Vite
# origin http://localhost:5173 (the browser hits Vite directly, /api is
# proxied to the Gateway). Never used outside development.
COMPOSE_DEV := $(COMPOSE) -f infra/compose.dev.yml
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
# Environment variables are prepended to the compose command: they take
# precedence over infra/.env and apply only to this stand. The S3 filer UI
# port (a dev convenience) gets its own published port so the stand can run
# alongside the main stack.
COMPOSE_E2E := WEB_PORT=$(E2E_PORT) S3_PUBLIC_PORT=$(E2E_S3_PORT) \
    S3_FILER_PORT=$(E2E_FILER_PORT) \
    RATE_LIMIT_REQUESTS_PER_MINUTE=100 $(COMPOSE) -p 3dpaint-e2e
E2E_DOWN := $(COMPOSE) -p 3dpaint-e2e down --volumes --remove-orphans

.PHONY: up up-dev down logs build test e2e

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

test: ## Build the monolith and run gateway tests (in a container; host SDK not required)
	docker run --rm -v "$(CURDIR):/work" -w /work/legacy/monolith $(DOTNET_SDK_8_IMG) dotnet build
	docker run --rm -v "$(CURDIR):/work" -w /work/services/gateway/tests $(DOTNET_SDK_10_IMG) dotnet test

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
	(cd frontend && E2E_BASE_URL=http://localhost:$(E2E_PORT) S3_FILER_PORT=$(E2E_S3_PORT) npx playwright test) || status=$$?; \
	$(E2E_DOWN); \
	exit $$status
