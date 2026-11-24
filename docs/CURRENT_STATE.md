# Текущее состояние системы (Phase 0)

> Дата обследования: 2026-11-22. Ветка: `microservises` (коммит `0edd479`).
> Метод: чтение кода + реальная сборка/запуск в Docker + прогон сценариев через curl и headless-браузер (Playwright).
> Код при обследовании **не изменялся** (кроме локального, не трекимого `.env`).

## 0. Кратко

Монолит ASP.NET Core MVC (net8.0) + PostgreSQL + MinIO (S3 API) + Three.js.
Реализованы: регистрация, логин (JWT в httpOnly-cookie), «библиотека ассетов» (список своих файлов, загрузка 3D-файлов в S3, генерация presigned URL).
**Не реализованы** (хотя заявлены в README/ARCHITECTURE): «локальный ИИ-агент» (сегментация/раскраска по номерам) — кода нет вообще; logout; скачивание/удаление файлов; реальные превью (миниатюра — SVG-заглушка); health-эндпоинты; тесты; CI.

Уточнение по AGENTS.md («две БД»): в системе **одна** СУБД (PostgreSQL) + объектное хранилище MinIO (S3). Второй RDBMS нет.

## 1. Версия .NET и ключевые NuGet-пакеты

- **Таргет:** `net8.0` (проект `LoginAndReg.csproj`), **Dockerfile:** `mcr.microsoft.com/dotnet/sdk:8.0` (build) + `mcr.microsoft.com/dotnet/aspnet:8.0` (runtime).
- Nullable: `disable` (т.е. **выключен**), TreatWarningsAsErrors: выключен. Сборка даёт 7 предупреждений (CS8618, CS0169, CS0618) и 0 ошибок.
- **`global.json` нет** — версия SDK определяется из Dockerfile (8.0).

| Пакет | Версия | Зачем используется |
|---|---|---|
| Microsoft.EntityFrameworkCore | 8.0.4 | ORM |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.0.4 | провайдер PostgreSQL |
| MinIO (SDK) | 7.0.0 | объектное хранилище (S3 API) |
| System.IdentityModel.Tokens.Jwt | 8.15.0 | формирование/валидация JWT |
| Microsoft.AspNetCore.Authentication.JwtBearer | 8.0.0 | авторизация по JWT (cookie + Bearer) |
| Microsoft.AspNetCore.Mvc.NewtonsoftJson | 8.0.0 | JSON-сериализация (Newtonsoft) |
| Microsoft.EntityFrameworkCore.Tools | 8.0.4 | CLI-инструменты EF (миграции) |
| System.Reactive | 6.0.1 | **не используется** (мёртвая зависимость) |

Сборка: `dotnet build` (в Docker) — **успешно**, 7 предупреждений:
- CS8618: `AccountService._fileStorageService` (readonly, не инициализирован) и `FileUploadViewModel` (все свойства) — nullable не включён, предупреждения «на пустом месте».
- CS0169: `AccountService._fileStorageService` — поле не используется.
- CS0618 (×5): устаревшие конструкторы `JsonWebTokenHandler`/`SecurityTokenDescriptor` в `AccountService` (HMACSecurityKey).

## 2. Базы данных / хранилища

| Хранилище | Что | Подключение (без паролей) |
|---|---|---|
| PostgreSQL 15 (`postgres:15-alpine`) | одна БД с 3 бизнес-таблицами + `__EFMigrationsHistory` | `Host=login_reg_db;Port=5432;Database=${DB_NAME};Username=${DB_USER}` — в `.env.example`: `DB_NAME=DB`, `DB_USER=USER`, пароль из `DB_PASSWORD` (в `.env`). Локально (appsettings.Development.json): `Host=127.0.0.1;Port=5432;Database=mydb;Username=myuser` |
| MinIO (S3 API) | пользовательские 3D-файлы + SVG-«миниатюры» | `http://storage:9000`, AccessKey `${MINIO_ROOT_USER}` (в `.env.example`: `MINIOADMIN`), бакет `${MINIO_BUCKET}` (в `.env.example`: `USERDATA` — **невалидное имя бакета**, см. TD-2) |

- **Нарушение «свои миграции у каждого сервиса» сейчас не применимо** — сервисов нет, один `DbContext`: `AppDbContext` (`data/AppDbContext.cs` — странное расположение, см. TD-9).
- **Миграции применяются автоматически при старте** приложения (`app.Database.Migrate()` в `Program.cs`), а не отдельной командой. Единственная миграция: `20260421091456_InitialCreate`.
- Имена таблиц сохраняют регистр (EF цитирует: `"Users"`, `"Persons"`, `"UserFiles"`).

### Сущности и связи

| Сущность | Поля | Связи |
|---|---|---|
| `User` | `id` (int, PK, identity), `login` (unique, required), `password_hash` (required), `salt` (required) | 1—N → `UserFile` (FK `UserFiles.user_id`, **OnDelete.Cascade**) |
| `Person` | `id` (int, PK, identity), `user_id` (int), `full_name` (required), `birth_date` (Date) | 1—1 с `User` **только на уровне приложения** — в БД **нет FK-ограничения** на `user_id` (TD-8) |
| `UserFile` | `id` (Guid, PK, `Guid.NewGuid()`), `user_id` (int, FK→Users, cascade), `file_name` (unique в пределах user), `storage_path` (required), `thumbnail_path`, `file_size` (long), `created_at` (DateTimeOffset) | N—1 → `User` |

