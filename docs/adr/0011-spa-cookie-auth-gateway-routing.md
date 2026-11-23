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
  `/api/**` защищаются только `SameSite=Lax` (кросс-сайтовый POST с другим
  сайтом cookie не приложит; кросс-сайтовый `fetch` с `mode: no-cors` не даст
  прочитать ответ). Полноценная защита (antiforgery-токен или
  double-submit) — при выносе аутентификации на Auth Service (Фаза 4).

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
  `/swagger/v1/swagger.json` (в dev-стэке `OpenApi:Enabled=true`), только
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

## Последствия

- Плюс: SPA и API без CORS; 401 JSON предсказуем для клиента; схема маршрутов
  расширяется одним PR на экран; стек зафиксирован.
- Минус: CSRF-защита пока только `SameSite=Lax` (antiforgery — Фаза 4);
  dev-стек на loopback работает без TLS только потому, что браузеры
  доверяют `localhost`/`127.0.0.1` (см. §1).
- Если человек захочет другой UI-фреймворк — меняется только `frontend/`
  (каркас), Gateway-маршруты и ADR остаются.
