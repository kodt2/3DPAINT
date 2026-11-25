# Технический долг (3DPAINT)

Формат: `TD-N` — название; где живёт; почему важно; в каком шаге плана рассматривается (если применимо).
Заполнен на Фазе 0 (2026-11-22). Новые пункты добавляются при обнаружении проблем вне текущей задачи.

## Критичные (ломает работу)

| ID | Проблема | Где | Почему важно |
|---|---|---|---|
| ~~TD-14~~ | ✅ **Решено в шаге 1.0 (2026-11-22):** образ S3-хранилища заменён на `chrislusf/seaweedfs:4.54` (тег зафиксирован), ADR 0009 | `docker-compose.yml` | Чистый `docker compose up` поднимается |
| ~~TD-2~~ | ✅ **Решено в шаге 1.0 (2026-11-22):** `.env.example`: `MINIO_BUCKET=USERDATA` → `userdata` | `.env.example` | Загрузка по примеру не падает с 500 |
| ~~TD-3~~ | ✅ **Решено в шаге 3.0 (2026-11-22):** presigned URL подписываются отдельным клиентом с публичного эндпоинта (`PUBLIC_S3_ENDPOINT`), внутренние операции — с `storage:9000`; S3 публикуется на `127.0.0.1:${S3_PUBLIC_PORT}` + CORS `-s3.allowedOrigins`; браузерные GET/PUT и превью работают (Chromium, сценарий 16 в `CURRENT_STATE.md`) | `FileStorageService`, `MinioOptions.PublicEndpoint`, `infra/compose.yml` | Закрыто: превью/«открыть»/прямой upload из браузера работают; паттерн «два клиента» будет переиспользован в Model Service (Фаза 5) |
| ~~TD-4~~ | ✅ **Решено в шаге 1.0 (2026-11-22):** поле логина `type="email"` → `type="text"` | `Views/Account/Auth.cshtml` | Не-email логины проходят форму; при переносе экрана входа (Фаза 3) учтено |

## Средние (корректность/безопасность)

| ID | Проблема | Где | Почему важно |
|---|---|---|---|
| TD-5 | В `Production` обработчик исключений указывает на `/Home/Error`, action отсутствует → 500 с пустым телом, плюс `InvalidOperationException` в логах | `Program.cs`, `HomeController` | Ошибки нечитаемы; на Фазе 2+ заменить на ProblemDetails + центральный middleware |
| TD-12 | Пароли: `SHA-256(salt+password)` без растяжки | `AccountService` | **Решение (2026-11-22):** в проде нет данных, которые нужно сохранять — миграция хэшей не нужна; Auth Service сразу на ASP.NET Identity со стандартным хэшером (PBKDF2), Фаза 4 |
| TD-13 | Multipart-загрузка полностью буферизуется в памяти (`MemoryBufferThreshold = int.MaxValue`), лимит 500 MiB | `Program.cs` | Один запрос на 500 МБ → 500 МБ RAM процесса. На Фазе 5 (Model Service) — стриминг в S3/диск |
| TD-16 | Конфигурация nginx + TLS живого демо (3dpaint.tw1.su) отсутствует в репозитории | README | Прод-инфраструктура невоспроизводима из репо; нужна при работе с gateway |
| ~~TD-20~~ | ✅ **Решено (2026-11-22):** планировочные документы остаются вне git (решение человека); изменение `.gitignore` закоммитить на Фазе 1 | `.gitignore` | — |
| ~~TD-24~~ | ✅ **Решено в шаге 3c.1 (2026-11-24), bug:** env `PUBLIC_ORIGIN` не читался Gateway — middleware брал только `configuration["PublicOrigin"]`, а env-провайдер регистрирует `PUBLIC_ORIGIN` (case-sensitive lookup), поэтому переменная из env молча игнорировалась (CSRF-проверка работала по умолчанию `http://localhost:{WEB_PORT}`) | `services/gateway/Middleware/OriginCheckMiddleware.cs` | Исправлено: `PublicOrigin` ?? `PUBLIC_ORIGIN` + регрессионный тест. Урок: проверять spelling env-переменных при добавлении |

