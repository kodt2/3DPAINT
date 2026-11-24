COMPOSE := docker compose -f infra/compose.yml -f infra/compose.override.yml
# Dev profile adds infra/compose.dev.yml: the Gateway also allows the Vite
# origin http://localhost:5173 (the browser hits Vite directly, /api is
# proxied to the Gateway). Never used outside development.
COMPOSE_DEV := $(COMPOSE) -f infra/compose.dev.yml
MONOLITH_CSPROJ := legacy/monolith/LoginAndReg.csproj
# The legacy monolith stays on .NET 8; new services use .NET 10 (ADR 0012).
DOTNET_SDK_8_IMG := mcr.microsoft.com/dotnet/sdk:8.0
DOTNET_SDK_10_IMG := mcr.microsoft.com/dotnet/sdk:10.0

.PHONY: up up-dev down logs build test

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
