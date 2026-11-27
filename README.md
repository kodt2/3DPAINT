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

Полезные команды: `make down`, `make logs`, `make build`, `make e2e`.

## SPA (frontend, с подшага 3a)

Каркас SPA живёт в `frontend/` (React 19 + Vite 7 + TS strict). В dev-стэке он
сервис `frontend` (nginx: SPA-fallback + immutable-кэш `/assets/`), а Gateway
маршрутизирует (ADR 0011):

| Путь | Куда |
|---|---|
| `/api/**` | монолит (JSON API) |
| `/assets/**` | frontend (hashed-ассеты сборки) |
| `/spa/**` | frontend (префикс снимается, SPA-fallback) |
| `/` (GET/HEAD) | 302 → `/spa/` |
| `/Home/**`, `/Account/**` (GET/HEAD) | 302 → `/spa/` (старые Razor-страницы, шаг 3d.2) |
| всё остальное | 404 от Gateway (без catch-all, шаг 3d.2) |

SPA доступен по `http://localhost:${WEB_PORT}/spa/`: `/spa/login`,
`/spa/register`, защищённый `/spa/library` (с шага 3c.2: список моделей,
presigned-превью, пагинация, удаление с подтверждением; с шага 3c.4:
экран загрузки) и `/spa/models/:id` — 3D-вьюер (с шага 3c.3).
Razor-интерфейс удалён (шаг 3d.2): корень `/` и старые пути `/Home/**`,
`/Account/**` редиректят на `/spa/` (302); неизвестные пути — 404 от
Gateway (catch-all в монолит убран).

**Библиотека (с шага 3c.2, ADR 0013).** SPA ходит в новый JSON-эндпоинт
монолита `/api/models` (`GET` список с `page`/`pageSize` 1..100, `GET`/{id}
деталь с presigned-ссылками, `DELETE`/{id} — запись + S3-объекты, повторный
`DELETE` идемпотентен). Владелец проверяется в самом SQL
(`id = @id AND user_id = @userId`): чужой `id` — обычный `404`. DTO не
несут внутренних хостов, S3-ключей как отдельных полей и данных владельца.
Маршрут совпадает с целевым API из ARCHITECTURE.md §7.2 — в Фазе 5 меняется
только бэкенд. Старые Razor-эндпоинты `/api/files/**` не тронуты.

**3D-вьюер (с шага 3c.3, ADR 0014; сжатие — шаг 3c.5).**
`/spa/models/:id` — просмотр GLB: Three.js 0.187 + @react-three/fiber
(ленивый чанк, three.js не грузится на login/library). GLB скачивается из
S3 по presigned URL с прогрессом; внешние URI внутри недоверенного файла
блокируются (доверенный `LoadingManager`: только `blob:`/`data:` и
локальные ассеты декодеров), истёкший URL — один повтор со свежей
ссылкой, потеря WebGL-контекста — оверлей с перезагрузкой сцены.
Файлы, не являющиеся GLB (`.stl`, `.obj`, `.step`, `.stp`), показывают
уведомление и ссылку «Скачать».

Сжатые форматы декодируются **локальными** копиями декодеров из
`node_modules/three` (без CDN): Draco, Meshopt, KTX2/BasisU. Ассеты
копирует `scripts/sync-decoders.mjs` в `public/assets/decoders/three-<версия>/`
(hooks `predev`/`prebuild`); чанк декодеров грузится лениво — только когда
файл их требует. `extensionsRequired` с неподдерживаемым three r187
расширением → файл отклоняется до парсинга с именем расширения в
сообщении; сбой декодирования и таймаут (60 с) — отдельные сообщения.
Диагностика файла: `npm run inspect:glb <файл.glb>` (в `frontend/`).

