# ADR 0011: SPA + cookie-аутентификация монолита и схема маршрутов Gateway

- **Статус:** принято (2026-11-23, шаг 3a)

## Контекст

С шага 3a в репозитории есть `frontend/` (SPA на React + Vite). До Фазы 4
аутентификацию по-прежнему отдаёт монолит (JWT HS256 в httpOnly-cookie `jwt`).
SPA и `/api/**` должны работать вместе **без CORS**: обе половины — один
оригин через Gateway.

Отдельно: реальный 3D-вьюер модели — **новая функция** (не перенос: в текущем
Razor-UI превью карточек — SVG-заглушки, ветка Three.js в `home-dashboard.js`
для загруженных файлов не выполняется).

## Решение

### 1. Аутентификация SPA через cookie монолита (до Фазы 4)

- Токен — JWT HS256 в cookie `jwt`, атрибуты (ставятся в `AccountController`):
  - `HttpOnly=true` — JS SPA не читает токен;
  - `Secure=true` — браузеры принимают такие cookie на
    `http://localhost` / `http://127.0.0.1` (trustworthy origins, проверено
    headless Chromium в dev-стэке); на любом другом plain-HTTP хосте (LAN-IP
    и т.п.) cookie не пойдёт — нужен TLS;
  - `SameSite=Lax` — cookie не уходит на кросс-сайтовые GET-запросы
    (основная CSRF-защита для SPA);
  - `Path=/`.
- SPA ходит в `/api/**` тем же браузером: `fetch(..., { credentials:
  'same-origin' })` (в `openapi-fetch` — дефолт), cookie подставляется
  автоматически.
- **CORS на `/api` не нужен и не настраивается**: SPA и API — один оригин
  Gateway.
- **Antiforgery:** в монолите его нет (ни Razor, ни `/api`). Мутации
  `/api/**` защищены цепочкой из трёх уровней (см. раздел 6 «CSRF-защита»):
  `SameSite=Lax` + Origin-проверка в Gateway + требование
  `Content-Type: application/json` для JSON-эндпоинтов монолита.
  Antiforgery-токены для JSON API не используются (обоснование — там же);
  финальное решение по CSRF-модели — при выносе аутентификации на
  Auth Service (Фаза 4).

### 2. `/api/**` отвечает 401 JSON, а не challenge/редиректом

SPA парсит JSON. Middleware монолита (после `UseAuthentication()`, до
`UseAuthorization()`): для пути `/api/**` на эндпоинтах с `[Authorize]`
(без `[AllowAnonymous]`) при неудачной аутентификации отвечает:

```
401 Unauthorized
Content-Type: application/json
WWW-Authenticate: Bearer
{"code":"unauthorized","message":"Authentication required"}
```

Публичные `/api`-эндпоинты (например `/api/health`) и все не-`/api` пути
ведут себя как раньше (legacy challenge/редирект на страницу входа).

### 3. Схема маршрутов Gateway и приоритеты

| Приоритет (`Order`) | Маршрут | Куда | Примечание |
|---|---|---|---|
| 1 | `/api/{**catch-all}` | монолит | JSON API; лимит тела `MAX_UPLOAD_BYTES` |
| 2 | `/assets/{**catch-all}` | frontend (nginx) | hashed-ассеты Vite (`immutable`-кэш) |
| 3 | `/spa/{**catch-all}` | frontend (nginx) | префикс `/spa` снимается (`PathRemovePrefix`) — nginx отдаёт SPA-fallback |
| 4 | `{**catch-all}` | монолит | всё остальное: Razor-страницы, статика, `/health/*` |

- Перенос экрана = добавить/сдвинуть маршрут (один экран — один PR, Фаза 3).
- Префикс `/legacy` **не вводится**: старые Razor-страницы остаются на
  прежних URL (catch-all), новые — под `/spa/**`.
- `/health/live`, `/health/ready` отдаёт сам Gateway (не проксирует).
- Все маршруты: оригинальный `Host` сохраняется, добавляются
  `X-Forwarded-*`; upstream-кластеры работают на HTTP/1.1 (YARP по
  умолчанию шлёт h2c — зафиксировано явно).
- Dev-режим (`make up-dev`): Gateway маршрутизирует `/spa/**` на Vite
  dev-сервер (`FRONTEND_UPSTREAM=http://frontend-dev:5173`), `/api/**`
  проксирует Vite сам через Gateway.

### 4. Frontend-стек

- **React 19 + TypeScript (strict) + Vite 7** — выбор по консервативным
  стабильным мажорам (Vite 7, TS 5.9, ESLint 9, Vitest 3; не новейшие
  нестабильные мажоры).
