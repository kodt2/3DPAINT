# 3DPAINT Studio

Интерактивная веб-платформа для 3D-раскрашивания моделей. Проект совмещает в себе элементы геймификации, алгоритмы обработки мешей и современные веб-технологии.

🔗 **[Живое демо](https://3dpaint.tw1.su/)**

---

## О проекте
Данный проект разработан в рамках научно-практической работы. Основная идея — перенос механики "раскрасок по номерам" в трехмерное пространство.

### Ключевые особенности:
* **Интерактивный 3D-движок**: Рендеринг и манипуляция моделями в реальном времени с помощью **Three.js**.
* **Backend на ASP.NET Core**: Надежная архитектура с разделением зон ответственности и авторизацией.
* **Система сегментации для раскраски по номерам**: В разработке.
* **Современный UI**: Адаптивный стеклянный интерфейс (Glassmorphism) с динамической сменой контента без перезагрузки страницы.

## Технологический стек
* **Frontend**: Three.js, JavaScript (ES6+), CSS3 (Custom Properties & Animations).
* **Backend**: C# (ASP.NET Core), Entity Framework.
* **DevOps & Infrastructure**:
    * **Docker & Docker Compose**: контейнеризация приложения, БД и хранилища.
    * **Nginx**: обратный прокси для обработки трафика (живое демо).
    * **SSL**: автоматическая сертификация через Let's Encrypt.
* **Database & storage**: PostgreSQL + S3-совместимое хранилище ассетов (в dev — SeaweedFS, ADR 0009).

## Структура репозитория

```
├── legacy/monolith/   # рабочее монолитное приложение (ASP.NET Core MVC)
├── services/gateway/  # Gateway (ASP.NET Core + YARP): единственный публичный вход
├── infra/             # Docker Compose (dev-стек)
├── docs/              # документация, ADR (docs/adr/)
├── contracts/         # контракты событий (с Фазы 2)
├── AGENTS.md          # инструкции для ИИ-агентов (не в git, локально)
└── Makefile           # up / down / logs / build / test
```

Весь трафик идёт через Gateway на порту `${WEB_PORT:-8080}` (`127.0.0.1`);
порт монолита наружу не публикуется. Health-эндпоинты Gateway:
`/health/live`, `/health/ready` (готовность = монолит доступен).

Целевая архитектура и план миграции на микросервисы описаны в `ARCHITECTURE.md`
и `IMPLEMENTATION_PLAN.md` (удерживаются локально, в git не коммитятся).

## Развертывание (dev)

```bash
git clone https://github.com/kodt2/3DPAINT.git
cd 3DPAINT
cp infra/env/.env.example infra/.env   # при необходимости смените секреты и WEB_PORT
make up   # = docker compose -f infra/compose.yml -f infra/compose.override.yml up -d --build
```

Внимание: Docker Compose v5 читает `.env` из каталога compose-файла
(`infra/`), поэтому файл окружения лежит в `infra/.env` (в git не коммитится).

Приложение: `http://localhost:${WEB_PORT:-8080}` (если порт 8080 занят —
`WEB_PORT=18080` в `infra/.env`).

Полезные команды: `make down`, `make logs`, `make build`.