**Загрузка моделей (с шага 3c.4, ADR 0015, временный экран).**
`/spa/library` — панель загрузки на multipart-эндпоинт монолита
`POST /api/files/upload` (кнопка + drag&drop, очередь до 2 файлов,
прогресс через XHR `upload.onprogress`, «Отменить»/«Повторить» у каждого
файла). Клиентские проверки (расширение, размер, пустой файл) идут **до**
отправки, значения — из публичного `GET /api/config` (не хардкод);
сервер повторно проверяет и дополнительно требует для `.glb` валидный
заголовок GLB (magic `glTF`, версия 2, длина ≤ размеру файла), иначе
`400 ProblemDetails` до записи в S3/БД. Лимит размера — одна переменная
`MAX_UPLOAD_BYTES` (дефолт 100 MiB), читаемая и Gateway, и монолитом:
превышение — `413` на любом из краёв (у монолита — читаемый
`File too large`). Отмена загрузки не оставляет записи в БД и объекта в
S3. Экран временный: в Фазе 5 загрузка перейдёт на presigned-флоу
(ADR 0003) и этот экран будет заменён; список форматов SPA пока только
GLB (вьюер понимает только GLB, ADR 0014) — расширение списка делается
осознанно, вместе с поддержкой вьюера/обработки.

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
E2E — **`make e2e`** (Playwright, ходит **через Gateway** отдельного
tестового стенда: свой compose-проект `3dpaint-e2e`, свой порт
`E2E_PORT`, по умолчанию 18090, и временный лимит входа
`RATE_LIMIT_REQUESTS_PER_MINUTE=100`). Основной стек и production-дефолты
не трогаются, а по завершении стенд гасится вместе со своими данными —
прогоны можно повторять, не упираясь в лимит. Если порты заняты:
`make e2e E2E_PORT=18190 E2E_S3_PORT=19100 E2E_FILER_PORT=18898` Ручной прогон против уже
поднятого стека: `cd frontend && E2E_BASE_URL=http://localhost:${WEB_PORT} npx playwright test`
(лимит входа 10/60 с может сработать при повторных прогонах).

**Rate limiting входа (с шага 3c.1a, ADR 0011 §6.2).** Gateway ограничивает
`POST /api/auth/login` и `POST /api/auth/register`: по умолчанию **10
запросов на 60 секунд на клиента**, при превышении — `429`
(ProblemDetails + `Retry-After`). Лимит настраивается env
`RATE_LIMIT_REQUESTS_PER_MINUTE` / `RATE_LIMIT_WINDOW_SECONDS` —
production-дефолт намеренно консервативен (повторные E2E-прогоны идут
через тестовый стенд `make e2e` с временным лимитом 100/мин). Ключ
клиента — IP прямого соединения; `X-Forwarded-For` учитывается только от
доверенного прокси (`KNOWN_PROXIES` / `KNOWN_NETWORKS`, по умолчанию
пусты).

Типы API-клиента генерируются из **двух** OpenAPI-документов (`npm run gen:api`
в `frontend/`, требует работающий dev-стэк `make up`): `/api/auth/**` и
`/api/me` — из Auth, остальное — из монолита; скрипт
`frontend/scripts/gen-api.mjs` склеивает документ и вызывает
`openapi-typescript`. Gateway `/swagger` не маршрутизирует (3d.2), поэтому в
dev оба документа доступны по loopback-портам из `infra/compose.override.yml`
(`AUTH_DEV_PORT`/`WEB_DEV_PORT`, дефолты 18096/18097). `npm run gen:api:check`
— сравнение регенерации с закоммиченным `src/api/schema.ts` (CI-гигиена).
Upload-эндпоинт в документе пока не описан — TD-22.

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
| `MAX_UPLOAD_BYTES` | байты (дефолт `104857600` = 100 MiB) | единый лимит загрузки: Gateway (Kestrel + YARP) и монолит (body-лимиты + приложение, `413 ProblemDetails`) читают одну и ту же переменную (ADR 0015); значение отдаётся SPA в `GET /api/config` |
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

## Доступ по сети: HTTPS-контур Caddy (шаг H, ADR 0016)

