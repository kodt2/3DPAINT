# ADR 0018: Auth Service — RS256/JWKS, ключи, refresh-схема и стратегия тестирования на PostgreSQL (шаг 4.1)

- **Статус:** принято (2026-11-26, шаг 4.1)

## Контекст

Шаг 4.1 создаёт отдельный Auth Service (`services/auth`, ASP.NET Core Web API на
.NET 10, ADR 0012) с ASP.NET Identity (PBKDF2), собственным PostgreSQL
(своя БД и свой пользователь), миграциями и OpenAPI. Он выпускает JWT
(RS256, 15 мин) по схеме ADR 0007: публичные ключи — по JWKS, остальные
сервисы и Gateway будут валидировать токен локально.

Отдельные решения, требующие фиксации:

1. **Ключи подписи.** ADR 0007 требует RS256 и JWKS, но не определяет, как
   хранятся приватные ключи и как устроена ротация. Ключи не могут лежать ни в
   образе, ни в git (запрет AGENTS.md §10), при этом dev-окружение должно
   подниматься «из коробки», а production — отказывать в старте без ключа.
2. **Тесты на PostgreSQL.** В проекте нет автотестов (TD-17); Auth — первый
   сервис, которому нужны интеграционные тесты против реальной СУБД.
   Testcontainers (вариант из плана Фазы 4) требует Docker-в-Docker в среде
   исполнения тестов; команда CI/локальная разработка в этом проекте —
   `docker compose` + Docker-образы SDK (`make test`, `make test-auth`), а не
   хостовый .NET-рантайм с доступом к Docker API из процесса тестов.
3. **Совместимость контракта.** До 4.2 фронтенд и Gateway ходят в Auth только
   через e2e-стенд (прямой порт); основной трафик `/api/auth/**` и `/api/me`
   остаётся на монолите. Контракты `register`/`login`/`logout`/`me` должны
   совпадать с монолитом, иначе смена маршрута Gateway на 4.2 сломает SPA.

## Решение

### 1. Ключи RS256: два файла, named volume, fail-fast в Production

