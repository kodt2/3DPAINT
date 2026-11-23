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
| `/api/files/upload` | POST | multipart `file`: whitelist расширений (STL/OBJ/STEP/STP/GLB) + размер ≤ 500 MiB; пишет файл в S3 (`{userId}/{fileId}{ext}`) и SVG-заглушку (`{userId}/thumbnails/{fileId}.svg`); JSON-ответ как в `/api/files` | **JWT** |
| `wwwroot/*` | GET | статика (см. §6) | публичный |

Прочее:
- **Нет** logout, refresh, `/api/auth/me`, удаления файлов, скачивания отдельным эндпоинтом (только presigned URL).
- **Нет** health-эндпоинтов (`/health/live`, `/health/ready` отсутствуют).
- `UseHttpsRedirection()` подключён, но HTTPS-эндпоинт не опубликован — фактически ничего не делает (в проде TLS режет внешний nginx, конфига которого **нет в репозитории**, см. README).
- В режиме `Production` обработчик исключений указывает на `/Home/Error`, **которого не существует** (нет такого action в `HomeController`) → необработанное исключение = 500 с пустым телом (TD-5).
- CORS в коде не настроен; rate-limiting отсутствует.

## 4. Механизм аутентификации

- **Не ASP.NET Identity.** Собственная схема в `Services/AccountService.cs`:
  - пароль: `SHA-256(salt + password)`, соль — 16 байт (Base64). **Быстрое хеширование без растяжки** (нет PBKDF2/bcrypt/argon2) — см. TD-12.
  - JWT HS256 (HMAC), секрет `JWT_Secret` (env; в `.env.example` — дефолтный dev-секрет), срок **60 минут**, единственная полезная claim — `ClaimTypes.Name` (= логин, `unique_name`). **Нет** roles, refresh-токенов, issuer/audience не валидируются (`ValidateIssuer=false`, `ValidateAudience=false`), проверяются только срок и подпись.
- Токен отдаётся **в httpOnly-cookie `jwt`** (`Secure`, path `/`). Браузерный клиент (`wwwroot/js/auth-logic.js`) использует `fetch` + `credentials: 'same-origin'`, localStorage не использует.
- Сервер также принимает стандартный заголовок `Authorization: Bearer` (дефолт JwtBearer).
- `[Authorize]` — на `HomeController` и `UserFilesController`; cookie подставляется в заголовок через `JwtBearerOptions.OnMessageReceived`.
- **Баг UI (TD-4):** поле «Email» формы логина имеет `type="email"`, а при регистрации логин — произвольная строка (`type="text"`). Пользователь с не-email логином **не может войти через UI** (клиентская валидация email блокирует отправку).
- **Нет logout**: cookie живёт 60 минут; «Выйти» не реализован.

## 5. Где и как хранятся загруженные 3D-файлы

- **Объектное хранилище** (MinIO, S3 API), бакет `userdata` (по appsettings-дефолту; в `.env.example` — `USERDATA`, что ломает загрузку, TD-2).
- Ключи: `{userId}/{fileId}{ext}` (файл) и `{userId}/thumbnails/{fileId}.svg` (миниатюра).
- Доступ из браузера — только **presigned URL** (GET, срок 30 минут).
- **Загрузка:** браузер → web-сервис (multipart, максимум 500 MiB; лимиты Kestrel `MaxRequestBodySize`/`MultipartBodyLengthLimit` = 524 288 000 байт; **весь multipart буферизуется в памяти** — `MemoryBufferThreshold = int.MaxValue`, см. TD-13) → S3.
- **Валидация:** только расширение и размер. **Нет** проверки содержимого, лимитов полигонов, таймаутов.
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
7. **Максимальный размер файла:** 500 MiB (hardcoded + лимиты Kestrel).
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
| Вход/регистрация (E1) | `POST /api/auth/login`, `POST /api/auth/register`, `POST /api/auth/logout`, `GET /api/me` | ⏳ делать в 3c (пока SPA может логиниться legacy-эндпоинтами `/Account/Login`/`Register` — form-encoded; logout отсутствует, TD-6) |
| Библиотека моделей (E2) | `GET /api/files` (уже есть), `POST /api/files/upload` (уже есть, но **исключён из OpenAPI** — Swashbuckle не моделирует `[FromForm] IFormFile`), `DELETE /api/files/{id}`, `GET /api/files/{id}` (метаданные+свежие presigned) | ⏳ `DELETE`/`GET {id}` — новые, сделать при переносе |
| 3D-вьюер модели (**новая функция**, не перенос) | `GET /api/files/{id}/preview` (свежий presigned GET) | ⏳ сделать вместе со вьюером (3c) |
| Paint (ADR 0010, Фаза 6.5) | `POST /api/files/{id}/textures` (версии текстур), `GET /api/files/{id}/textures` | ⏳ не в Фазе 3 |

### 11.4 3D-вьюер — новая функция

В текущем Razor-UI «превью» карточек — это SVG-заглушки (`<img class=asset-thumb>`), т.к. монолит всегда создаёт thumbnail; Three.js-ветка в `home-dashboard.js` для загруженных файлов **не выполняется**. Проверено (шаг 3a): `GLTFLoader` грузит пользовательский GLB напрямую из S3 через presigned GET из оригина Gateway — 2 меша, UV на месте, рендерится. Реальный 3D-вьюер (Three.js / `@react-three/fiber`) добавляется в `frontend/` как новая функция при переносе (3c), оценка отдельно.

### 11.5 Состояние SPA-каркаса (после шага 3a)

`frontend/`: React 19 + Vite 7 + TS strict, ESLint 9 + Prettier, Vitest (jsdom), `openapi-fetch` + сгенерированные типы (`npm run gen:api` из OpenAPI монолита, `Swashbuckle 7.2.0`, только `/api/**`). Dockerfile multi-stage → nginx (SPA-fallback, immutable-кэш `/assets/`). Сервисы compose: `frontend` (nginx) и `frontend-dev` (Vite HMR, профиль `dev`, `make up-dev`). Маршруты Gateway: `/api/**` → монолит, `/assets/**`, `/spa/**` → frontend, остальное → монолит (ADR 0011). Экран-заглушка показывает результат `GET /api/health` и `GET /api/me`. **Экраны Razor не переносились и не удалялись.**

Скриншот: `docs/screenshots/step3a-spa-stub.png`.