Для LAN и production у стэка есть TLS-контур **Caddy**
(образ `caddy:2.11.9-alpine`, тег закреплён). Наружу публикуются **только
порты Caddy** на адресе `BIND_ADDR` (дефолт `127.0.0.1`); Gateway и S3
остаются loopback-only и из сети не видны.

```bash
make up-https    # стэк + Caddy (профиль https), по умолчанию для localhost
make down-https  # остановить (профиль обязателен, иначе Caddy не в конфиге)
```

Порты хоста (контейнерные фиксированы: 80/443/9443): `HTTP_PORT` (80 —
ACME/редирект, prod), `HTTPS_PORT` (443 — сайт), `S3_HTTPS_PORT` (9443 — S3
в lan-режиме; в prod S3 тоже на 443, роутинг по SNI).

Для LAN-доступа клиентов:

```bash
BIND_ADDR=0.0.0.0 MAIN_HOST=<имя-машины> PUBLIC_ORIGIN=https://<имя-машины>:8443 \
  HTTPS_PORT=8443 make up-https
```

Внутренний сертификат покрывает только хост `MAIN_HOST` (SNI): `PUBLIC_ORIGIN`
и `S3_CORS_ORIGINS` задавайте под тот же хост (порты в URL допустимы).

### Режимы TLS (`TLS_MODE`)

| Режим | Сертификат | HSTS | Когда |
|---|---|---|---|
| `lan` (дефолт) | внутренний CA Caddy (`tls internal`) | **выключен** | локальная сеть: клиенты один раз ставят корневой сертификат |
| `prod` | Let's Encrypt (обязателен `ACME_EMAIL`) | **включён** | production: публичный домен, Caddy достижим на `:80`/`:443` |

### Установка корневого сертификата (LAN)

Корневой сертификат — в контейнере
`caddy:/data/caddy/pki/authorities/local/root.crt`; он стабилен между
пересозданиями контейнера (том `caddydata`). Копия:

```bash
docker compose -f infra/compose.yml -f infra/compose.override.yml \
  -f infra/compose.https.yml --profile https \
  cp caddy:/data/caddy/pki/authorities/local/root.crt 3dpaint-caddy-root.crt
```

- **Linux (Debian/Ubuntu):** `sudo cp 3dpaint-caddy-root.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates` (имя файла обязательно оканчивается `.crt` — так браузеры и принимают корень);
- **macOS:** открыть `.crt`, перетащить сертификат в «Ключи» и поставить доверие «Всегда доверять»;
- **Windows:** импорт в «Достоверные корневые центра сертификации» (`certlm.msc`);
- **Android:** Настройки → Безопасность → Установить сертификаты → «CA»; на **iOS** — профиль конфигурации (Настройки → Ограничения → Профиль).

### Production

`TLS_MODE=prod` + `ACME_EMAIL`, `PUBLIC_ORIGIN=https://домен`,
`PUBLIC_S3_ENDPOINT=https://s3.домен` (Let's Encrypt не выдаёт сертификаты
на IP), `BIND_ADDR` — адрес, видимый из интернета, порты 80/443 открыты.
Остальное — раздел «Деплой на сервер (production-паттерн)»:
`ASPNETCORE_ENVIRONMENT=Production`, `S3_CORS_ORIGINS`, секреты.
`KNOWN_PROXIES` в `make up-https` уже задан compose (фиксированный IP Caddy
`172.28.0.10`) — XFF доверяется только от него (ADR 0016 §3).

### E2E по HTTPS

`make e2e-https` — одноразовый стенд: полный Playwright-прогон **через Caddy**
(внутренний CA; Chromium доверяет CA через SPKI-список, Node — через
`NODE_EXTRA_CA_CERTS`; в системный trust-store хоста ничего не ставится).
Проверяет, в частности: Draco-модель открывается по HTTPS, декодеры идут с
origin сайта, presigned S3 — только `https://`, mixed-content/CORS-ошибок нет,
HSTS в lan-режиме отсутствует. Plain `make e2e` (HTTP) не меняется.