Создание `Person` выполняется в той же транзакции, что и `User` (регистрация).

## 3. Контроллеры и маршруты

| Маршрут | Метод | Что делает | Auth |
|---|---|---|---|
| `/` | GET | редирект на `/Account/Auth` | публичный |
| `/Account/Auth` | GET | страница логина/регистрации (Razor) | публичный |
| `/Account/Login` | POST | проверка пароля (SHA256(salt+password)), выдаёт JWT (60 мин) в httpOnly-cookie `jwt`; JSON `{success, message, redirectUrl:"/Home/Index"}` | публичный |
| `/Account/Register` | POST | валидация (FluentValidation: FullName/BirthDate/Login/Password, Password≥6), создаёт `User`+`Person`; JSON `{success, message}` | публичный |
| `/Home/Index` | GET | главная страница — «библиотека ассетов» (Razor) | **JWT** |
| `/api/files` | GET | JSON-список файлов текущего пользователя: `id, fileName, fileSize, createdAt, previewUrl` (presigned 30 мин), `thumbnailUrl` (presigned 30 мин) | **JWT** |
| `/api/files/upload` | POST | multipart `file`: whitelist расширений (STL/OBJ/STEP/STP/GLB) + размер ≤ `MAX_UPLOAD_BYTES` (дефолт 100 MiB, ADR 0015; body-уровень = лимит + 1 MiB envelope, превышение envelope — «голый» 413, превышение лимита — `413 ProblemDetails` `File too large`); (с 3c.4) для `.glb` — проверка заголовка GLB (magic `glTF`, версия 2, длина ≤ размеру файла и ≤ 64 MiB), иначе `400 ProblemDetails` `Invalid GLB file`; пишет файл в S3 (`{userId}/{fileId}{ext}`) и SVG-заглушку (`{userId}/thumbnails/{fileId}.svg`); JSON-ответ как в `/api/files` | **JWT** |
| `/api/config` | GET | (с 3c.4, ADR 0015) публичная конфигурация SPA: `{maxUploadBytes, allowedUploadExtensions: [".glb"]}` — лимит тот же `MAX_UPLOAD_BYTES`, список расширений принимает **SPA** (Razor UI сохраняет полный whitelist) | публичный |
| `/api/models` | GET | (с шага 3c.2, ADR 0013) страница моделей текущего пользователя: `items` (`id, name, sizeBytes, createdAt, thumbnailUrl` — presigned по публичному эндпоинту), `page, pageSize, totalCount, totalPages`; `pageSize` 1..100 (дефолт 20), иначе `400` ProblemDetails; сортировка — новые сначала | **JWT** |
| `/api/models/{id}` | GET | (с шага 3c.2) деталь модели: как карточка + `downloadUrl` (presigned); чужой/несуществующий `id` — `404` (owner-check в SQL) | **JWT** |
| `/api/models/{id}` | DELETE | (с шага 3c.2) удаление записи + S3-объектов (оригинал + thumbnail; сначала S3, потом БД, ошибка S3 — `500`); повторный `DELETE` — `404` (идемпотентность); чужой `id` — `404` | **JWT** |
| `wwwroot/*` | GET | статика (см. §6) | публичный |

Прочее:
- (до 3c.2) не было logout, refresh, `/api/auth/me`, удаления файлов; теперь: `POST /api/auth/logout`, `GET /api/me` (с 3c.1), удаление — `DELETE /api/models/{id}` (с 3c.2). Скачать отдельным эндпоинтом нельзя — только presigned URL.
- **Нет** health-эндпоинтов (`/health/live`, `/health/ready` отсутствуют).
- `UseHttpsRedirection()` подключён, но HTTPS-эндпоинт не опубликован — фактически ничего не делает (в проде TLS режет внешний nginx, конфига которого **нет в репозитории**, см. README).
- В режиме `Production` обработчик исключений указывает на `/Home/Error`, **которого не существует** (нет такого action в `HomeController`) → необработанное исключение = 500 с пустым телом (TD-5).
- CORS в коде не настроен; rate-limiting отсутствует (логин/регистрация ограничены на Gateway с шага 3c.1a, ADR 0011 §6.2).
- (с шага 3b.0) Middleware монолита: state-changing запрос (`POST/PUT/PATCH/DELETE`) на `/api/**` **с телом** обязан приходить с `Content-Type: application/json`, иначе `415` (ProblemDetails). Исключение — эндпоинты с form-параметрами (`POST /api/files/upload`); мутации без тела не проверяются (см. ADR 0011 §6).
- (с шага 3b.0) OpenAPI (`/swagger/**`) отдаётся **только при `ASPNETCORE_ENVIRONMENT=Development`**; в Production-подобных стэках — `404`. Dev-стэк задаёт окружение через `infra/.env` (`ASPNETCORE_ENVIRONMENT=Development`); настройка `OpenApi:Enabled` удалена.
- (с шага 3c.1) JSON auth API для SPA (`Controllers/AuthApiController.cs`, `[AllowAnonymous]`): `POST /api/auth/login` (`{login, password}` → `AuthUserDto`, неудача — `401` ProblemDetails), `POST /api/auth/register` (`{fullName, birthDate, login, password}` → `201` + `AuthUserDto`; занятый login — `409`; валидация — `400` с `errors`), `POST /api/auth/logout` (`204`, удаляет cookie). `GET /api/me` теперь типизирован (`AuthUserDto`, `401` — ProblemDetails). Парольные проверки идут через тот же `IAccountService`, что и Razor-экраны (хранирование паролей не менялось).

