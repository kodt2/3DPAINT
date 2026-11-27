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

(с шага 4.0) Зависимость `System.Reactive` (6.0.1) удалена — была мёртвой (TD-10), ссылок в коде нет (проверено поиском + сборкой).

Сборка: `dotnet build` (в Docker) — **успешно**, 5 предупреждений:
- CS8618 (×5): неинициализированные non-nullable свойства в `RegistrationViewModel`/`LoginViewModel` (Razor-ViewModel'ы, nullable не включён) — предсуществующие, «на пустом месте».
- CS0618 (×5): устаревшие конструкторы `JsonWebTokenHandler`/`SecurityTokenDescriptor` в `AccountService` (HMACSecurityKey).

(с шага 4.0) Предупреждения CS8618/CS0169 для `AccountService._fileStorageService` и `FileUploadViewModel` исчезли: мёртвое поле и ViewModel удалены (TD-7).

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

> **С 3d.2 (2026-11-25) Razor удалён из монолита** (ADR 0017): строки ниже для `/`, `/Account/*`, `/Home/Index`, `wwwroot/*` больше не обслуживаются монолитом — это **исторический baseline**. Эти пути теперь отдаёт Gateway: `/` и старые Razor-пути `/Home/**`, `/Account/**` → `302 /spa/`, прочее неизвестное → `404`. Живые маршруты монолита — только JSON `/api/**` (см. строки `api/*` ниже и §13).

| Маршрут | Метод | Что делает | Auth |
|---|---|---|---|
| `/` | GET | редирект на `/Account/Auth` | публичный |
| `/Account/Auth` | GET | страница логина/регистрации (Razor) | публичный |
| `/Account/Login` | POST | проверка пароля (SHA256(salt+password)), выдаёт JWT (60 мин) в httpOnly-cookie `jwt`; JSON `{success, message, redirectUrl:"/Home/Index"}` | публичный |
| `/Account/Register` | POST | валидация (FluentValidation: FullName/BirthDate/Login/Password, Password≥6), создаёт `User`+`Person`; JSON `{success, message}` | публичный |
| `/Home/Index` | GET | главная страница — «библиотека ассетов» (Razor) | **JWT** |
| `/api/files` | GET | JSON-список файлов текущего пользователя: `id, fileName, fileSize, createdAt, previewUrl` (presigned 30 мин), `thumbnailUrl` (presigned 30 мин) | **JWT** |
| `/api/files/upload` | POST | multipart `file`: whitelist расширений из конфига `ALLOWED_UPLOAD_EXTENSIONS` (дефолт `.glb`, шаг 4.0; нормализация: точка/регистр/дубли) + размер ≤ `MAX_UPLOAD_BYTES` (дефолт 100 MiB, ADR 0015; body-уровень = лимит + 1 MiB envelope, превышение envelope — «голый» 413, превышение лимита — `413 ProblemDetails` `File too large`); (с 3c.4) для `.glb` — проверка заголовка GLB (magic `glTF`, версия 2, длина ≤ размеру файла и ≤ 64 MiB), иначе `400 ProblemDetails` `Invalid GLB file`; пишет файл в S3 (`{userId}/{fileId}{ext}`) и SVG-заглушку (`{userId}/thumbnails/{fileId}.svg`); JSON-ответ как в `/api/files` | **JWT** |
| `/api/config` | GET | (с 3c.4, ADR 0015) публичная конфигурация SPA: `{maxUploadBytes, allowedUploadExtensions}` — лимит тот же `MAX_UPLOAD_BYTES`, список расширений — **тот же единый источник**, что валидирует сервер (`FileStorageService`, из `ALLOWED_UPLOAD_EXTENSIONS`, шаг 4.0): SPA и сервер не могут расходятся | публичный |
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

> **С 3d.2 (2026-11-25) `wwwroot/` удалён** (ADR 0017): таблица ниже — исторический baseline. Статика Razor-UI (15M) больше не в репозитории и не в образе монолита; SPA несёт свои ассеты через nginx (`/assets/**`).

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
3. **Форматы:** (с шага 4.0) whitelist из конфига `ALLOWED_UPLOAD_EXTENSIONS` (дефолт `.glb` — единственный валидный формат); клиентский и серверный whitelist — единый источник `UploadOptions.AllowedExtensions` (`/api/config` отдаёт тот же список, что валидирует `FileStorageService`).
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
- [ ] (с шага 3c.3) `/spa/models/{id}`: GLB-модель рендерится (canvas, камера по bounding box, OrbitControls), инфо-панель (вершины/треугольники/меши/материалы/текстуры/размеры/UV); чужая модель → «Модель не найдена»; битый GLB → «Файл повреждён»; не-GLB (.stl/.obj/.step/.stp) → уведомление + «Скачать» (с шага 4.0 недостижимо через upload — сервер отклоняет 400 до записи; покрытие — unit `ViewerPage.test.tsx`, E2E `e2e/viewer.spec.ts` проверяет server-side отказ `.stl`).
- [ ] (с шага 3c.1a) после 429 от rate limiting окно **открывается** через `RATE_LIMIT_WINDOW_SECONDS` (65 с) — следующий запрос доходит до монолита (401/200, не 429; проверено live, ADR 0016 §4.2).
- [ ] (шаг H) `make up-https` → сайт и S3 только через Caddy: `https://localhost:443/health/ready` → 200 (внутренний CA), наружу опубликованы только порты Caddy; HSTS-заголовка в lan-режиме **нет** (prod-only); `make e2e-https` — 30/30 (Draco-модель по HTTPS, декодеры с origin сайта, presigned S3 только https, без mixed-content/CORS-ошибок), plain `make e2e` не тронут.

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

(с шага 3c.3) `/spa/models/{id}` — 3D-вьюер (Three.js 0.187 + @react-three/fiber + drei, ленивый чанк ~1 МБ, three.js не грузится на login/library). Данные — `GET /api/models/{id}` (чужая/несуществующая → «Модель не найдена»). GLB: браузер `fetch`-ит presigned URL (progress), `GLTFLoader.parse` с доверенным `LoadingManager` (`setURLModifier` пропускать только `blob:`/`data:` — внешние URI внутри недоверенного GLB блокируются, ADR 0014); http/сеть-ошибка → один повтор со свежим presigned URL (новый `GET /api/models/{id}`), abort при уходе со страницы не считается ошибкой. Сцена: hemisphere+directional свет, drei `Grid` (без HDR/CDN), OrbitControls + «Сбросить камеру», камера по bounding box. Панель: вершины/треугольники/меши/материалы/текстуры/размеры/UV (чистый модуль `model-info.ts`). Потеря WebGL-контекста → оверлей с «Перезагрузить сцену»; нет WebGL → уведомление. При размонтировании сцены GPU-ресурсы освобождаются (`disposeSceneObject`, E2E проверяет счётчик geometries). Не-GLB (.stl/.obj/.step/.stp) — уведомление «Просмотр 3D недоступен» + ссылка «Скачать» (canvas не создаётся). Отдельного лимита вьюера нет (максимум — upload монолита, 500 МБ, TD-13; крупный файл ~51 МБ проверен тестом). Сжатые форматы (Draco/Meshopt/KTX2) — локальные декодеры, см. абзац шага 3c.5 ниже. S3-CORS расширен на все origins SPA (8080/18080/5173/18090 × localhost/127.0.0.1) — `S3_CORS_ORIGINS`. Тесты: 64 unit-теста вьюера (Vitest: model-info, glb-json, decoder-requirements, decoder-assets, glb-loader (включая ~50 МБ GLB, блокировку внешних URI и decoder-aware загрузку), scene-dispose, ViewerPage) + E2E `e2e/viewer.spec.ts` (5 сценариев: рендер + инфо-панель, отсутствие утечки GPU-ресурсов при перемонтировании, битый GLB, чужая модель → 404, .stl → уведомление) и `e2e/compressed-glb.spec.ts` (5 сценариев, шаг 3c.5) — `make e2e` 28/28. Тестовые GLB генерируются в памяти (`src/test/glbfactory.ts`), бинарных фикстур в репозитории нет. Headless Chromium: WebGL2 из коробки (SwiftShader), дополнительные флаги не понадобились.

(с шага 3c.4) `/spa/library` — временный экран загрузки (ADR 0015, заменится presigned-флоу в Фазе 5): панель `UploadPanel` (кнопка «Выбрать файлы (GLB)» + drag&drop; `input[type=file]` скрыт визуально, но фокусируемый с клавиатуры), очередь до 2 одновременных загрузок (`UploadQueue`), прогресс — XHR `upload.onprogress` (`upload-client.ts`, `fetch` не умеет progress), у каждого файла — строка со статусом/прогрессом/«Отменить»/«Повторить». Клиентская валидация **до сети** (`validate-upload.ts`): расширение по `allowedUploadExtensions` из `GET /api/config` (публичный эндпоинт, `ConfigApiController`; список SPA — `[".glb"]`), размер ≤ `maxUploadBytes`, не пустой файл — нарушитель не отправляется (E2E проверяет отсутствие запроса). Обработка ответов: 200/201 → «Готово» + invalidation списка моделей; 400 → `detail` сервера (без «Повторить»); 401 → редирект `/spa/login`; 413 (JSON монолита или «голый» Gateway) → «превышен лимит»; 429/5xx/сеть → «Повторить». Сервер: GLB magic/версия/длина — `400 ProblemDetails` до S3/БД; лимит — единая `MAX_UPLOAD_BYTES` (Gateway + монолит, body-уровень + приложение, `413 ProblemDetails` `File too large`). Отмена: клиент abort'ит XHR, сервер видит разрыв до/вместо записи — ни записи в БД, ни объекта в S3 не остаётся (проверено E2E: отмена 22 MiB на троттлинге ~1.5 MiB/s). Имена файлов — только текст (без `dangerouslySetInnerHTML`), статусы — `aria-live="polite"`. Тесты: 41 unit-тест (upload-client, upload-queue, validate-upload, UploadPanel) + E2E `e2e/upload.spec.ts` (6 сценариев, включая отмену и 401) — `make e2e` 23/23.

(с шага 3c.5, ADR 0014 §2) 3D-вьюер декодирует сжатые GLB — Draco, Meshopt, KTX2/BasisU — **локальными** копиями декодеров three r187 (CDN нигде не используется): `scripts/sync-decoders.mjs` (hooks `predev`/`prebuild`) копирует `draco/gltf/*` и `basis/*` из `node_modules/three` в `public/assets/decoders/three-0.187.0/` (gitignored; версия three в пути — cache-busting) и пишет `src/generated/decoder-version.ts` (коммитится). nginx отдаёт `/assets/decoders/**` с immutable-кэшем, Gateway маршрутизирует `/assets/{**}` на frontend; dev-сервер Vite отдаёт те же пути. Доверенный `LoadingManager` теперь разрешает, кроме `blob:`/`data:`, только префикс `/assets/decoders/three-<версия>/`. Ленивая загрузка: перед парсингом вьюер читает JSON-чанк GLB (`glb-json.ts`: magic, версия, длина, chunk-типы, JSON) и только при `KHR_draco_mesh_compression` / `EXT_meshopt_compression` / `KHR_texture_basisu` динамически импортирует чанк `glb-decoders.ts` (обычный GLB его не получает). Draco — `DRACOLoader` с `setDecoderPath({js, wasm})` на glTF-сборку (объектная форма → `dep_js = null`, без ~700 КБ JS-fallback), `preload()` + `dispose()`; Meshopt — `meshopt_decoder.module.js` (wasm inline base64, Blob-URL воркеры, в r187 без `dispose` — TD-29); KTX2 — `KTX2Loader` + `detectSupport()` через одноразовый WebGL-контекст (сразу `loseContext()`). `extensionsRequired` с расширением вне `SUPPORTED_GLTF_EXTENSIONS` (сверено со списком `GLTFLoader` r187) → отклонение до парсинга с именем расширения (`kind: 'unsupported-extension'`); сбой декодера → `kind: 'compression'`; таймаут фазы декодирования `DECODE_TIMEOUT_MS = 60 с` → `kind: 'decode-timeout'`; при таймауте/отмене декодеры диспозятся. Диагностика: `scripts/inspect-glb.mjs` (`npm run inspect:glb <файл>`, без зависимостей: magic/версия/длина/чанки, расширения, вершины/треугольники, флаги draco/meshopt/ktx2). Тесты: unit (glb-json 10, decoder-requirements 11, decoder-assets 3, glb-loader +decoder-aware, ViewerPage +новые сообщения) + E2E `e2e/compressed-glb.spec.ts` (Draco: рендер 8 вершин/12 треугольников + локальные запросы декодера; Meshopt и Meshopt+quant: 24/12; утечка GPU-ресурсов при перемонтировании Draco-сцены; неподдерживаемое расширение → имя в сообщении; в каждом — «внешних запросов нет»: только origin SPA + presigned S3). Фикстуры генерируются в памяти (`src/test/gltf-fixture.ts`: `@gltf-transform` + `draco3dgltf` + `meshoptimizer`), бинарных файлов в репозитории нет. TD-26 закрыт.

(с шага 3c.1) Origin-проверка Gateway усилена: сравнение разбором URL (`scheme + host + порт`), не строковым префиксом — `Referer: http://localhost:8080.evil.com` → `403`, `https://example.com:443` == `https://example.com`, литерал `Origin: null` → `403`; найден и исправлен баг: env `PUBLIC_ORIGIN` читался как `PublicOrigin` (case-sensitive lookup в .NET 10) и молча игнорировался — регрессионный тест добавлен. Dev-профиль (`make up-dev`) через `infra/compose.dev.yml` добавляет в `PUBLIC_ORIGIN` origin Vite-сервера `http://localhost:5173` (браузер ходит в Vite, `/api` проксируется в Gateway); в production-стек этот origin не попадает (проверено: `make up` + `Origin: http://localhost:5173` → `403`).

(шаг H, ADR 0016) TLS-контур **Caddy** (`caddy:2.11.9-alpine`, тег закреплён): `infra/compose.https.yml` (профиль `https`, `make up-https`/`make down-https`) + `infra/caddy/entrypoint.sh` генерирует Caddyfile из env. Наружу публикуются **только** порты Caddy на `${BIND_ADDR}` (дефолт `127.0.0.1`): сайт `:443` → gateway:8080, S3 `:9443` → storage:9000 (Host/path сохранены — SigV4-подписи не ломаются); Gateway и S3 остаются loopback-only. Режимы `TLS_MODE`: `lan` (дефолт) — `tls internal`, **без HSTS**; `prod` — Let's Encrypt (`ACME_EMAIL`), HSTS, сайт и S3 на `:443` (SNI-роутинг). Корневой сертификат стабилен между пересозданиями контейнера (том `caddydata`), установка на клиентах — README. Сеть `app-network` закреплена за `172.28.0.0/16` (ipam в `infra/compose.yml`), Caddy — за фиксированным IP `172.28.0.10`; Gateway получает `KNOWN_PROXIES: 172.28.0.10` (точный IP, не подсеть — хост через bridge-адрес `172.28.0.1` не может подделывать ключ rate limiting; проверено live: поддельный XFF от хоста игнорируется). Найденные и исправленные в шаге дефекты Gateway: (1) Kestrel на .NET 10 представляет IPv4-пиров как IPv4-mapped IPv6 (`::ffff:172.28.0.10`) — точное сравнение `IPAddress` не считало их равными, и `KNOWN_PROXIES` молча не работал; `TrustedProxyClientKeyResolver` теперь канонизирует адреса (`MapToIPv4`) — 4 регрессионных теста; (2) дефолтный clock `SlidingWindowCounter` брал `Environment.TickCount64` (мс) как ticks (100 нс): окно «60 с» было ~7 дней, на хостах с меньшим аптаймом бакет залипал в 429 до рестарта; исправлено (`× TimeSpan.TicksPerMillisecond`) + тест с реальными часами. E2E: `make e2e-https` — одноразовый стенд, полный прогон через Caddy + `e2e/https-viewer.spec.ts` (проскипится в plain `make e2e`); доверие CA без установки в хост: Chromium — SPKI-список (Caddy предъявляет [leaf, intermediate], в список идёт intermediate), Node — `NODE_EXTRA_CA_CERTS`. Результаты: `make e2e-https` 30/30, `make e2e` 29 passed + 1 skipped, Gateway-тесты 54/54.

(с шага 4.0) Сеть и IP Caddy параметризованы: `app-network` — `${APP_NETWORK_SUBNET}` (дефолт `172.28.0.0/16`), Caddy и `KNOWN_PROXIES` Gateway — `${APP_NETWORK_CADDY_IP}` (дефолт `172.28.0.10`, `infra/compose.https.yml`). Одноразовые E2E-стенды (`make e2e` / `make e2e-https`) поднимаются на отдельных /16 (`172.29.0.0/16` / `172.30.0.0/16`, Caddy `172.30.0.10`), поэтому работают **параллельно** с основным стеком без `make down` (ранее Docker отказывался создавать их сеть: «Pool overlaps with other one on this address space»).

(с шага 3c.1a) Rate limiting входа на Gateway (`AuthRateLimitMiddleware` + `SlidingWindowCounter`, ADR 0011 §6.2): `POST /api/auth/login` и `POST /api/auth/register` — скользящее окно, по умолчанию 10 запросов на 60 с на клиента, превышение → `429` ProblemDetails + `Retry-After`. Конфиг: env `RATE_LIMIT_REQUESTS_PER_MINUTE`, `RATE_LIMIT_WINDOW_SECONDS`, `KNOWN_PROXIES` (IP), `KNOWN_NETWORKS` (CIDR) — по умолчанию 10/60 и пустые списки; `X-Forwarded-For` учитывается только если socket peer — доверенный прокси. Middleware стоит до OriginCheck (запросы с чужим Origin тоже расходуют лимит). Тесты: Gateway-тесты (429 + `Retry-After`, независимость ключей, другие маршруты не ограничиваются, XFF-логика, fail-fast на невалидном `KNOWN_*`) + живой прогон (`make up`: 10×401 + 429; `RATE_LIMIT_REQUESTS_PER_MINUTE=100`: 11 запросов без 429; E2E 4/4 с дефолтным лимитом). Dev-профиль: Vite-контейнер работает под `UID:GID` хоста, `node_modules` — в именованном томе `vite-node-modules` (one-shot `vite-node-modules-init` делает `chown`), `HOME=/tmp` — в `frontend/` нет root-файлов (проверено: `make up-dev`, `make down`, `find frontend -user root` → 0).

(с шага 3d.1) Корень сайта `/` больше не отдаёт Razor-страницу монолита: Gateway-`RootRedirectMiddleware` отвечает на точный `/` для `GET`/`HEAD` **302** с **относительным** `Location: /spa/` (без собственного абсолютного URL — браузер/Caddy резолвят его относительно текущего origin, поэтому нестандартный https-порт за Caddy сохраняется, ADR 0016). `POST /` (и прочие небезопасные методы) редиректу не подлежат и уходят на монолит (корень — страница, не API). Middleware зарегистрирован в `Program.cs` после `OriginCheckMiddleware` и **до** любого `Map*` (health + reverse proxy), чтобы сработать до catch-all-проброса `/` на Razor-корень. Браузер после 302 попадает в SPA, где корень `/spa/` маршрутизируется по `GET /api/me`: гость (401) → `/spa/login`, авторизованный → `/spa/library`. Из SPA удалена заглушка «Проверить /api» (компонент `ApiCheck` + тест + CSS + импорт из `LibraryPage`); эндпоинт `/api/health` **сохранён**. Навигация вьюер↔библиотека — клиентская (React Router pushState, без полной перезагрузки; E2E проверяет маркером на `window`). Тесты: Gateway-тесты 58/58 (GET/HEAD → 302 + относительный `Location`, `POST /` → монолит, `/spa/` → frontend без редиректа; хелпер `CreateNoRedirectClient`, т.к. дефолтный клиент `WebApplicationFactory` авто-follow-ит 302) + E2E `e2e/root-redirect.spec.ts` (`GET /` → гость на `/spa/login`) + `e2e/https-viewer.spec.ts` («root redirect preserves the https origin and non-standard port», проскипится вне https-стенда) + `e2e/viewer.spec.ts` («library ↔ viewer navigation is client-side»). `make e2e` 31 passed + 2 skipped, `make e2e-https` 33 passed. Headless-скриншоты через HTTPS-стенд (Caddy, внутренний CA): `docs/screenshots/3d1-{login,library,viewer}.png` (1280×800) и `3d1-{login,library,viewer}-mobile.png` (390×844); вьюер — со сжатой (Draco) моделью из генератора.

**Экраны Razor не переносились и не удалялись** (3d.2 — только по явной команде).

Скриншот: `docs/screenshots/step3a-spa-stub.png`.

## 12. Смоук-сьюит «после зачистки» (шаг 3d.0)

Набор проверок, который должен оставаться зелёным **после** удаления Razor/`wwwroot` (шаг 3d.2). Это «список того, что должно работать», привязанный к конкретному способу проверки. Сценарии, которые уходят вместе с Razor, вынесены в конец раздела (они из этого сьюита выпадают).

**Как прогонять (минимальный набор):**

```bash
make up                 # основной стек; контейнеры healthy
make check-prod         # production-профиль: наружу только Caddy, нет dev/localhost-origin
make e2e                # Playwright через Gateway (http)
make e2e-https          # Playwright через Caddy (https, внутренний CA) — ОБЯЗАТЕЛЬНО в сьюите
cd frontend && npm ci && npm run lint && npm test && npm run build
docker run --rm -v "$PWD:/work" -w /work/legacy/monolith mcr.microsoft.com/dotnet/sdk:8.0 dotnet build   # монолит
make test               # Gateway-тесты (dotnet test, .NET 10)
```

### 12.1 Сценарий → как проверяется

| # | Сценарий | Как проверяется |
|---|---|---|
| 1 | Стек поднимается, контейнеры healthy, `GET /health/live` и `/health/ready` → 200 | `make up`; E2E ходит в `/health/*` через Gateway; `make check-prod` (конфиг) |
| 2 | Регистрация нового пользователя; дубликат → 409 | E2E `e2e/auth.spec.ts` (`make e2e`, `make e2e-https`) |
| 3 | Логин: неверный пароль → 401; верный → httpOnly-cookie `jwt` | E2E `e2e/auth.spec.ts` |
| 4 | `GET /api/me` без cookie → 401 JSON; с cookie → профиль | E2E `e2e/auth.spec.ts`; ручной `curl` |
| 5 | Библиотека: список только своих моделей, пагинация, пустое состояние | E2E `e2e/library.spec.ts` |
| 6 | Удаление модели: своя → 200 (исчезает из S3 и БД); чужая → 404 | E2E `e2e/library.spec.ts` (IDOR, идемпотентность) |
| 7 | Загрузка: валидный `.glb` появляется в списке; чужое расширение → 400 | E2E `e2e/upload.spec.ts` |
| 8 | Загрузка сверх `MAX_UPLOAD_BYTES` → 413 | E2E `e2e/upload.spec.ts` (через `E2E_MONOLITH_URL`) |
| 9 | 3D-вьюер: GLB рендерится, панель с вершинами/треугольниками | E2E `e2e/viewer.spec.ts` |
| 10 | Сжатые GLB (Draco/Meshopt) декодируются локальными декодерами, без CDN | E2E `e2e/compressed-glb.spec.ts` |
| 11 | Сжатый GLB + presigned через **HTTPS/Caddy**; декодеры с origin-сайта `/assets/decoders/**` | E2E `e2e/https-viewer.spec.ts` (`make e2e-https`) |
| 12 | Origin/CSRF: мутация `/api/**` с чужим/отсутствующим Origin → 403; `text/plain` → 415 | Gateway-тесты (`Gateway.Tests.cs`); ручной `curl` |
| 13 | Rate limiting входа (XFF-доверие, истечение окна) | Gateway-тесты; живой прогон `make up` |
| 14 | Production-стек: наружу только Caddy на `BIND_ADDR`, нет порта монолита, нет dev/localhost-origin | `make check-prod` (`infra/check-prod-stack.sh`) |
| 15 | Корень `/` → 302 → `/spa/` (относительный `Location`), origin сохраняется через Caddy (https, нестандартный порт) | Gateway-тесты + E2E `e2e/root-redirect.spec.ts` (гость → `/spa/login`) и `e2e/https-viewer.spec.ts` (origin/порт через https, шаг 3d.1) |
| 16 | SPA-навигация без полной перезагрузки (библиотека ↔ вьюер, выход) | E2E `e2e/spa-nav.spec.ts` (шаг 3d.1) |
| 17 | `/api/health` остаётся после удаления «Проверить /api» | E2E (health-хелпер); ручной `curl` |
| 18 | Скриншоты: login, library, viewer (сжатая модель), mobile 390 px | `docs/screenshots/` (шаг 3d.1) |

### 12.2 Сценарии, которые уходят вместе с Razor (и почему)

Эти пункты из ручного чек-листа (§10) и baseline (§8) **перестают существовать** после удаления Razor (3d.2) — у них нет соответствия в SPA, либо они заменяются SPA-поведением.

| Сценарий (было) | Станет / почему уходит |
|---|---|
| `GET /` → 200 Razor-страница входа (форма + Three.js демо-фон) | `GET /` → **302 → `/spa/`** → SPA-логин (шаг 3d.1). Three.js-фон был декорацией Razor; в SPA логин без декоративного вьюера — функциональности не теряется |
| `GET /Home/Index` (Razor-дашборд: SVG-превью-карточки, кнопки-заглушки «Живопись»/«Заказ») | SPA `/spa/library`: реальные presigned-превью + 3D-вьюер (3c). Кнопки-заглушки были no-op (нет бэкенда) — переносить нечего |
| `GET /Home/Index` / `GET /api/files` без JWT → серверный 401/редирект на Razor-аутентификацию | SPA обрабатывает незалогиненное состояние **на клиенте** (`IndexRedirect` → `/spa/login`, `ProtectedRoute`). Серверного Razor-редиректа больше нет |
| Razor-формы регистрации/входа (E1: `GET /Account/Register`, `GET /Account/Auth`) | SPA `/spa/register` и `/spa/login` (те же JSON-эндпоинты `AuthApiController`). Экраны Razor дублируют SPA и удаляются |
| Декор Razor-аута: «Забыли пароль?», кнопки Google/GitHub/Discord | Удаляются: в Razor были декоративными (без бэкенда/интеграций), функциональности не было — сохранять нечего |
| Пункт чек-листа «(3b.0) Razor-формы работают» | Пункт исчезает: Razor-UI после 3d.2 отсутствует |
| Baseline #15: SVG-заглушки превью в Razor-дашборде | Заменено реальными presigned-превью/3D-вьюером в SPA (3c) |

**Примечание:** JSON-эндпоинты, которые использовали Razor-экраны (`/api/auth/*`, `/api/models`, `/api/files`, `/api/health`, `/api/me`, `/api/config`), **не удаляются** — ими пользуется SPA. Удаляются только Razor-`Views`, MVC-контроллеры-обёртки над ними (`AccountController`, `HomeController`) и статика `wwwroot/` (см. §11.2).

## 13. Удаление Razor — монолит API-only (шаг 3d.2, 2026-11-25, ADR 0017)

Фактическое состояние после 3d.2 (baseline §3/§6/§11.2 выше — исторический).

### 13.1 Что удалено (80 файлов в git)

- `Views/` — 6 файлов (Razor-шаблоны: `Account/Auth`, `Home/Index`, `_AuthLayout`, `_HomeLayout`, `_HomeLayout.css`, `_ViewImports`).
- `wwwroot/` — 72 файла (CSS/JS, Bootstrap 5, jQuery + validation, three.js 0.161, `models/auth-object.glb` 3.7M, favicon). Всего 15M.
- `Controllers/AccountController.cs`, `Controllers/HomeController.cs` — MVC-обёртки над `IAccountService`/`IFileStorageService` для Razor-форм; SPA использует `AuthApiController` и `/api/models`.

### 13.2 Что сохранено (использует SPA)

- JSON-контроллеры: `AuthApiController`, `ModelsApiController`, `UserFilesController`, `ConfigApiController`, `ApiStatusController` (`/api/health`).
- `Services/`: `AccountService`, `JwtService`, `FileStorageService`, `ThumbnailService`, `AuthCookie`.
- `data/AppDbContext.cs` + миграции EF Core, `Program.cs`-пайплайн (middleware 3b.0/3c.4, JWT, multipart-лимиты).
- Мёртвый, но не Razor-only код **не удалён**, зафиксирован в `TECH_DEBT.md`: `AccountService.DeleteAccountAsync`, `FileUploadViewModel`, `AccountService._fileStorageService` (TD-7), `System.Reactive` (TD-10).

### 13.3 `Program.cs`: ключевое изменение

`AddControllersWithViews()` → `AddControllers()`; удалены `UseStaticFiles`, `UseExceptionHandler("/Home/Error")` (action не существовал, TD-5), конвенционный `MapControllerRoute`. **Добавлен `app.MapControllers()`** — без него после удаления `MapControllerRoute` весь `/api/**` отдавал 404 (атрибут-роутинг `[ApiController]` требует `MapControllers()`; в оригинале их маппил `MapControllerRoute`). Живой прогон подтвердил: после добавления `GET /api/health` → 200.

### 13.4 Маршрутизация Gateway (без catch-all)

| Путь | Метод | Действие |
|---|---|---|
| `/api/**` | все | → монолит |
| `/spa/**`, `/assets/**` | GET/HEAD | → frontend (nginx) |
| `/` | GET/HEAD | `302 /spa/` (относительный `Location`) |
| `/Home/**`, `/Account/**` | GET/HEAD | `302 /spa/` (относительный `Location`) |
| `/Home/**`, `/Account/**` | иные методы | `404` |
| остальное | — | `404` (JSON problem, `MapFallback`) |

Новый `LegacyRazorRedirectMiddleware` (GET/HEAD `/Home/**`, `/Account/**` → 302 `/spa/`), match по префиксу точно или с `/` (lookalike `/Homepage` → 404). Удалён catch-all-маршрут монолита.

### 13.5 Замеры и валидация

| Метрика | До | После |
|---|---|---|
| Файлов в git (`legacy/monolith`) | 122 | 42 (−80) |
| Диск без `obj`/`bin` | 15M | 272K |
| Docker-образ монолита | 360MB | 340MB |
| Чистая сборка (no-cache) | 35.6s | 25.6s |
| Предупреждений (уник.) | 7 | 7 (новых нет) |

Валидация (всё зелёное): `make test` (сборка монолита + 60 Gateway-тестов), `make up` (health 200, маршрутизация проверена живым), `make check-prod` (PASS), `make e2e` (31 passed), `make e2e-https` (33 passed, включая редирект с сохранением origin/порта через Caddy).

## 14. Гигиена и подготовка к Фазе 4 (шаг 4.0, 2026-11-26)

### 14.1 Whitelist загрузок — из конфигурации, единый источник

Whitelist расширений загрузки теперь задаётся переменной `ALLOWED_UPLOAD_EXTENSIONS` (дефолт `.glb`, в `infra/env/.env.example`). Единый источник — `UploadOptions.AllowedExtensions`: его связывает `Program.cs` (`ParseAllowedExtensions`: нормализация — точка/регистр/дубли), из него читает **и** серверная валидация (`FileStorageService`), **и** публичный `/api/config` (`ConfigApiController`). SPA и сервер не могут расходятся: SPA принимает ровно то, что примет сервер. Исторический хардкод (`{.stl,.obj,.step,.stp,.glb}` в `FileStorageService.AllowedExtensions` и `SpaUploadExtensions = [".glb"]` в `ConfigApiController`) удалён. Сервер отклоняет чужое расширение `400` с сообщением `Поддерживаются только файлы: <список>.` **До** проверки GLB-заголовка и записи в S3/БД. Дефолт `.glb` сужает whitelist до вьюера SPA (ADR 0015).

### 14.2 Мёртвый код (TD-7, TD-10)

Удалено: поле `AccountService._fileStorageService` (не инициализировано, не использовано — CS8618/CS0169), метод `AccountService.DeleteAccountAsync(FileUploadViewModel)`, `Models/ViewModels/FileUploadViewModel.cs`, зависимость `System.Reactive` (6.0.1) из `LoginAndReg.csproj`. Подтверждено поиском по репозиторию (ссылок нет) и сборкой. Предупреждения сборки: 7 → 5 (остальные — pre-existing CS8618 в `RegistrationViewModel`/`LoginViewModel` и CS0618 в `AccountService`).

### 14.3 Gateway `/health/ready` — строгий прогон

`UpstreamHealthCheck` пробирует монолит по `GET /api/health` (не корню `/`) и считает upstream готовым **только** при `HTTP 200`: 404/5xx → `503` (`Upstream /api/health returned <status>.`), недоступность (connection error/timeout) → `503` (`Upstream is not reachable.`). URL строится через `GetLeftPart(UriPartial.Path).TrimEnd('/') + "/api/health"` (без двойного слеша). Это закрыло слепое пятно 3d.2: старый прогон корня `/` трактовал любой статус <500 (включая 404) как «достигнуто», маскируя то, что весь `/api/**` отдавал 404. `/health/live` не тронут (только сам Gateway). Тесты: расширен `StubUpstream.SetStatus(path, status)` + `Health_ready_returns_503_when_api_health_returns_404`; Gateway-тесты 62/62.

### 14.4 E2E/HTTPS-стенды параллельно основному стеку

Сеть `app-network` и IP Caddy параметризованы: `APP_NETWORK_SUBNET` (дефолт `172.28.0.0/16`) и `APP_NETWORK_CADDY_IP` (дефолт `172.28.0.10`, идёт и в `KNOWN_PROXIES` Gateway). Makefile поднимает одноразовые стенды на отдельных /16: `e2e` — `172.29.0.0/16`, `e2e-https` — `172.30.0.0/16` (Caddy `172.30.0.10`). Основной стек и любой E2E-стенд живут одновременно — `make down` перед `make e2e`/`make e2e-https` больше не нужен (ранее: «Pool overlaps with other one on this address space»). E2E-тест `e2e/viewer.spec.ts`: сценарий «non-GLB показывает уведомление» заменён на «server-side отказ `.stl` → 400» (через upload недостижимо, §14.1).

### 14.5 Валидация

Всё зелёное: `make test` (сборка монолита, 5 предупреждений pre-existing; Gateway 62/62), `make up` (`/health/live` 200, `/health/ready` 200, `/api/health` 200, `/api/config` `allowedUploadExtensions: [".glb"]`), живой smoke: `.stl`/`.step` → 400 `Поддерживаются только файлы: .glb.`, валидный `.glb` → 200; `make e2e` при поднятом основном стеке (31 passed + 2 skipped, без pool overlap), `make e2e-https` при поднятом основном стеке (33 passed), `make check-prod` (PASS).

## 15. Auth Service (шаг 4.1, 2026-11-26, ADR 0018)

### 15.1 Сервис

`services/auth` — ASP.NET Core Web API (.NET 10, ADR 0012), ASP.NET Identity (PBKDF2, стандартный хэшер), EF Core + Npgsql (своя БД `auth` и свой пользователь `auth` в `auth-db`), Serilog, OpenAPI (Swashbuckle). `TreatWarningsAsErrors`.

Эндпоинты (контракты совместимы с монолитом, `AuthUserDto` + `id`):

| Маршрут | Статусы | Что делает |
|---|---|---|
| `POST /api/auth/register` | 201/400/409 | Регистрация; пароль ≥ 10 символов и не из списка из 100 распространённых (400 по полю `Password`); дубликат логина — 409 |
| `POST /api/auth/login` | 200/400/401 | JWT (RS256, 15 мин) в cookie `jwt` + opaque refresh в cookie `rt`; неверный логин ИЛИ пароль — один и тот же 401 `"Неверный логин или пароль"` |
| `POST /api/auth/logout` | 204 | Стирает cookie `jwt` и `rt` (идемпотентно, аноним-безопасно) |
| `GET /api/me` | 200/401 | Текущий пользователь (user id из `sub`); реализован `/api/me` — как у монолита и SPA (не `/api/auth/me`) |
| `GET /.well-known/jwks.json` | 200 | JWKS: RSA, `use: sig`, `alg: RS256`, `kid` = JWK Thumbprint (RFC 7638); current + previous ключи |
| `/health/live`, `/health/ready` | 200/503 | ready проверяет БД |

### 15.2 Ключи RS256

- PEM PKCS#8 (2048 бит), два файла: `Jwt:CurrentKeyFile` (обязателен) + `Jwt:PreviousKeyFile` (опционален) в named volume `auth-keys` (0600).
- **Development**: if-missing — генерация; one-shot `auth-keygen` (`command: ["keygen"]`, идемпотентный) создаёт оба ключа. **Production**: fail-fast **до старта HTTP** при отсутствии current-ключа (проверено).
- Ротация: оператор ставит новый ключ в `current.pem`, старый — в `previous.pem`; JWKS публикует оба, валидация — любым, подпись — только current.
- Identity токена: `AUTH_JWT_ISSUER`/`AUTH_JWT_AUDIENCE` (дефолты `3dpaint.auth`/`3dpaint`) — **независимы** от монолитных `JWT_ISSUER`/`JWT_AUDIENCE` (в `.env.example`).

### 15.3 Refresh-токены (4.1; с 4.2 — см. раздел 16)

Opaque-токен (32 байта, base64url), в БД — только SHA-256 хэш (таблица `RefreshTokens` — в миграции `InitialCreate`). Cookie `rt` (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/auth` — с 4.2; в 4.1 было `Lax`) выдаётся на логине. Ротация, детект повторного использования, lockout, cleanup — реализованы в **4.2** (раздел 16).

### 15.4 Компоновка

- `auth-db` (PostgreSQL 15, свой volume `auth-db-data`, пароль — `AUTH_DB_PASSWORD` из env) → one-shot `auth-initdb` (пользователь/БД) → one-shot `auth-migrate` (миграции + seed роли `User`) → `auth`. Сервис **не** мигрирует при старте (AGENTS.md §5).
- Auth **без опубликованных портов** в main/dev/prod (проверка `check-prod-stack.sh` §2b); прямой порт — только e2e-стенд (`E2E_AUTH_PORT`, дефолт 18095). Gateway-маршрут `/api/auth/**` → Auth — **4.2**.
- Compose: `auth-keygen`/`auth-migrate`/`auth` + `auth-db` + volume `auth-keys` (в `infra/compose.yml`).

### 15.5 Тесты (стратегия — ADR 0018 §4: compose-стенд, без Testcontainers)

`infra/compose.auth-test.yml` — одноразовый PostgreSQL (`auth-test-db`, volume, сеть `172.31.0.0/16`) + `dotnet/sdk:10.0`. `make test-auth`: build → `migrate` → `keygen` (оба ключа) → `dotnet test` → **всегда** `down -v` (после — ни контейнеров, ни сетей, ни volumes). 15 интеграционных тестов (register/login/me/logout/JWKS/подмена-подписи) — 15/15.

Найдено при отладке (.NET 10, ADR 0018 §6): `AddIdentity` ставит `DefaultAuthenticateScheme = "Identity.Application"` — стандартный auth-middleware аутентифицировал cookie-схемой Identity, а не `Bearer` (валидный JWT → 401). Фикс — явные `Default*Scheme = Bearer` после `AddIdentity`; регрессию держит `Me_WithValidToken_Returns200`.

### 15.6 Валидация

Всё зелёное: `make test-auth` (15/15), Release-сборка (0 предупреждений), `make up` (auth-цепочка up, health 200, портов наружу нет), живой smoke (register → login → me → JWKS → logout → 401), `gen:api`-совместимость (коды ответов 4 путей идентичны монолиту, `AuthUserDto` +`id`), production fail-fast без ключа, `make check-prod` (PASS), `make e2e` (31 passed + 2 skipped), `make e2e-https` (33 passed), секрет-скан диффа (чисто).

## 16. Refresh-ротация, lockout, SPA-обработчик 401 (шаг 4.2, 2026-11-26, ADR 0018 §8–10)

### 16.1 Исправления 4.1 (контракт register + cookie)

- `RegisterRequest`: `fullName` и `birthDate` теперь **обязательны** (как в монолите): `[Required]` + `[StringLength(100)]` / `[BirthDateRange]` (не в будущем, не ранее `1900-01-01`, русское сообщение). Открытый контракт `gen:api` не изменился (схема `RegisterRequest` идентична монолиту — проверено сравнением swagger'ов).
- Cookie-флаги собраны в одном месте — `Auth.Security.AuthCookies`: `jwt` = `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`; `rt` = `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/auth`. Атрибуты `Set-Cookie` проверяются тестами.

### 16.2 `POST /api/auth/refresh` (ротация + детект повторного использования)

- Токены живут **семействами** (`FamilyId`, GUID): login — семейство + первый токен, refresh — новый токен в том же семействе. Старый токен помечается **атомарно** (`ExecuteUpdateAsync` с условием `ReplacedAt IS NULL AND RevokedAt IS NULL AND ExpiresAt > now`; побеждает тот, у кого affected rows = 1) — из N параллельных refresh ровно один 200 (тест: 8 запросов).
- Реплей заменённого токена: внутри grace-окна (`RefreshToken:ReuseGracePeriodSeconds`, дефолт 60 с) — **дубликат** (401, семейство живо); вне окна — **кража** (всё семейство ревакуется, 401, warning с id семейства/пользователя, токен в лог не пишется).
- Все провалы — один 401 ProblemDetails `detail: "Сессия истекла. Войдите снова."`.
- Logout ревакует семейство на сервере (не только cookie).
- Миграция `20261126213344_AddReplacedAtAndFamilyIndex`: `RefreshTokens.ReplacedAt` + индекс `IX_RefreshTokens_FamilyId`.

### 16.3 Lockout

ASP.NET Identity: `Lockout:MaxFailedAttempts=5`, `Lockout:LockoutMinutes=15` (appsettings). Ответ при lockout — **тот же 401 с тем же телом**, что при неверном пароле (решение ADR 0018 §8: 429 отклонён, т.к. он раскрыл бы «аккаунт существует и заблокирован»). Тест: 5 неверных + 6-й с верным паролем — идентичные 401. Остаточный DoS-риск (заблокировать известный логин 5 запросами) принят: первая линия — Gateway IP rate limiting (4.3).

### 16.4 Фоновая очистка

`RefreshTokenCleanupWorker` (BackgroundService, `PeriodicTimer`): раз в час (`RefreshToken:CleanupIntervalHours`) удаляет мёртвые строки (`RevokedAt IS NOT NULL OR ExpiresAt < now`) пакетами по `RefreshToken:CleanupBatchSize` (дефолт 500) до пустого; заминированный внутри grace-окна токен остаётся до истечения `ExpiresAt`; каждый тик — свой DI scope, сбой тика не убивает воркер. Тест `Cleanup_RemovesOnlyDeadRows_InBatches` (пакетом по 1).

### 16.5 SPA: single-flight обработчик 401

`frontend/src/api/session.ts` (опция `fetch` openapi-fetch 0.17, `src/api/client.ts`): 401 на защищённом пути → один общий `POST /api/auth/refresh` на все параллельные 401 → при успехе исходный запрос повторяется ровно один раз (из `clone()`; повтор мимо обработчика — второй круг невозможен) → при провале — один редирект на `/spa/login` (latch; на `/spa/login` и `/spa/register` редирект пропущен — страницы сами шлют `/api/me`). `/api/auth/**` не перехватывается. Пока монолит не знает `/api/auth/refresh` (4.3), refresh отвечает non-2xx и поведение эквивалентно «401 → логин». Ограничения (access-токен живёт ≤ 15 мин без досрочного ревока) — ADR 0018 §10.

### 16.6 Валидация

`make test-auth` (29/29: +8 — ротация, дубликат vs кража, expired/unknown, logout-ревок, 8-way параллельный race, lockout-тело, batched cleanup), frontend: lint + 127 unit (15 файлов; +5 — `session.test.ts`) + build, `make e2e` (32 passed + 2 skipped; +1 — `e2e/session.spec.ts`: неавторизованный `/spa/library` → `/spa/login`, стабильность без цикла), `make e2e-https` (см. отчёт по шагу), `make check-prod`, секрет-скан диффа — чисто.

## 17. Монолит принимает RS256 от Auth; owner — строка (шаг 4.3a, 2026-11-27, ADR 0018 §11)

### 17.1 Что изменилось

- **JWT в монолите — две схемы.** Старые HS256-токены (`JWT_SECRET`) и токены
  Auth RS256 (JWKS с `http://auth:8080/.well-known/jwks.json`, issuer/audience
  из конфига Auth) валидируются в одном процессе; схема выбирается по `alg`
  JOSE-заголовка. Для обоих типов обязательны подпись, issuer, audience, срок;
  HS256-токены теперь несут `iss`/`aud` (`JWT_ISSUER`/`JWT_AUDIENCE`).
  `ClockSkew` — `JWT_CLOCK_SKEW_SECONDS` (дефолт 30 с).
- **JWKS:** ленивая загрузка, кэш в памяти, Auth недоступен → 401 (не 500) и
  старт/`/api/health` работают; неизвестный `kid` → повторный fetch не чаще
  раза в 30 с. Gateway токены **не проверяет** (TD-31; Фаза 5),
  `/.well-known/jwks.json` на Gateway **нет**.
- **Владелец файла — строка.** `"UserFiles".user_id`: `integer` → `text`
  (миграция `20261127001848_UserFilesUserIdToText`, данные `5` → `'5'`,
  FK на `"Users"` снят). RS256: владелец = `sub` (без запроса к БД); HS256:
  lookup login → id, как строка. `"Persons".user_id` — пока `integer` (4.3c).
- **Миграция — one-shot:** `dotnet LoginAndReg.dll migrate` + контейнер
  `monolith-migrate` (web ждёт его завершения; у `db` добавлен healthcheck).
  Веб-процесс мигрировать не может.
- **Поведение для пользователей не изменилось:** Gateway шлёт всё в монолит,
  логин/регистрация — монолит (HS256). Auth-токен при этом уже принимается
  монолитом (live: `/api/me`, `/api/models` с RS256-токеном через Gateway).
- **`gen:api` — два источника:** пути `/api/me` и `/api/auth/**` из OpenAPI
  Auth, остальное — из монолита (`frontend/scripts/gen-api.mjs`); в dev оба
  документа доступны по loopback-портам из `infra/compose.override.yml`
  (`127.0.0.1:18096` Auth, `127.0.0.1:18097` монолит; Gateway `/swagger` не
  маршрутизирует — 3d.2). `npm run gen:api:check` — сравнение с закоммиченным
  `schema.ts`. Diff схемы после 4.3a: +`/api/auth/refresh`, `AuthUserDto.id?`.
- **Тесты монолита:** `legacy/monolith/tests` (.NET 8, xUnit,
  `WebApplicationFactory<Program>`), стенд `infra/compose.monolith-test.yml`
  (Postgres + SeaweedFS, одноразовый). 22 теста: обе схемы, JWKS-сбои,
  троттлинг `kid`, owner/IDOR, миграция (scratch-БД). Запуск:
  `make test-monolith` (входит в `make test`).

### 17.2 Новые env-переменные

| Переменная | Дефолт | Где читается | Назначение |
|---|---|---|---|
| `JWT_CLOCK_SKEW_SECONDS` | 30 | монолит | ClockSkew обеих JWT-схем |
| `MONOLITH_JWKS_URL` | `http://auth:8080/.well-known/jwks.json` | монолит | Адрес JWKS Auth |
| `MONOLITH_JWKS_REQUIRE_HTTPS` | `false` | монолит | Требовать HTTPS для JWKS (fail-fast при неверной комбинации) |
| `MONOLITH_JWKS_REFRESH_THROTTLE_SECONDS` | 30 | монолит | Мин. пауза между повторными fetch JWKS |
| `AUTH_DEV_PORT` | 18096 | dev compose (override) | loopback-порт OpenAPI Auth для `gen:api` |
| `WEB_DEV_PORT` | 18097 | dev compose (override) | loopback-порт OpenAPI монолита для `gen:api` |

### 17.3 Валидация

`make test` (монолит: 0 предупреждений; интеграция 22/22; Gateway 62/62),
`make test-auth` (29/29), frontend: `gen:api` + `gen:api:check` + lint +
127 unit + build, `make e2e` (32 passed + 2 skipped), `make e2e-https`
(34 passed), `make check-prod` (PASS, включая «no ports published» для web/auth),
live-smoke через Gateway: register/login/me/upload/models (HS256),
`/api/me` + `/api/models` с RS256-токеном Auth, подделанный токен → 401,
legacy-`user_id` в БД — строки.