- 3D: **Three.js** через `@react-three/fiber` + `@react-three/drei`
  (подключается при реализации вьюера, Фазы 3c/6.5).
- Состояние сервера: **TanStack Query**. Роутинг: **React Router**
  (`basename="/spa"`). Тесты: **Vitest** + Testing Library; E2E —
  **Playwright** (появится с первым переносимым экраном).
- API-клиент: **`openapi-typescript`** (типы) + **`openapi-fetch`** (запросы)
  из OpenAPI монолита (`npm run gen:api`); OpenAPI отдаёт монолит по
  `/swagger/v1/swagger.json` **только при
  `ASPNETCORE_ENVIRONMENT=Development`** (dev-стэк задаёт его через
  `infra/.env`; в production-подобных стэках `/swagger/**` — `404`), только
  `/api/**` (фильтр `ApiOnlyDocumentFilter`).
- Docker: multi-stage `node:20.19.5-alpine3.22` (build, `npm ci`) →
  `nginx:1.28.3-alpine` (SPA-fallback, immutable-кэш `/assets/`).
- Будущий paint-модуль живёт в `frontend/src/features/paint` как чистый
  TS-модуль, независимый от React (ADR 0010).

### 5. S3 CORS и origin

CORS бакета (`S3_CORS_ORIGINS`) должен содержать **оригин Gateway** — тот,
что в адресной строке браузера (в dev: `http://localhost:18080` и
`http://127.0.0.1:18080`, т.к. origin совпадает точечно). Presigned URL
содержат `PUBLIC_S3_ENDPOINT` (ADR 0003/шаг 3.0), поэтому браузер ходит в S3
напрямую — CORS там обязателен, на `/api` — нет.

## 6. CSRF-защита (дополнено в шаге 3b.0, 2026-11-23)

Угроза: cookie `jwt` ставится без явного `Domain` и с `SameSite=Lax`.
Lax покрывает большинство сценариев (кросс-сайтовый POST не несёт cookie),
но не все (редирект-цепочки, ряд исторических браузерных поведений).
Поэтому мутации `/api/**` защищены ещё двумя уровнями:

1. **Origin-проверка в Gateway** (`OriginCheckMiddleware`), работает до
   reverse proxy — отклонённый запрос не доходит до монолита:
   - проверяются только `POST/PUT/PATCH/DELETE` на `/api/**`; `GET/HEAD/
     OPTIONS` и все не-`/api` пути (Razor-формы) не трогаются;
   - запрос должен нести заголовок `Origin` (fallback — `Referer`, из него
     берётся `scheme://authority`), совпадающий с публичным origin Gateway;
   - разрешённые origin — настройка `PublicOrigin` (env `PUBLIC_ORIGIN`,
     comma-separated); по умолчанию `http://localhost:{WEB_PORT}` (`WEB_PORT` —
     тот же host-порт, что в compose, дефолт `8080`);
   - нарушение → `403 Forbidden`, `Content-Type: application/problem+json`
     (RFC 7807), запрос не проксируется.
2. **Только JSON — в монолите.** Мутация `/api/**` с телом обязана приходить
   с `Content-Type: application/json`, иначе `415` (ProblemDetails).
   Браузер не умеет слать JSON из кросс-сайтового `<form>`, поэтому
   подделанный cross-site form-POST (`application/x-www-form-urlencoded`,
   `multipart/form-data`) умирает здесь. Исключение — эндпоинты с
   form-параметрами (загрузка `POST /api/files/upload`,
   `BindingSource.Form/FormFile`): требование к ним не применяется.
   Мутации без тела (logout, delete-by-id) не проверяются — против них
   работают Origin-проверка и `SameSite=Lax`.
3. **Почему не antiforgery.** Antiforgery требует выдать токен, заставить
   клиент хранить его и добавлять в каждый мутационный запрос. Для JSON API
   SPA это: дополнительный эндпоинт/поле ответа + ручная инъекция заголовка
   в сгенерированный клиент + дублирование защиты, которую уже дают
   Origin-проверка (заголовок `Origin` браузер ставит принудительно, подделать
   его с чужого сайта нельзя) и JSON-only-правило (кросс-сайтовый `<form>`
   JSON не слёт). Razor-UI остаётся без antiforgery, как и до шага 3b.0
   (его формы уходят same-origin, действует Lax). Финальная CSRF-модель
   (включая possible double-submit) решается при выносе аутентификации на
   Auth Service (Фаза 4).

Ограничение (принято сознательно): Origin-проверка опирается на заголовок,
который браузеры ставят принудительно; не-браузерные клиенты (curl-
интеграции, инструментинг) могут ходить без Origin — на read-only методы это
допускается, для мутаций `/api/**` такой клиент обязан передать один из
разрешённых Origin (документировано в отчёте шага 3b.0).

