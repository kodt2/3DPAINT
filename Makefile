COMPOSE := docker compose -f infra/compose.yml -f infra/compose.override.yml
MONOLITH_CSPROJ := legacy/monolith/LoginAndReg.csproj

.PHONY: up down logs build test

up: ## Build (if needed) and start the full stack
	$(COMPOSE) up -d --build

down: ## Stop the stack
	$(COMPOSE) down

logs: ## Tail stack logs
	$(COMPOSE) logs -f

build: ## Build stack images
	$(COMPOSE) build

test: ## Build the monolith (no test projects yet, TD-17)
	dotnet build $(MONOLITH_CSPROJ)