## 4. Механизм аутентификации

- **Не ASP.NET Identity.** Собственная схема в `Services/AccountService.cs`:
  - пароль: `SHA-256(salt + password)`, соль — 16 байт (Base64). **Быстрое хеширование без растяжки** (нет PBKDF2/bcrypt/argon2) — см. TD-12.
  - JWT HS256 (HMAC), секрет `JWT_Secret` (env; в `.env.example` — дефолтный dev-секрет), срок **60 минут**, единственная полезная claim — `ClaimTypes.Name` (= логин, `unique_name`). **Нет** roles, refresh-токенов, issuer/audience не валидируются (`ValidateIssuer=false`, `ValidateAudience=false`), проверяются только срок и подпись.
- Токен отдаётся **в httpOnly-cookie `jwt`** (`Secure`, path `/`). Браузерный клиент (`wwwroot/js/auth-logic.js`) использует `fetch` + `credentials: 'same-origin'`, localStorage не использует.
- Сервер также принимает стандартный заголовок `Authorization: Bearer` (дефолт JwtBearer).
- `[Authorize]` — на `HomeController` и `UserFilesController`; cookie подставляется в заголовок через `JwtBearerOptions.OnMessageReceived`.
- **Баг UI (TD-4):** поле «Email» формы логина имеет `type="email"`, а при регистрации логин — произвольная строка (`type="text"`). Пользователь с не-email логином **не может войти через UI** (клиентская валидация email блокирует отправку).
- (с шага 3c.1) Опции cookie собраны в одном месте — `Services/AuthCookie.cs` (`AuthCookie.CreateOptions()`: имя `jwt`, `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`); Razor-контроллер и JSON `AuthApiController` используют их оба, флаги совпадают.
- (с шага 3c.1) Logout реализован как JSON-эндпоинт `POST /api/auth/logout` (удаляет cookie) — для SPA. Razor-кнопки «Выйти» по-прежнему нет (TD-6).

## 5. Где и как хранятся загруженные 3D-файлы