### 6.1 Усиление (шаг 3c.1, 2026-11-24)

1. **Сравнение origins — разбор URL, не строковый префикс.** И запросный,
   и разрешённые origins нормализуются в `scheme://host:порт` (порт —
   эффективный: `http→80`, `https→443`). Поэтому `Referer:
   http://localhost:8080.evil.com/...` **не** проходит как «начинается с
   `http://localhost:8080`» (`403`), а `https://example.com` и
   `https://example.com:443` — один и тот же origin. Литеральное значение
   `Origin: null` (opaque origins: sandbox-iframe, file://) не парсится как
   http(s) URL и отклоняется. Непарсируемые значения `Referer` middleware
   не падает — просто не находит совпадения.
2. **Баг: env `PUBLIC_ORIGIN` молча игнорировался.** Middleware читал
   `configuration["PublicOrigin"]`, а environment-provider регистрирует env
   под точным именем `PUBLIC_ORIGIN`; в .NET 10 lookup по ключу
   case-sensitive, поэтому env-переменная не находилась, и Gateway
   тихо работал с дефолтным origin. Найден живьём при вводе dev-origina
   (Vite): `Origin: http://localhost:5173` давал `403` при корректно
   выставленном env. Исправление — чтение обоих вариантов
   (`PublicOrigin` для appsettings, `PUBLIC_ORIGIN` для env) +
   регрессионный тест `PublicOrigin_env_var_spelling_is_read`.
3. **Dev-origin для Vite — только через `infra/compose.dev.yml`.**
   `make up-dev` (профиль `dev`) поднимает Vite-сервер на
   `127.0.0.1:5173`: браузер ходит в Vite напрямую, `/api` Vite проксирует
   в Gateway, и Origin остаётся `http://localhost:5173`. Dev-оверлей
   добавляет этот origin в `PUBLIC_ORIGIN` Gateway
   (`http://localhost:{WEB_PORT},http://localhost:5173`). Файл подключается
   **только** в `make up-dev`; в production-стек origin Vite не попадает
   (проверено: обычный `make up` + `Origin: http://localhost:5173` → `403`).
   В production `PUBLIC_ORIGIN` задаётся доменом приложения (README,
   «Деплой на сервер»). В dev-профиле Vite работает с `base: "/"`
   (`VITE_BASE` не задаётся — раньше `/spa/` ломал вход через Gateway, где
   префикс снимается): и прямой вход `http://localhost:5173/spa/**` (SPA
   fallback), и вход через Gateway (`/spa/**` с обрезкой префикса) работают.

### 6.2 Rate limiting на логин/регистрацию (шаг 3c.1a, 2026-11-24)

Угроза: у монолита нет троттлинга входа — brute-force по паролю не
ограничен. До Фазы 4 (Auth Service) первичная защита — на Gateway
(`AuthRateLimitMiddleware` + `SlidingWindowCounter`).

- **Область**: только `POST /api/auth/login` и `POST /api/auth/register`.
  Другие маршруты и `GET`/`HEAD` тех же путей не считаются. Лимит на
  клиента, не глобальный.
- **Механика**: in-memory скользящее окно по client-ключу; по умолчанию
  **10 запросов на 60 секунд**. Превышение → `429`,
  `Content-Type: application/problem+json` (RFC 7807), заголовок
  `Retry-After` (целое число секунд до освобождения места в окне, минимум 1).
- **Настройка** (env, compose пробрасывает; пусто = дефолт):
  - `RATE_LIMIT_REQUESTS_PER_MINUTE` (дефолт `10`) — максимум запросов на
    клиента за окно;
  - `RATE_LIMIT_WINDOW_SECONDS` (дефолт `60`) — длина окна в секундах;
  - `KNOWN_PROXIES` (список IP через запятую) и `KNOWN_NETWORKS`
    (список CIDR через запятую) — доверенные прокси; **по умолчанию
    пусты**. Непарсируемое значение — ошибка при старте Gateway
    (fail-fast), не тихий игнор.
- **Ключ клиента**: IP прямого соединения (socket peer). `X-Forwarded-For`
  учитывается **только** если socket peer сам входит в `KNOWN_PROXIES` /
    `KNOWN_NETWORKS`: список читается справа налево, доверенные записи
  пропускаются, ключ — первая недоверенная; если все записи доверенные —
  левая. Обоснование: при пустых `KNOWN_*` поддельный `XFF` не позволяет
  атакующему вращать ключи и обходить лимит; с непустыми — лимит честно
  переносится на реального клиента. Непарсируемые записи в `XFF`
  пропускаются (заголовок недоверенный).
- **Позиция в pipeline**: до `OriginCheckMiddleware` — запросы с чужим
  Origin тоже расходуют лимит (brute-force не делится на части сменой
  Origin). Отклонённый `429` не проксируется.
- **Ограничения (приняты сознательно)**: счётчик в памяти процесса
  Gateway — при перезапуске окно сбрасывается; при N репликах Gateway
  лимит становится N раз шире. Durable/общий счётчик (например, Redis) —
  вместе с инфраструктурой Фаз 4/5, если brute-force станет реальной
  угрозой; до этого уровня защита достаточна для dev и одиночного
  production-процесса.
- **Тестовый стенд**: для повторяющихся E2E-прогонов лимит поднимается
  env (`RATE_LIMIT_REQUESTS_PER_MINUTE=100 make up`) — production-дефолт
  не меняется. E2E из 3c.1 (≈6 мутаций auth за прогон, один клиент) проходит
  и с дефолтным лимитом, если между прогонов окно освобождается (проверено).

### 6.3 Переключение маршрутов на Auth Service и refresh-лимит (шаг 4.3b, 2026-12-01)

- **Маршруты**: Gateway получил третий кластер — `Upstream:Auth`
  (обязателен при старте, fail-fast). `/api/auth/**` и точный путь
  `/api/me` идут в Auth Service (order 0), **все остальные** `/api/**`
  — в монолит (order 1); `/api/me*`-lookalike (`/api/meetings/…`) остаётся
  на монолите. Старый `AuthApiController` монолита снаружи
  недостижим (не удалён — удаление, миграция БД и чистка — шаг 4.3c).
- **Refresh-лимит**: в область `AuthRateLimitMiddleware` вошёл
  `POST /api/auth/refresh` со **своим** лимитом —
  `RATE_LIMIT_REFRESH_REQUESTS_PER_MINUTE` (дефолт **30** на 60 с на
  клиента): SPA шлёт refresh автоматически при каждом истечении токена
  доступа, дефолтные 10/мин его бы задевали. Окно (`RATE_LIMIT_WINDOW_SECONDS`)
  и ключ клиента (IP + доверенный XFF) — общие с login/register: слайдинг-
  окно **одно на клиента**, путь только выбирает, какой лимит применить
  (не «10 на login и отдельно 30 на refresh»). Обоснование: refresh с
  украденным `rt` — тот же brute-force-канал; отдельный более высокий
  лимит — компромисс между UX (авто-refresh) и защитой.
- **Cookie**: Gateway не трогает `Set-Cookie` — флаги задаёт Auth Service
  (`jwt`: `Path=/`, `SameSite=Lax`; `rt`: `Path=/api/auth`,
  `SameSite=Strict`; оба `HttpOnly`/`Secure`). Проверено тестом
  pass-through и live-смоуком через Gateway.
- **Origin-проверка**: `/api/auth/**` под той же CSRF-защитой, что и
  любой `/api/**` (мутация с чужим Origin — `403` до upstream; валидный
  public origin проходит) — проверено отдельными тестами. Это
  «не блокируем нормальные запросы», а не «исключаем auth из проверки».
- **E2E-стенды**: `infra/compose.e2e.yml` больше не публикует порт Auth —
  стенд ходит в Auth только через Gateway (как и все остальные стеки).
  Новый `make e2e-short` — одноразовый стенд с 5-секундными токенами
  (`AUTH_JWT_EXPIRY_SECONDS=5`) и `JWT_CLOCK_SKEW_SECONDS=0`: там
  `short-token.spec.ts` проверяет прозрачный refresh через Gateway
  (один refresh на 401, без редиректа на логин).
- **Ограничение (принято сознательно)**: Gateway до Фазы 5 JWT **не
  валидирует** — только маршрутизирует и троттлит; валидацию делает
  монолит (HS256 + RS256/JWKS, 4.3a). Отслеживается как TD-31.

## Последствия

- Плюс: SPA и API без CORS; 401 JSON предсказуем для клиента; схема маршрутов
  расширяется одним PR на экран; стек зафиксирован; CSRF-защита мутаций
  `/api/**` — три уровня (Lax + Origin + JSON-only, раздел 6).
- Минус: dev-стэк теперь поднимает монолит в `Development` (OpenAPI нужен для
  `npm run gen:api`) — в dev доступны Swagger UI и детальные страницы
  исключений; dev-стэк на loopback работает без TLS только потому, что
  браузеры доверяют `localhost`/`127.0.0.1` (см. §1); CSRF-модель
  финализируется в Фазе 4.
- Если человек захочет другой UI-фреймворк — меняется только `frontend/`
  (каркас), Gateway-маршруты и ADR остаются.
