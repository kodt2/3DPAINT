COMPOSE := docker compose -f infra/compose.yml -f infra/compose.override.yml
MONOLITH_CSPROJ := legacy/monolith/LoginAndReg.csproj
# The legacy monolith stays on .NET 8; new services use .NET 10 (ADR 0012).
DOTNET_SDK_8_IMG := mcr.microsoft.com/dotnet/sdk:8.0
DOTNET_SDK_10_IMG := mcr.microsoft.com/dotnet/sdk:10.0

.PHONY: up down logs build test

up: ## Build (if needed) and start the full stack
	$(COMPOSE) up -d --build

down: ## Stop the stack
	$(COMPOSE) down

logs: ## Tail stack logs
	$(COMPOSE) logs -f

build: ## Build stack images
	$(COMPOSE) build

test: ## Build the monolith and run gateway tests (in a container; host SDK not required)
	docker run --rm -v "$(CURDIR):/work" -w /work/legacy/monolith $(DOTNET_SDK_8_IMG) dotnet build
	docker run --rm -v "$(CURDIR):/work" -w /work/services/gateway/tests $(DOTNET_SDK_10_IMG) dotnet test