- **Объектное хранилище** (MinIO, S3 API), бакет `userdata` (по appsettings-дефолту; в `.env.example` — `USERDATA`, что ломает загрузку, TD-2).
- Ключи: `{userId}/{fileId}{ext}` (файл) и `{userId}/thumbnails/{fileId}.svg` (миниатюра).
- Доступ из браузера — только **presigned URL** (GET, срок 30 минут).
- **Загрузка:** браузер → web-сервис (multipart; с 3c.4 максимум = `MAX_UPLOAD_BYTES`, та же переменная, что и у Gateway, дефолт 100 MiB, ADR 0015; body-лимиты Kestrel `MaxRequestBodySize`/`MultipartBodyLengthLimit` = лимит + 1 MiB envelope; **весь multipart буферизуется в памяти** — `MemoryBufferThreshold = int.MaxValue`, см. TD-13) → S3.
- **Валидация:** расширение, размер и (с 3c.4, только для `.glb`) заголовок GLB (magic/версия/длина — до записи в S3 и БД). **Нет** проверки содержимого дальше заголовка, лимитов полигонов, таймаутов (ADR 0015).
- **Миниатюра — не превью геометрии:** генерируется статичный SVG-заглушка (`ThumbnailService`), в UI поверх неё идёт декоративная JS-анимация «генерации превью» (TD-18).
- **Критический баг (TD-3):** presigned URL содержит **внутренний hostname** `http://storage:9000/...` — из браузера (внешняя сеть) хост не разрешается: `net::ERR_NAME_NOT_RESOLVED`. Предпросмотр 3D-модели у пользователя **не работает** (проверено headless-браузером, см. скриншот `docs/screenshots/home-model-preview-broken.png`). Изнутри docker-сети URL валиден.
- Загруженные файлы **не удаляются** ни приложением, ни ручным API (нет endpoint'а удаления).
- Объём данных в проде: **не определён из репозитория** — вопрос для человека (см. §9).

## 6. Содержимое `data/` и `wwwroot/`

### `data/`
- `AppDbContext.cs` — единственный файл; фактический `DbContext` (смешение кода и «данных», TD-9).

### `wwwroot/` (всё закоммичено в git)

| Контент | Путь | Размер (git) |
|---|---|---|
| **Своя** CSS/JS | `css/site.css`, `css/home-dashboard.css`, `js/site.js`, `js/auth-logic.js`, `js/auth-visuals.js`, `js/home-dashboard.js`, `favicon.ico` | ~50 КБ |
| Демо-модель (декор экрана логина) | `models/auth-object.glb` | **3.7 МБ** (TD-15) |
| **Библиотеки** (закоммичены, TD-1) | `lib/bootstrap/` (css+js, 5.3.3) | **8.4 МБ** |
| | `lib/jquery/` (3.7.1) | 920 КБ |
| | `lib/jquery-validation/` (1.19.5) | 168 КБ |
| | `lib/jquery-validation-unobtrusive/` (3.3.11) | 40 КБ |
| | `lib/three/` (`three.module.js` 0.161.0, `GLTFLoader.js`, `BufferGeometryUtils.js` — ESM, без бандлера) | 1.3 МБ |

- Стек фронтенда: Razor-вью + ванильный JS (модули), jQuery + jquery-validation (unobtrusive), Bootstrap 5, Three.js (WebGL, GLTFLoader) — **без Vite/TS/сборщика**.
- В `LoginAndReg.csproj` есть ссылка на несуществующую папку `wwwroot\Новая папка` (TD-10).

## 7. «Локальный ИИ-агент»

**В репозитории ИИ-агента и какого-либо кода сегментации/раскраски нет.**
- README: «Система сегментации для раскраски по номерам: **В разработке**».
- Кнопки «Перейти в модуль покраски» / «Перейти в модуль заказа» в `home-dashboard.js` — заглушки (`alert('Модуль находится в разработке...')`).
- Служебные кнопки Google/GitHub/Discord на экране авторизации — декоративные, backend отсутствует.
- Никаких Python-сервисов, очередей, моделей в репозитории нет.

## 8. Итоги baseline-проверки (2026-11-22)

### Как запускалось
1. `docker compose build` — **успех** (7 предупреждений, 0 ошибок).
2. `docker compose up` **в чистом виде падает**: образ `minio/minio` **больше не публикуется в Docker Hub** (pull 404; `quay.io/minio/minio` требует авторизацию, bitnami/minio — репозиторий пуст). В baseline-запуске MinIO заменён временным S3-совместимым хранилищем **SeaweedFS** (`chrislusf/seaweedfs`, порт 9000) через compose-override **вне репозитория** (`/tmp/baseline-override.yml`); порт веб-приложения временно `127.0.0.1:18080` (порт 8080 хоста занят другим процессом).
3. После `up`: миграция PostgreSQL применилась автоматически; все 3 контейнера healthy/running; логи web без ошибок.

### Прогон сценариев (curl + headless Chromium)

| # | Сценарий | Результат на текущем коде |
|---|---|---|
| 1 | `GET /` → редирект на `/Account/Auth`, страница 200, WebGL-canvas с демо-моделью отрендерился | ✅ |
| 2 | Регистрация нового пользователя (уникальный логин) | ✅ 200; в БД `Users`+`Persons` |
| 3 | Регистрация с дублирующимся логином | ✅ 409 + сообщение |
| 4 | Логин с неверным паролем | ✅ 401 |
| 5 | Логин с верными данными | ✅ 200 + httpOnly-cookie `jwt` (Secure), payload: `unique_name, nbf, exp, iat` |
| 6 | `GET /Home/Index` с JWT | ✅ 200 (dashboard) |
| 7 | `GET /Home/Index`, `GET /api/files` без JWT / с невалидным JWT | ✅ 401 |
| 8 | Загрузка валидного `.glb` (3.7 МБ) | ✅ 200; объект в S3, строка в `UserFiles`, presigned URL |
| 9 | Загрузка `.exe` (расширение вне whitelist) | ✅ 400 |
| 10 | `GET /api/files` — список только своих файлов | ✅ 200, JSON |
| 11 | Presigned URL: доступ изнутри docker-сети | ✅ 200 |
| 12 | **Presigned URL: доступ из браузера (внешняя сеть)** | ✅ **решено в шаге 3.0 (2026-11-22):** presigned URL подписываются с публичного S3-эндпоинта (`PUBLIC_S3_ENDPOINT`), из хоста и браузера → 200. В baseline было ❌ (TD-3: `storage:9000` не резолвится) |
| 13 | Статика: js/css/lib, `.glb` с content-type `model/gltf-binary` | ✅ 200 |
| 14 | UI: логин с не-email логином | ✅ **решено в шаге 1.0 (2026-11-22):** `type="email"` → `type="text"` (TD-4). Полный UI-сценарий (логин → dashboard → загрузка → карточка файла) работает |
| 15 | UI: превью 3D-модели в карточке | ✅ **работает после шага 3.0 (2026-11-22):** превью-карточки загружаются из публичного S3 в браузере (скриншот `step3-dashboard-preview.png`). В baseline было ❌ из-за #12 (пустая область). Примечание: в текущем Razor-UI превью — это SVG-заглушки (`<img>`), ветка Three.js/canvas для загруженных файлов не выполняется (карточка всегда имеет thumbnail) — для нового фронтенда 3D-вьюер будет новой функцией (см. инвентарь, раздел 11) |
| 16 | **Прямой upload в S3 из браузера (presigned PUT)** — сценарий будущего SPA-флоу (Фаза 3/5): страница `http://127.0.0.1:18080` выполняет `fetch(presignedUrl, {method: PUT})` прямо в S3 (CORS разрешён) | ✅ **добавлен и пройден в шаге 3.0 (2026-11-22):** PUT из headless Chromium → 200, объект в S3 цел (сверено содержимое); preflight с разрешённым origin → 200, с чужим → 403 |

**Вывод (актуальный на 2026-11-22, после шага 3.0):** регистрация/логин/библиотека файлов/превью/прямые S3-операции из браузера работают. В baseline было три бага: образ MinIO недоступен (решено TD-14 → SeaweedFS, шаг 1.0), 500 из-за имени бакета (решено TD-2, шаг 1.0), `type="email"` (решено TD-4, шаг 1.0) и presigned из браузера (решено TD-3, шаг 3.0).

Скриншоты: `docs/screenshots/auth-page.png`, `docs/screenshots/home-model-preview-broken.png` (baseline), `docs/screenshots/step3-dashboard-preview.png` (после шага 3.0).

## 9. Ответы на открытые вопросы ARCHITECTURE.md §12 (столько, сколько известно из кода)

1. **Какие СУБД и две БД?** Одна СУБД — PostgreSQL 15 (БД из `DB_NAME`, в example `DB`), в ней: `Users`, `Persons`, `UserFiles`, `__EFMigrationsHistory`. «Вторая БД» — это MinIO (объектное хранилище, бакет `userdata`), не RDBMS.
2. **Где лежат файлы?** MinIO, ключи `{userId}/{fileId}{ext}` и `{userId}/thumbnails/{fileId}.svg`; метаданные в `UserFiles`. **Количество/размер файлов в проде — неизвестно из репозитория, вопрос для человека.**
3. **Форматы:** STL, OBJ, STEP/STP, GLB (whitelist в `FileStorageService.AllowedExtensions` + клиентский whitelist).
4. **Версия .NET:** 8.0.
5. **Есть ли пользователи/данные, которые нельзя потерять?** **Неизвестно из репозитория** — вопрос для человека (продакшен: 3dpaint.tw1.su).
6. **Google/GitHub-провайдеры?** В коде только декоративные кнопки. **Нужны ли они — вопрос для человека.**
7. **Максимальный размер файла:** (с 3c.4) `MAX_UPLOAD_BYTES`, дефолт 100 MiB — одна переменная для Gateway и монолита (ADR 0015); до 3c.4 монолит хардкодил 500 MiB, Gateway — 100 MiB. Лимит не секрет (отдаётся в `GET /api/config`).
8. **Модуль покраски?** Не реализован (кнопка-заглушка). **Что именно должно делать — вопрос для человека.**
9. **Первая версия AI-сервиса?** Кода нет вообще. **Скоуп — вопрос для человека.**

## 10. Чек-лист ручной проверки (использовать после КАЖДОГО шага миграции)

Сценарии 1–15 из §8 — обязательный чек-лист. Минимальный набор (быстрый прогон):

- [ ] `docker compose ... up -d --build` — все контейнеры поднялись, web отвечает.
- [ ] `GET /Account/Auth` → 200.
- [ ] Регистрация нового логина → 200; дубликат → 409.
- [ ] Логин с верным паролем → 200 + cookie `jwt`; неверный → 401.
- [ ] `GET /Home/Index` с cookie → 200; без → 401.
- [ ] `POST /api/files/upload` (GLB) → 200; строка в `UserFiles`; объект в объектном хранилище.
- [ ] `POST /api/files/upload` (плохое расширение) → 400.
- [ ] `GET /api/files` → только свои файлы, валидные ссылки на файлы.
- [ ] Presigned URL открывается **из браузера** (проверять с хоста, не из docker-сети!).
- [ ] Статика (js/css) отдаётся, `.glb` — с content-type `model/gltf-binary`.
- [ ] (по мере фаз) health-эндпоинты сервисов → 200.
- [ ] (с шага 3a) `GET /spa/` → SPA (title `3DPAINT`); deep-link `GET /spa/любой/путь` → 200 (SPA-fallback).
- [ ] (с шага 3a) `GET /api/health` → 200 JSON; `GET /api/me` без cookie → 401 **JSON**; с cookie → 200.
- [ ] (с шага 3b.0) `POST /api/**` с `Origin` чужого сайта / без `Origin` → **403** `application/problem+json` (проверка на Gateway, до монолита).
- [ ] (с шага 3b.0) `POST /api/**` с телом и `Content-Type: text/plain` (при правильном `Origin`) → **415**; `POST /api/files/upload` multipart — не 415.
- [ ] (с шага 3b.0) `GET /swagger/v1/swagger.json` в dev-стэке → 200; в Production-подобном → 404.
- [ ] (с шага 3b.0) Razor-формы работают как раньше (логин/регистрация через `/Account/Auth`), т.к. Origin-проверка не трогает не-`/api` пути.
- [ ] (с шага 3c.4) `GET /api/config` без cookie → 200 `{maxUploadBytes, allowedUploadExtensions}`.
- [ ] (с шага 3c.4) `POST /api/files/upload` с битым `.glb` (не magic `glTF`) → `400 ProblemDetails`, записи в БД и объекта в S3 нет.
- [ ] (с шага 3c.4) файл > `MAX_UPLOAD_BYTES` → `413` как через Gateway, так и в монолит напрямую (E2E: `e2e/library.spec.ts`, `E2E_MONOLITH_URL`).
- [ ] (с шага 3c.4) SPA: `/spa/library` — панель загрузки (кнопка + drag&drop), очередь, прогресс, отмена/повтор; `.stl`/пустой/больший лимит файл отклоняется **до** отправки; отмена загрузки не оставляет модели в библиотеке (`e2e/upload.spec.ts`).
- [ ] (с шага 3c.1a) `POST /api/auth/login` 11 раз подряд от одного клиента → первые 10 доходят до монолита, 11-й → **429** `application/problem+json` + заголовок `Retry-After` (rate limit Gateway, ADR 0011 §6.2).
- [ ] (с шага 3c.2) `GET /api/models` (JWT) → только свои модели, `pageSize=1000` → **400**; без cookie → **401**.
- [ ] (с шага 3c.2) `GET`/`DELETE /api/models/{чужой-id}` от другого пользователя → **404** (не 403), модель владельца на месте.
- [ ] (с шага 3c.2) `DELETE /api/models/{id}` → **204**; повторный `DELETE` → **404**; старый presigned URL после удаления не открывается.
- [ ] (с шага 3c.2) `/spa/library`: список с превью и пагинацией, удаление с подтверждением; DTO без `storage:`, S3-ключей-полей и данных владельца (E2E: `make e2e`).
- [ ] (с шага 3c.3) `/spa/models/{id}`: GLB-модель рендерится (canvas, камера по bounding box, OrbitControls), инфо-панель (вершины/треугольники/меши/материалы/текстуры/размеры/UV); чужая модель → «Модель не найдена»; битый GLB → «Файл повреждён»; не-GLB (.stl/.obj/.step/.stp) → уведомление + «Скачать» (E2E: `make e2e`).

## 11. Инвентарь экранов (составлен 2026-11-23, шаг 3a; субагент + сверка главным агентом)

Маршрутизация монолита — conventional (`{controller=Account}/{action=Auth}/{id?}`), `ViewBag` не используется. Всего `.cshtml`: 5 (2 экрана + 2 layout + `_ViewImports`).

### 11.1 Экраны

| # | Экран (View) | Маршрут | JS (свой) | JS (чужой) | Вызовы в бэкенд | Данные | Примечания |
|---|---|---|---|---|---|---|---|
| E1 | `Views/Account/Auth.cshtml` (layout `_AuthLayout`) | `GET /` и `GET /Account/Auth` (публично) | `js/auth-logic.js` (формы, fetch, табы), `js/auth-visuals.js` (модуль: Three.js-фон) | Three.js r157 + GLTFLoader (`lib/three/`), шрифт Inter (Google Fonts) | `POST /Account/Login`, `POST /Account/Register` (FormData) | ViewModel'ов на GET нет; на POST: `LoginViewModel{Login,Password}`, `RegistrationViewModel{FullName,BirthDate,Login,Password}` → сущности `User`, `Person` | Декоративные: кнопки Google/GitHub/Discord, «Забыли пароль?». Three.js-фон: модель `wwwroot/models/auth-object.glb` (fallback — wireframe-икосаэдр), parallax, материалы реагируют на заполнение форм. Весь CSS экрана — инлайн в `_AuthLayout` (~590 строк) |
| E2 | `Views/Home/Index.cshtml` (layout `_HomeLayout`) | `GET /Home/Index` (**JWT**) | `js/home-dashboard.js` (модуль: сайдбар, upload-drawer, drag&drop, валидация, карточки, per-card Three.js-сцены) | Three.js r157 + GLTFLoader (`lib/three/`), шрифт Inter | `GET /api/files`, `POST /api/files/upload` | ViewModel'а нет; данные из `UserFiles` (`id, fileName, fileSize, createdAt, previewUrl, thumbnailUrl` — presigned 30 мин) | Профиль в сайдбаре — хардкод («Алексей С.»); кнопки «покраска»/«заказ» — заглушки без обработчиков; «pipeline» загрузки — визуальный (`await wait(450)`); превью карточек — **SVG-заглушки** (`<img>`), ветка Three.js/canvas для загруженных файлов не выполняется |

Layout'ы: `_HomeLayout` (только Google Fonts + `css/home-dashboard.css`), `_AuthLayout` (Google Fonts + инлайн-<style> + `#bgCanvas`).

### 11.2 Мёртвый/мусорный код в `wwwroot` (для 3d)

| Файл | Статус |
|---|---|
| `js/site.js` | Пустой placeholder, нигде не подключается |
| `lib/three/BufferGeometryUtils.js` | Нигде не импортируется |
| `lib/bootstrap/` (v5.3.3) | Нигде не подключается |
| `lib/jquery/` (v3.7.1), `lib/jquery-validation/`, `lib/jquery-validation-unobtrusive/` | Нигде не подключаются |
| `css/site.css` | Нигде не подключается |
| `models/README.md` | Пустой файл |

### 11.3 Экран → JSON-эндпоинты, которые понадобятся SPA (без реализации, кроме отмеченных)

| Экран SPA | Нужные JSON-эндпоинты монолита | Статус (шаг 3a) |
|---|---|---|
| `/spa/` (каркас, connection-check) | `GET /api/health` (публичный), `GET /api/me` (JWT) | ✅ добавлены (`ApiStatusController`) |
| Вход/регистрация (E1) | `POST /api/auth/login`, `POST /api/auth/register`, `POST /api/auth/logout`, `GET /api/me` | ✅ сделано в 3c.1 (`AuthApiController`, типизированные OpenAPI-ответы; SPA-экраны `/spa/login`, `/spa/register`) |
| Библиотека моделей (E2) | `GET /api/models` (страница), `GET /api/models/{id}`, `DELETE /api/models/{id}`; upload — `POST /api/files/upload` (уже есть, но **исключён из OpenAPI** — Swashbuckle не моделирует `[FromForm] IFormFile`) | ✅ сделано в 3c.2 (`ModelsApiController`, ADR 0013: owner-check в SQL, чистые DTO, idempotent delete); старые `/api/files/**` не тронуты |
| 3D-вьюер модели (**новая функция**, не перенос) | `GET /api/files/{id}/preview` (свежий presigned GET) | ⏳ сделать вместе со вьюером (3c) |
| Paint (ADR 0010, Фаза 6.5) | `POST /api/files/{id}/textures` (версии текстур), `GET /api/files/{id}/textures` | ⏳ не в Фазе 3 |

### 11.4 3D-вьюер — новая функция

В текущем Razor-UI «превью» карточек — это SVG-заглушки (`<img class=asset-thumb>`), т.к. монолит всегда создаёт thumbnail; Three.js-ветка в `home-dashboard.js` для загруженных файлов **не выполняется**. Проверено (шаг 3a): `GLTFLoader` грузит пользовательский GLB напрямую из S3 через presigned GET из оригина Gateway — 2 меша, UV на месте, рендерится. Реальный 3D-вьюер (Three.js / `@react-three/fiber`) добавляется в `frontend/` как новая функция при переносе (3c), оценка отдельно.

### 11.5 Состояние SPA (после шага 3c.1)

`frontend/`: React 19 + Vite 7 + TS strict, ESLint 9 + Prettier, Vitest (jsdom) + Playwright (E2E), `openapi-fetch` + сгенерированные типы (`npm run gen:api` из OpenAPI монолита, `Swashbuckle 7.2.0`, только `/api/**`). Dockerfile multi-stage → nginx (SPA-fallback, immutable-кэш `/assets/`). Сервисы compose: `frontend` (nginx) и `frontend-dev` (Vite HMR, профиль `dev`, `make up-dev`). Маршруты Gateway: `/api/**` → монолит, `/assets/**`, `/spa/**` → frontend, остальное → монолит (ADR 0011). (с шага 3b.0) До reverse proxy в Gateway работает `OriginCheckMiddleware`: мутации `/api/**` без `Origin`/`Referer` совпадающего с публичным origin Gateway отклоняются `403` ProblemDetails (ADR 0011 §6).

(с шага 3c.1) Экраны: `/spa/login` и `/spa/register` (JSON-эндпоинты `AuthApiController`), защищённый `/spa/library`, `/spa/` — редирект по результату `GET /api/me`. Состояние авторизации — TanStack Query (`useMe()`, `queryKey ['me']`), маршруты — React Router (`basename="/spa"`), ошибки — из ProblemDetails (`detail` + полевые `errors`). Тесты: Vitest (unit, `MemoryRouter`, моки `openapi-fetch`) и Playwright (E2E `e2e/auth.spec.ts` + `e2e/library.spec.ts` + общие хелперы `e2e/helpers.ts`, базовый URL — `http://localhost:{WEB_PORT}`, т.е. через Gateway; `npm run test:e2e`, `WEB_PORT=<порт> npx playwright test`, изолированный стенд — `make e2e`).

(с шага 3c.2) `/spa/library` — библиотека моделей: список от `GET /api/models` (TanStack Query, типизированный клиент `src/api/models.ts` из OpenAPI), presigned-превью (при истёкшем URL — один повтор запроса списка на страницу, дальше фолбэк), пагинация (стрелки + «Стр. X из Y»), удаление с inline-подтверждением (ошибка — `role="alert"` из ProblemDetails). Бэкенд: `ModelsApiController` (`/api/models`, ADR 0013) — owner-check в SQL (`id = @id AND user_id = @userId`, чужой `id` — `404`), DTO без внутренних хостов/S3-ключей/данных владельца, `DELETE` — сначала S3-объекты (оригинал + thumbnail), потом запись в БД, повторный `DELETE` — `404`. Тесты: 7 unit-тестов библиотеки (Vitest), E2E `e2e/library.spec.ts` (список, whitelist DTO + отрицательные проверки, IDOR 404, удаление с проверкой исчезновения S3-объектов и идемпотентности, пагинация на 21 модели, `pageSize=1000` → 400) — `make e2e` 10/10 дважды подряд.

(с шага 3c.3) `/spa/models/{id}` — 3D-вьюер (Three.js 0.187 + @react-three/fiber + drei, ленивый чанк ~1 МБ, three.js не грузится на login/library). Данные — `GET /api/models/{id}` (чужая/несуществующая → «Модель не найдена»). GLB: браузер `fetch`-ит presigned URL (progress), `GLTFLoader.parse` с доверенным `LoadingManager` (`setURLModifier` пропускать только `blob:`/`data:` — внешние URI внутри недоверенного GLB блокируются, ADR 0014); http/сеть-ошибка → один повтор со свежим presigned URL (новый `GET /api/models/{id}`), abort при уходе со страницы не считается ошибкой. Сцена: hemisphere+directional свет, drei `Grid` (без HDR/CDN), OrbitControls + «Сбросить камеру», камера по bounding box. Панель: вершины/треугольники/меши/материалы/текстуры/размеры/UV (чистый модуль `model-info.ts`). Потеря WebGL-контекста → оверлей с «Перезагрузить сцену»; нет WebGL → уведомление. При размонтировании сцены GPU-ресурсы освобождаются (`disposeSceneObject`, E2E проверяет счётчик geometries). Не-GLB (.stl/.obj/.step/.stp) — уведомление «Просмотр 3D недоступен» + ссылка «Скачать» (canvas не создаётся). Отдельного лимита вьюера нет (максимум — upload монолита, 500 МБ, TD-13; крупный файл ~51 МБ проверен тестом). Draco/Meshopt не декодируются (локальной WASM нет) → явное сообщение (TD-26). S3-CORS расширен на все origins SPA (8080/18080/5173/18090 × localhost/127.0.0.1) — `S3_CORS_ORIGINS`. Тесты: 21 unit-тест вьюера (Vitest: model-info, glb-loader (включая ~50 МБ GLB и блокировку внешних URI), scene-dispose, ViewerPage) + E2E `e2e/viewer.spec.ts` (рендер + инфо-панель, отсутствие утечки GPU-ресурсов при перемонтировании, битый GLB, чужая модель → 404, .stl → уведомление) — `make e2e` 15/15. Тестовые GLB генерируются в памяти (`src/test/glbfactory.ts`), бинарных фикстур в репозитории нет. Headless Chromium: WebGL2 из коробки (SwiftShader), дополнительные флаги не понадобились.

(с шага 3c.4) `/spa/library` — временный экран загрузки (ADR 0015, заменится presigned-флоу в Фазе 5): панель `UploadPanel` (кнопка «Выбрать файлы (GLB)» + drag&drop; `input[type=file]` скрыт визуально, но фокусируемый с клавиатуры), очередь до 2 одновременных загрузок (`UploadQueue`), прогресс — XHR `upload.onprogress` (`upload-client.ts`, `fetch` не умеет progress), у каждого файла — строка со статусом/прогрессом/«Отменить»/«Повторить». Клиентская валидация **до сети** (`validate-upload.ts`): расширение по `allowedUploadExtensions` из `GET /api/config` (публичный эндпоинт, `ConfigApiController`; список SPA — `[".glb"]`), размер ≤ `maxUploadBytes`, не пустой файл — нарушитель не отправляется (E2E проверяет отсутствие запроса). Обработка ответов: 200/201 → «Готово» + invalidation списка моделей; 400 → `detail` сервера (без «Повторить»); 401 → редирект `/spa/login`; 413 (JSON монолита или «голый» Gateway) → «превышен лимит»; 429/5xx/сеть → «Повторить». Сервер: GLB magic/версия/длина — `400 ProblemDetails` до S3/БД; лимит — единая `MAX_UPLOAD_BYTES` (Gateway + монолит, body-уровень + приложение, `413 ProblemDetails` `File too large`). Отмена: клиент abort'ит XHR, сервер видит разрыв до/вместо записи — ни записи в БД, ни объекта в S3 не остаётся (проверено E2E: отмена 22 MiB на троттлинге ~1.5 MiB/s). Имена файлов — только текст (без `dangerouslySetInnerHTML`), статусы — `aria-live="polite"`. Тесты: 41 unit-тест (upload-client, upload-queue, validate-upload, UploadPanel) + E2E `e2e/upload.spec.ts` (6 сценариев, включая отмену и 401) — `make e2e` 23/23.

(с шага 3c.1) Origin-проверка Gateway усилена: сравнение разбором URL (`scheme + host + порт`), не строковым префиксом — `Referer: http://localhost:8080.evil.com` → `403`, `https://example.com:443` == `https://example.com`, литерал `Origin: null` → `403`; найден и исправлен баг: env `PUBLIC_ORIGIN` читался как `PublicOrigin` (case-sensitive lookup в .NET 10) и молча игнорировался — регрессионный тест добавлен. Dev-профиль (`make up-dev`) через `infra/compose.dev.yml` добавляет в `PUBLIC_ORIGIN` origin Vite-сервера `http://localhost:5173` (браузер ходит в Vite, `/api` проксируется в Gateway); в production-стек этот origin не попадает (проверено: `make up` + `Origin: http://localhost:5173` → `403`).

(с шага 3c.1a) Rate limiting входа на Gateway (`AuthRateLimitMiddleware` + `SlidingWindowCounter`, ADR 0011 §6.2): `POST /api/auth/login` и `POST /api/auth/register` — скользящее окно, по умолчанию 10 запросов на 60 с на клиента, превышение → `429` ProblemDetails + `Retry-After`. Конфиг: env `RATE_LIMIT_REQUESTS_PER_MINUTE`, `RATE_LIMIT_WINDOW_SECONDS`, `KNOWN_PROXIES` (IP), `KNOWN_NETWORKS` (CIDR) — по умолчанию 10/60 и пустые списки; `X-Forwarded-For` учитывается только если socket peer — доверенный прокси. Middleware стоит до OriginCheck (запросы с чужим Origin тоже расходуют лимит). Тесты: Gateway-тесты (429 + `Retry-After`, независимость ключей, другие маршруты не ограничиваются, XFF-логика, fail-fast на невалидном `KNOWN_*`) + живой прогон (`make up`: 10×401 + 429; `RATE_LIMIT_REQUESTS_PER_MINUTE=100`: 11 запросов без 429; E2E 4/4 с дефолтным лимитом). Dev-профиль: Vite-контейнер работает под `UID:GID` хоста, `node_modules` — в именованном томе `vite-node-modules` (one-shot `vite-node-modules-init` делает `chown`), `HOME=/tmp` — в `frontend/` нет root-файлов (проверено: `make up-dev`, `make down`, `find frontend -user root` → 0).

**Экраны Razor не переносились и не удалялись.**

Скриншот: `docs/screenshots/step3a-spa-stub.png`.
