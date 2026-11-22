# Технический долг (3DPAINT)

Формат: `TD-N` — название; где живёт; почему важно; в каком шаге плана рассматривается (если применимо).
Заполнен на Фазе 0 (2026-11-22). Новые пункты добавляются при обнаружении проблем вне текущей задачи.

## Критичные (ломает работу)

| ID | Проблема | Где | Почему важно |
|---|---|---|---|
| TD-14 | Образ `minio/minio` больше не публикуется в Docker Hub (pull 404); `quay.io/minio/minio` требует авторизации, bitnami/minio пуст | `docker-compose.yml` | Свежий `docker compose up` **не поднимается**. Блокер для всей миграции — нужен новый образ/реестр (решение человека) |
| TD-2 | `MINIO_BUCKET=USERDATA` в `.env.example` — заглавные буквы невалидны для S3-бакета; MinIO SDK бросает `InvalidBucketNameException` | `.env.example`, `FileStorageService` | Любой деплой по примеру: загрузка файлов → **500**. Дефолт в appsettings (`userdata`) рабочий |
| TD-3 | Presigned URL содержит внутренний hostname `storage:9000` | `FileStorageService.GetPresignedUrlAsync` | Из браузера (вне docker-сети) файлы **не грузятся**: превью/«открыть» не работают (`ERR_NAME_NOT_RESOLVED`). Решается на Фазе 2 (gateway) и/или Фазе 5 (публичный endpoint Model Service) |
| TD-4 | Поле логина в форме логина `type="email"`, а логин может быть любой строкой | `Views/Account/Auth.cshtml` | Пользователь с не-email логином **не может войти через UI** (клиентская валидация). Исправить при работе на Фазе 3 (frontend) |

## Средние (корректность/безопасность)

| ID | Проблема | Где | Почему важно |
|---|---|---|---|
| TD-5 | В `Production` обработчик исключений указывает на `/Home/Error`, action отсутствует → 500 с пустым телом, плюс `InvalidOperationException` в логах | `Program.cs`, `HomeController` | Ошибки нечитаемы; на Фазе 2+ заменить на ProblemDetails + центральный middleware |
| TD-12 | Пароли: `SHA-256(salt+password)` без растяжки | `AccountService` | Слабое хеширование. При миграции Auth (Фаза 4) — решение: bcrypt/argon2 + стратегия перехода (перехеш при логине) |
| TD-13 | Multipart-загрузка полностью буферизуется в памяти (`MemoryBufferThreshold = int.MaxValue`), лимит 500 MiB | `Program.cs` | Один запрос на 500 МБ → 500 МБ RAM процесса. На Фазе 5 (Model Service) — стриминг в S3/диск |
| TD-16 | Конфигурация nginx + TLS живого демо (3dpaint.tw1.su) отсутствует в репозитории | README | Прод-инфраструктура невоспроизводима из репо; нужна при работе с gateway |
| TD-20 | Рабочее дерево: `.gitignore` (незакоммиченное изменение) добавляет `AGENTS.md`, `ARCHITECTURE.md`, `IMPLEMENTATION_PLAN.md` в игнор | `.gitignore` | Планировочные документы не попадают в git — **нужно решение человека**: трекить или держать локально |

## Низкие (гигиена)

| ID | Проблема | Где | Заметка |
|---|---|---|---|
| TD-1 | JS-библиотеки закоммичены в git: bootstrap (8.4 МБ), jquery* (1.1 МБ), three.js (1.3 МБ) | `wwwroot/lib/` | На Фазе 3 заменить на Vite + npm (по плану) |
| TD-15 | Бинарный ассет `wwwroot/models/auth-object.glb` (3.7 МБ) в git | `wwwroot/models/` | Демо-объект экрана логина; на Фазе 3 перенести в npm/CDN или оставить осознанно |
| TD-6 | Нет logout, refresh-токенов, `/api/auth/me` | `AccountController` | Покрыть на Фазе 4 (Auth Service) |
| TD-7 | Мёртвый код: `AccountService._fileStorageService` (unused, CS0169/CS8618), `DeleteAccountAsync` (нигде не вызывается), `FileUploadViewModel` (пустая обёртка) | `Services/`, `Models/` | Чистить при касании файла, не массово |
| TD-8 | `Persons.user_id` без FK-ограничения в БД | миграция `InitialCreate` | Данные только под контролем приложения |
| TD-9 | `AppDbContext.cs` лежит в папке `data/` | `data/` | Перенести при рефакторинге структуры |
| TD-10 | `LoginAndReg.csproj` ссылается на несуществующую папку `wwwroot\Новая папка`; неиспользуемый пакет `System.Reactive` | `LoginAndReg.csproj` | Мусор из экспериментов |
| TD-11 | Устаревший атрибут `version:` в `docker-compose.yml` (предупреждение compose) | `docker-compose.yml` | Убрать при первом изменении compose |
| TD-17 | Нет health-эндпоинтов, OpenAPI, rate-limit, CORS-конфига, тестов, CI | весь проект | Поэтапно: health — в каждом сервисе (по плану), OpenAPI — с Phase 2/4, CI — отдельное решение |
| TD-18 | «Миниатюра» — SVG-заглушка, а не превью геометрии; в UI имитируется анимация «генерации превью» | `ThumbnailService`, `home-dashboard.js` | Честные превью — задача processing-воркера (Фаза 6) |
| TD-19 | Декоративные кнопки Google/GitHub/Discord и «модули покраски/заказа» — заглушки без backend | `Views/Account/Auth.cshtml`, `home-dashboard.js` | Не вводить в заблуждение или реализовать — решение человека (вопрос §9 ARCHITECTURE) |
| TD-21 | `global.json` отсутствует; версия SDK закреплена только в Dockerfile | корень | Создать на Фазе 1 (по плану) |

## Прочее (не долг, факты)

- Имя проекта/сборки исторически `LoginAndReg` (csproj) при названии продукта 3DPAINT — переименовывать только как отдельный осознанный шаг.
- Миграции применяются автоматически при старте (`db.Database.Migrate()` в `Program.cs`) — на целевой архитектуре применять отдельной командой (по AGENTS.md).
- `appsettings.Development.json` содержит плейсхолдер-пароль БД — не секрет, но не подменять реальными данными в коммитах.
