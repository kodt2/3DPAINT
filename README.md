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

## SPA (frontend, с подшага 3a)

Каркас SPA живёт в `frontend/` (React 19 + Vite 7 + TS strict). В dev-стэке он
сервис `frontend` (nginx: SPA-fallback + immutable-кэш `/assets/`), а Gateway
маршрутизирует (ADR 0011):

| Путь | Куда |
|---|---|
| `/api/**` | монолит (JSON API) |
| `/assets/**` | frontend (hashed-ассеты сборки) |
| `/spa/**` | frontend (префикс снимается, SPA-fallback) |
| всё остальное | монолит (Razor-страницы, статика) |

SPA доступен по `http://localhost:${WEB_PORT}/spa/`: `/spa/login`,
`/spa/register` и защищённый `/spa/library` (пока заглушка, список моделей —
в подшаге 3c.2). Старые Razor-страницы остаются на прежних URL
(`/`, `/Account/Auth`, `/Home/Index`).

Режим HMR для работы над фронтендом (опционально):

```bash
make up-dev   # поднимает профиль `dev`: Vite-сервер на 127.0.0.1:5173, Gateway шлёт /spa/** на него
```

В dev-режиме можно работать и напрямую через Vite:
`http://localhost:5173/spa/` (`/api` Vite проксирует в Gateway). Для этого
`make up-dev` (через `infra/compose.dev.yml`) добавляет origin Vite
`http://localhost:5173` в разрешённые для CSRF-проверки Gateway; в
production-стек этот origin не попадает (ADR 0011 §6.1). Vite-контейнер
работает под пользователем хоста (`UID`/`GID`), а `node_modules` живёт в
именованном томе — в `frontend/` не остаётся root-файлов.

Тесты фронтенда: `npm run lint`, `npm test` (Vitest), `npm run build`,
E2E — `npm run test:e2e` (Playwright, ходит **через Gateway**;
базовый URL — `E2E_BASE_URL` или `http://localhost:${WEB_PORT}`,
например `WEB_PORT=18080 npx playwright test`).

**Rate limiting входа (с шага 3c.1a, ADR 0011 §6.2).** Gateway ограничивает
`POST /api/auth/login` и `POST /api/auth/register`: по умолчанию **10
запросов на 60 секунд на клиента**, при превышении — `429`
(ProblemDetails + `Retry-After`). Лимит настраивается env
`RATE_LIMIT_REQUESTS_PER_MINUTE` / `RATE_LIMIT_WINDOW_SECONDS`; для
повторяющихся E2E-прогонов поднимите его (`RATE_LIMIT_REQUESTS_PER_MINUTE=100
make up`) — production-дефолт намеренно консервативен. Ключ клиента — IP
прямого соединения; `X-Forwarded-For` учитывается только от доверенного
прокси (`KNOWN_PROXIES` / `KNOWN_NETWORKS`, по умолчанию пусты).

Типы API-клиента генерируются из OpenAPI монолита (`npm run gen:api` в
`frontend/`, требует работающий dev-стэк; upload-эндпоинт в документе пока не
описан — TD-22).

## Публичный S3 (presigned-ссылки и браузерная загрузка)

Приложение отдаёт presigned-ссылки на S3-хранилище, а браузер по ним скачивает
файлы и (в будущем SPA) загружает напрямую. Поэтому S3 должен быть доступен из
браузера — на отдельном **публичном** эндпоинте, а не по внутреннему адресу
контейнера (`storage:9000`).

### Как это устроено (шаг 3.0, закрытие TD-3)

* Монолит держит **два клиента S3** (S3 API, реализация-независимые настройки):
  * внутренний — `MINIO_ENDPOINT` (`http://storage:9000`): все операции с данными;
  * публичный — `PUBLIC_S3_ENDPOINT`: только для **подписи** presigned URL.
* Dev (`infra/compose.yml`): S3 публикуется на `127.0.0.1:${S3_PUBLIC_PORT:-9000}`,
  `PUBLIC_S3_ENDPOINT=http://localhost:9000`, CORS — флаг SeaweedFS
  `-s3.allowedOrigins=${S3_CORS_ORIGINS}` (точные origin, без `*`).
  Это **только для разработки**: на хосте S3 доступен с root-ключами.

### Настройка сервера (prod-паттерн)

1. Выделите публичный адрес для S3: отдельный хост или **поддомен**
   (например `s3.3dpaint.tw1.su`) с реверс-прокси на S3-порт (nginx/Caddy),
   TLS (Let's Encrypt). Ссылки подписываются с этим Host — он должен совпадать
   с тем, по которому браузер реально обращается.
2. `PUBLIC_S3_ENDPOINT=https://s3.ваш-домен` — точный публичный адрес.
3. `S3_CORS_ORIGINS` — точные origin фронтенда (например
   `https://3dpaint.tw1.su`); в SeaweedFS — тот же список в
   `-s3.allowedOrigins` (у MinIO — policy-конфигурация CORS).
4. Внутренний эндпоинт (`MINIO_ENDPOINT`) остаётся адресом внутри сети
   (поддомен наружу не ведём).

Новые сервисы (Model Service, Фаза 5) используют тот же паттерн: внутренние
операции — по внутреннему эндпоинту, presigned — по публичному.

## Деплой на сервер (production-паттерн)

Стэк поднимается тем же `make up`, но **с server-конфигурацией** в
`infra/.env` (локальный `infra/.env` из dev-машины на сервер **не копировать**
— в нём `ASPNETCORE_ENVIRONMENT=Development`, и тогда в проде будет доступен
Swagger UI и включён dev-профиль OpenAPI):

| Переменная | Значение на сервере | Зачем |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | OpenAPI `/swagger` → `404`, без dev-диагностики |
| `PUBLIC_ORIGIN` | `https://<домен>` (тот же, под которым открыт сайт) | CSRF-проверка мутаций `/api/**` в Gateway (ADR 0011 §6); по умолчанию (`пусто`) разрешён только `http://localhost:{WEB_PORT}` — из браузера по домену мутации получат `403` |
| `S3_CORS_ORIGINS` | тот же origin, что в `PUBLIC_ORIGIN` | браузерные загрузки/скачивания через presigned URL (раздел «Публичный S3») |
| `WEB_PORT` | публичный порт Gateway (обычно `80` за TLS-прокси) | дефолтный origin и порты compose |
| `KNOWN_PROXIES` | адрес reverse-прокси перед Gateway (IP, или CIDR в `KNOWN_NETWORKS`) | rate limiting входа: `X-Forwarded-For` доверяется только от него (ADR 0011 §6.2); без этого все пользователи делят один бакет по IP прокси |
| секреты (`JWT_SECRET`, `MINIO_ROOT_PASSWORD`, `DB_PASSWORD`) | настоящие, не dev-значения из `.env.example` | — |

Важные детали:

* TLS режет внешний reverse proxy (nginx/Caddy), он обязан передавать
  `X-Forwarded-Proto: https` — cookie `jwt` ставится с флагом `Secure`,
  без https-контекста браузер его не сохранит (`UseForwardedHeaders` в
  Gateway подключён).
* `infra/compose.dev.yml` и `make up-dev` на сервере **не использовать**
  (dev-оверлей расширяет разрешённые origins Vite-сервером).
* Проверка после деплоя: `curl -H 'Origin: https://<домен>' -X POST
  https://<домен>/api/auth/login ...` → не `403`; `https://<домен>/swagger` →
  `404`.