- Ключ — PEM (PKCS#8, 2048 бит, `Jwt:RsaKeySize`). Два файла:
  `Jwt:CurrentKeyFile` (обязателен) и `Jwt:PreviousKeyFile` (опционален).
  `kid` в JWT и JWKS — **JWK Thumbprint (RFC 7638)**: base64url(SHA-256)
  канонического JSON `{"e":…,"kty":"RSA","n":…}`. Это и есть «отпечаток
  ключа» из требований шага.
- **Development:** if-не-существует — генерация. One-shot контейнер
  `auth-keygen` (`command: ["keygen"]`, идемпотентный `GenerateIfMissing`)
  создаёт оба ключа в named volume `auth-keys` (0600). При ручной перезаписи
  `ASPNETCORE_ENVIRONMENT=Development` сервис тоже сам генерирует текущий
  ключ в том же volume. В dev JWKS всегда отдаёт два ключа — путь ротации
  проверяем в dev-стенде без реального обмена ключами.
- **Production:** ключ **обязателен**. `RsaKeyStore.Load()` при отсутствии
  `current.pem` бросает `InvalidOperationException` **до старта HTTP-сервера**
  (fail-fast, проверено: контейнер падает с понятной ошибкой). Оператор
  предоставляет файл через volume/secret; предыдущий ключ — опционален, при
  отсутствии — warning (токены, выпущенные им, не валидируются — это и есть
  окно ротации).
- **Ротация (модель current + previous):** оператор кладёт новый ключ в
  `current.pem`, старый — в `previous.pem`, перезапускает сервис. `JwksService`
  публикует оба ключа (оба `use: sig`, `alg: RS256`), `TokenValidationParameters`
  принимает подпись любым из них; подписывает только `current`. После того как
  все 15-минутные токены, выпущенные старым ключом, истекли, файл
  `previous.pem` можно удалить.
- **Идентичность токена независима от монолита.** `AUTH_JWT_ISSUER` /
  `AUTH_JWT_AUDIENCE` (дефолты `3dpaint.auth` / `3dpaint`) — собственные
  переменные, задокументированы в `infra/env/.env.example` как независимые от
  монолитных `JWT_ISSUER` / `JWT_AUDIENCE`. Смешение не допускается: переходный
  период «cookie ИЛИ JWT» (ADR 0011) предполагает, что валидатор должен
  отличать легаси-токен монолита от токена Auth.
- `kid` в токенах Auth **не совпадает** с `kid` монолита (разные ключи),
  поэтому валидатор с JWKS-кэшем обоих издательств не путает ключи.

### 2. Refresh-токены (часть 4.1; ротация и lockout — 4.2)

- Opaque-токен (32 байта `RandomNumberGenerator`, base64url), **в БД только
  SHA-256 хэш** (колонка `TokenHash`), срок `RefreshTokenOptions` (дефолт
  7 дней). Схема `RefreshTokens` входит в миграцию 4.1 — таблица нужна сразу,
  чтобы 4.2 не менял схему.
- Выдаётся на логине как httpOnly cookie `rt` (`Secure`, `SameSite=Lax`,
  `Path=/api/auth`). `POST /api/auth/refresh` намеренно **не** реализован в
  4.1 (это 4.2: ротация, детект повторного использования, lockout, очистка
  просроченных) — до 4.2 access-токен живёт свои 15 минут и пользователь
  перелогинивается; SPA это не ломает (401 → экран логина).
- Access-токен — cookie `jwt` (`HttpOnly`, `Secure`, `SameSite=Lax`,
  `Path=/`), совпадает с монолитным `AuthCookie` (ADR 0011): браузер не
  хранит никаких токенов.
- Логин при неверном пароле или неизвестном логине — **один и тот же 401**
  `ProblemDetails` с `detail: "Неверный логин или пароль"` (без точки) —
  user-enum не раскрывается. Проверка пароля идёт через один и тот же
  `UserManager.CheckPasswordAsync`-путь для обоих случаев.
- Политика пароля (`PasswordPolicyOptions`, из `appsettings`): минимум 10
  символов + встроенный список из 100 распространённых паролей
  (`Security/CommonPasswords.cs`). Отказ — 400 `ValidationProblemDetails`
  с полем `Password`.

### 3. Компоновка: своя БД, one-shot миграции, без внешнего порта

- `auth-db`: отдельный PostgreSQL (отдельный volume, отдельный пользователь
  `auth`/БД `auth`); пароль — только из `AUTH_DB_PASSWORD` (в
  `infra/env/.env.example` с комментарием; в git только example-значение).
- Миграции и seed (роль `User` через `RoleSeeder`) выполняются **только**
  one-shot контейнером `auth-migrate` (`command: ["migrate"]`) с
  `depends_on: auth-db: service_healthy`. Сам сервис **не** применяет
  миграции при старте (AGENTS.md §5).
- Auth **не публикует портов** ни в основном compose, ни в production-профиле
  (проверяется `make check-prod`, п. 2b скрипта). Единственное исключение —
  e2e-стенд (`infra/compose.e2e.yml`), где порт опубликован для прямых
  интеграционных проверок Auth без Gateway-маршрута (маршрут
  `/api/auth/**` → Auth появится в 4.2).
- Один Dockerfile на `sdk:10.0`/`aspnet:10.0`, `ENTRYPOINT ["dotnet","Auth.dll"]`;
  compose-команды one-shot-контейнеров — только подкоманда (`["keygen"]`,
  `["migrate"]`), т.к. compose дописывает `command` к ENTRYPOINT.

### 4. Тестовая стратегия PostgreSQL (решение, требуемое планом)

**Выбрано: интеграционные тесты против одного общего PostgreSQL-контейнера в
отдельном compose-стенде (`infra/compose.auth-test.yml`), без Testcontainers.**

Обоснование:

1. **Среда.** Тесты .NET-сервисов в проекте исполняются в Docker-образе SDK
   (`make test-auth` = `docker run mcr.microsoft.com/dotnet/sdk:10.0 dotnet
   test`). Testcontainers внутри такого контейнера требует доступа к
   Docker API из-под контейнера (docker-in-docker или bind mount
   `/var/run/docker.sock`) — это новый класс привилегий и новая поверхность
   для поломки, ради чего Auth-тестам нужен бы только PostgreSQL.
2. **Достаточность.** Auth-тесты проверяют HTTP-контракт + поведение
   Identity/EF против реального PostgreSQL (реальные SQL, индексы,
   транзакции, `unique`-ограничения на логин). Интеграционный слой, ради
   которого Testcontainers был бы оправдан, здесь именно СУБД — и один
   общий контейнер её даёт в полную силу.
3. **Изоляция.** Стенд — отдельный compose-проект с собственным
   `app-network` (подсеть `172.31.0.0/16`, Caddy-IP `172.31.0.10` — не
   пересекается с основным стеком `172.28`, e2e `172.29`, e2e-https
   `172.30`), собственной `auth-test-db` (volume `auth-test-db-data`),
   собственными ключами (`auth-test-keys`). `make test-auth` поднимает стенд
   (`up -d --build`), ждёт health, запускает `dotnet test` с
   `ConnectionStrings__AuthConnection` на стенд и **всегда** роняет его
   (`down -v --remove-orphans`, даже при падении тестов) — после запуска не
   остаётся ни контейнеров, ни сетей, ни volumes (требование шага).
4. **Дешевизна и воспроизводимость.** Один `psql`-контейнер стартует за
   секунды; весь стенд — один `make test-auth`, результат детерминирован
   (свежая БД на каждый запуск).
5. **Почему не in-memory / EF InMemory.** ASP.NET Identity + EF Core
   используют SQL-специфику (серверная проверка уникальности, транзакции
   роли/пользователя); InMemory её не покрывает и даёт ложное чувство
   защищённости.

Ограничение, принятое осознанно: тесты не проверяют поведение при
перезаписи ключей вживую (это сценарий оператора, покрывается dev-стендом
рукой) и не дают изоляции «по тесту» (тесты в одном `ICollection` делят
одну БД и один приложение-инстанс; каждый тест регистрирует уникального
пользователя).

Интеграционный стенд e2e (`infra/compose.e2e.yml`) публикует прямой порт
Auth (`AUTH_E2E_PORT`, 18095) — это допущенное исключение (п. 3): Playwright
проходит реальный register/login через Auth в браузере, когда Gateway уже
маршрутизирует туда (после 4.2).

### 5. Совместимость контракта с монолитом

Сравнение сгенерированных типов (`npx openapi-typescript` на `swagger.json`
монолита и Auth):

| Контракт | Монолит | Auth 4.1 | Вердикт |
|---|---|---|---|
| `POST /api/auth/login` → 200/400/401 | `AuthUserDto` | `AuthUserDto` | совпадает |
| `POST /api/auth/register` → 201/400/409 | `RegisterRequest{login,password,fullName*,birthDate*}` | `RegisterRequest{login,password,fullName?,birthDate?}` | **совместимо**: сервер стал строже по паролю, но полнее — фронтенд шлёт оба поля всегда (регистрационная форма собирает fullName+birthDate), принимаются и сохраняются. В сгенерированном TS они становятся optional — на клиентский код не влияет (тип input в `register()` задан вручную). |
| `POST /api/auth/logout` → 204 | — | — | совпадает |
| `GET /api/me` → 200/401 | `AuthUserDto{login,fullName,birthDate}` | `AuthUserDto{id,login,fullName,birthDate}` | **совместимо**: добавлен только `id` (разрешено условием шага) |
| `ProblemDetails` / `ValidationProblemDetails` | есть | есть | совпадает |

Разница маршрутов: в `ARCHITECTURE.md` §7.2 написано `GET /api/auth/me`, но
реальный монолит и фронтенд используют **`GET /api/me`** — Auth реализует
`/api/me` (совместимость важнее; таблица ARCHITECTURE поправляется при
следующей правке доков).

### 6. Нюанс .NET 10: `AddIdentity` перехватывает default-схему аутентификации

**Найдено при отладке 4.1:** `builder.Services.AddIdentity(...)` в .NET 10
ставит `AuthenticationOptions.DefaultAuthenticateScheme =
"Identity.Application"` (cookie-схема). Стандартный `AuthenticationMiddleware`
подхватывает этот default и аутентифицирует **cookie-схемой Identity**, а не
`Bearer` — `UseAuthentication()` не трогает JWT из cookie `jwt`, и
`[Authorize]` отвечал 401 на валидный токен. В .NET 8 этого не наблюдалось
(монолит работает на cookie-аутентификации, Bearer там не подключался).

**Решение:** в Auth являно устанавливаем все три default-схемы на `Bearer`
(после `AddIdentity`):

```csharp
builder.Services
    .AddIdentity<AuthUser, IdentityRole>(...)
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddAuthentication(options =>
    {
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(...);
```

Это делает Auth устойчивым к будущим изменениям Identity (любое `Add*`
схемы больше не сможет незаметно сменить default). Тест
`Me_WithValidToken_Returns200` защищает от регрессии: до фикса он падал
401, после — 200.

### 7. Пастка base64url при тесте подмены JWT

Последний символ base64url-подписи может нести **меньше 6 бит данных**
(остаток от выравнивания до 4 символов). Замена последнего символа на другой
может не изменить ни одного бита реальной подписи — поддельный токен
окажется с **идентичной** подписью и валидируется (тест «подмена подписи →
401» ложно падал 200). Правило, зафиксированное в
`AuthApiTests.TamperSignature`: менять **полную 6-битовую группу в середине**
подписи (позиция `sigOffset + len/2`), а не последний символ.

## Последствия

- **Плюс:** Auth автономен (своя БД, свои ключи, свои миграции), dev
  поднимается без действий оператора, prod без ключа не стартует (нет
  «тихо работающего» Auth с сгенерированным на лету ключом), JWKS всегда
  валиден для ротации (1-2 ключа), тесты — одна команда, без остатков.
- **Плюс:** контракт совпадает с монолитом → 4.2 (маршрут Gateway → Auth)
  не требует менять SPA.
- **Минус:** dev JWKS отдаёт два ключа с самого начала (виртуальная
  «ротация» сразу) — для dev неважно.
- **Минус:** `RefreshTokens`-таблица существует, но endpoint `/refresh`
  появится в 4.2 — до тех пор cookie `rt` «мёртвый груз» (объяснимо: схема
  не меняется в 4.2).
- **Минус:** тесты делят одну БД (изоляция «по коллекции», не «по тесту») —
  принято осознанно (п. 4).
- **Риск:** `DefaultAuthenticateScheme` — скрытое поведение Identity; при
  обновлении .NET 10.x нужно повторно прогонять `AuthApiTests` (они это
  поймают).
- **Риск:** окно ротации (15 мин) — валидатор должен держать оба JWKS-ключа;
  Gateway (4.2) и монолит (при переходе на JWKS) должны кешировать JWKS и
  обновлять при 401/истечении, а не на каждый запрос.

## Альтернативы (отклонены)

1. **Testcontainers для PostgreSQL в тестах** — отклонено: docker-in-docker /
   bind mount Docker socket ради одного `psql`; compose-стенд даёт тот же
   реальный PostgreSQL дешевле и без новых привилегий (п. 4).
2. **EF InMemory / SQLite для тестов** — отклонено: не покрывает SQL-специфику
   Identity (unique, транзакции).
3. **Один ключ без `previous`** — отклонено: условие шага требует два
   (current + previous) и модель ротации; JWKS с одним ключом не даёт
   бесшовной ротации.
4. **Генерация ключа в Production при отсутствии** — отклонено: тихий
   self-signed key в проде = потеря всех валидных токенов при перезапуске +
   невозможность аудитного ротирования; fail-fast безопаснее.
5. **`/api/auth/refresh` сразу в 4.1** — отклонено: условие шага относит
   refresh-ротацию, lockout и cleanup в 4.2; делать endpoint без ротации —
   ложное чувство завершённости.
6. **Маршрут Gateway → Auth в 4.1** — отклонено: условие шага; без ротации
   refresh и lockout переводить реальный трафик на Auth нельзя.