## Низкие (гигиена)

| ID | Проблема | Где | Заметка |
|---|---|---|---|
| TD-1 | JS-библиотеки закоммичены в git: bootstrap (8.4 МБ), jquery* (1.1 МБ), three.js (1.3 МБ) | `wwwroot/lib/` | На Фазе 3 заменить на Vite + npm (по плану) |
| TD-15 | Бинарный ассет `wwwroot/models/auth-object.glb` (3.7 МБ) в git | `wwwroot/models/` | Демо-объект экрана логина; на Фазе 3 перенести в npm/CDN или оставить осознанно |
| TD-6 | Нет refresh-токенов. **Частично закрыто в 3c.1:** добавлены `POST /api/auth/logout` (удаление cookie) и `GET /api/me` | `legacy/monolith/Controllers/AuthApiController.cs` | Refresh-токены — при необходимости на Фазе 4 (Auth Service) |
| TD-7 | Мёртвый код: `AccountService._fileStorageService` (unused, CS0169/CS8618), `DeleteAccountAsync` (нигде не вызывается), `FileUploadViewModel` (пустая обёртка) | `Services/`, `Models/` | Чистить при касании файла, не массово |
| TD-8 | `Persons.user_id` без FK-ограничения в БД | миграция `InitialCreate` | Данные только под контролем приложения |
| TD-9 | `AppDbContext.cs` лежит в папке `data/` | `data/` | Перенести при рефакторинге структуры |
| TD-10 | `LoginAndReg.csproj` ссылается на несуществующую папку `wwwroot\Новая папка`; неиспользуемый пакет `System.Reactive` | `LoginAndReg.csproj` | Мусор из экспериментов |
| ~~TD-11~~ | ✅ **Решено в шаге 1.0 (2026-11-22):** атрибут `version:` удалён из `docker-compose.yml` | `docker-compose.yml` | — |
| TD-17 | Нет health-эндпоинтов, OpenAPI, rate-limit, CORS-конфига, тестов, CI. **Частично закрыто в 3c.1a:** rate-limit на логин/регистрацию есть на Gateway (in-memory, ADR 0011 §6.2) | весь проект | Поэтапно: health — в каждом сервисе (по плану), OpenAPI — с Phase 2/4, CI — отдельное решение |
| TD-18 | «Миниатюра» — SVG-заглушка, а не превью геометрии; в UI имитируется анимация «генерации превью» | `ThumbnailService`, `home-dashboard.js` | Честные превью — задача processing-воркера (Фаза 6) |
| TD-19 | Декоративные кнопки Google/GitHub/Discord и «модули покраски/заказа» — заглушки без backend | `Views/Account/Auth.cshtml`, `home-dashboard.js` | Не вводить в заблуждение или реализовать — решение человека (вопрос §9 ARCHITECTURE) |
| TD-21 | `global.json` отсутствует; версия SDK закреплена только в Dockerfile | корень | Создать на Фазе 1 (по плану) |
| TD-22 | OpenAPI монолита не описывает `POST /api/files/upload`: Swashbuckle 7.x не моделирует `[FromForm] IFormFile`; акшен исключён из документа через `DocInclusionPredicate` | `Program.cs` (монолит), `frontend/src/api/schema.ts` | Дописать контракт upload в OpenAPI вручную (или заменить генератор) при переносе загрузки (3c/Фаза 5) |
| TD-23 | На мутации `/api/**` нет antiforgery-защиты (в монолите её никогда не было). **Частично mitigated в шаге 3b.0:** держит `SameSite=Lax` + Origin-проверка в Gateway (403) + требование `Content-Type: application/json` для JSON-эндпоинтов (415) — ADR 0011 §6. Финальная CSRF-модель (antiforgery/double-submit) всё ещё не принята | `legacy/monolith`, `services/gateway`, ADR 0011 | При Фазе 4 (Auth Service) принять финальную CSRF-модель для SPA |
| TD-25 | Rate limit логина/регистрации на Gateway — in-memory скользящее окно на процесс: при перезапуске окно сбрасывается, при N репликах Gateway лимит N раз шире (шаг 3c.1a) | `services/gateway/Middleware/SlidingWindowCounter.cs` | Durable/общий счётчик (Redis или аналог) — вместе с инфраструктурой Фаз 4/5, если brute-force станет реальной угрозой (ADR 0011 §6.2) |
| ~~TD-26~~ | ✅ **Решено в шаге 3c.5 (2026-11-25):** локальные копии декодеров three r187 (Draco glTF-сборка, Meshopt inline-wasm, BasisU) копируются `scripts/sync-decoders.mjs` в `public/assets/decoders/three-<версию>/` (hooks `predev`/`prebuild`), грузятся лениво (только когда GLB требует), CDN нигде нет; диагностика неподдерживаемых расширений + таймаут декодирования + `inspect-glb.mjs`; ADR 0014 §2 | `frontend/src/features/viewer/` | Закрыто: Draco/Meshopt/KTX2 GLB открываются (E2E: 5 сценариев в `e2e/compressed-glb.spec.ts`) |
| TD-27 | Старый Razor UI не синхронизирован с новым лимитом (шаг 3c.4, ADR 0015): клиентская подсказка в `home-dashboard.js` всё ещё говорит «Размер должен быть от 1 байта до 500 MB», а на `400`/`413` ProblemDetails показывает общий текст «Ошибка загрузки файла» (читает поле `message`, которого в ProblemDetails нет) | `legacy/monolith/wwwroot/js/home-dashboard.js` | Косметика: сервер авторитетен (лимит и валидация GLB применяются). Исправить только если старому UI продлят жизнь; иначе уходит вместе с Razor-страницами в конце Фазы 3 |
| TD-28 | Временный экран загрузки (шаг 3c.4, ADR 0015): multipart через монолит, тело целиком в памяти (TD-13), body-лимит = `MAX_UPLOAD_BYTES + 1 MiB` envelope-запас (приближение: multipart-обёртка обычно меньше, но гарантировать её размер нельзя) | `frontend/src/features/upload/`, `legacy/monolith/UserFilesController.cs` | Заменить presigned-флоу (ADR 0003) в Фазе 5: экран и multipart-эндпоинт монолита удаляются, стриминг в S3 закрывает TD-13 |
| TD-29 | Пул воркеров Meshopt-декодера (`meshopt_decoder.module.js`, three r187) не имеет `dispose`/`destroy`: после первой загрузки Meshopt-GLB воркеры живут до перезагрузки страницы (Draco/KTX2-пулы диспозятся в `glb-decoders.ts`) | `frontend/src/features/viewer/glb-decoders.ts`, `node_modules/three` | Косметика: воркеры idle-не заняты и не держут GPU-ресурсы; проверить при апгрейде three (новый major может добавить dispose) |
| TD-30 | Vite выносит в `dist/assets/**` декоративные пути декодера из `new URL(..., import.meta.url)` в `DRACOLoader.js` three r187 (standalone-draco + JS-fallback, ~1.2 МБ): файлы собираются, но никогда не запрашиваются (пути переопределяются `setDecoderPath`, ADR 0014 §2) | `dist/assets/` (сборка) | Мёртвый вес сборки; устранилось бы только патчем/форком three — не в шаге |

## Прочее (не долг, факты)

- Имя проекта/сборки исторически `LoginAndReg` (csproj) при названии продукта 3DPAINT — переименовывать только как отдельный осознанный шаг.
- Миграции применяются автоматически при старте (`db.Database.Migrate()` в `Program.cs`) — на целевой архитектуре применять отдельной командой (по AGENTS.md).
- `appsettings.Development.json` содержит плейсхолдер-пароль БД — не секрет, но не подменять реальными данными в коммитах.
