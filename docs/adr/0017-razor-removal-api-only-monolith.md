# ADR 0017: Удаление Razor — монолит становится API-only, Gateway без catch-all (шаг 3d.2)

- **Статус:** принято (2026-11-25, шаг 3d.2)

## Контекст

- Фазы 3a–3c построили SPA (Vite + TS + Three.js), которая обслуживает все
  экраны: логин/регистрация (`/spa/login`, `/spa/register`), библиотека
  (`/spa/library`), вьюер (`/spa/models/{id}`). SPA ходит в JSON-эндпоинты
  `/api/**` монолита.
- Шаг 3d.0 создал смоук-сьюит «после зачистки» (`CURRENT_STATE.md` §12) и
  тег `pre-spa-root`; шаг 3d.1 — корневой редирект Gateway (`GET/HEAD /` →
  `302 /spa/`, относительный `Location`, ADR 0016-логика) и убрал экран
  «Проверить /api». Тег `pre-razor-removal` зафиксирован перед 3d.2.
- После 3d.1 Razor-интерфейс монолита (`Views/`, `wwwroot/`,
  `AccountController`, `HomeController`) **не используется**: ни один живой
  сценарий не открывает Razor-страницы — всё через SPA. Это подтверждено
  смоук-сьюитом §12 и E2E.
- Требования человека (шаг 3d.2): удалить только то, что доказанно
  Razor-only; сомнительное/мёртвое, но не доказанное, — в `TECH_DEBT.md`, не
  удалять; сохранить весь `/api/**`; Gateway — явные маршруты без catch-all
  (`/api/**` → монолит, `/spa/**` + `/assets/**` → frontend, `/` → 302 `/spa/`,
  старые Razor-пути → 302 `/spa/`, неизвестные → 404).

## Решение

### 1. Что удалено из монолита (80 файлов)

| Что | Сколько | Почему Razor-only |
|---|---|---|
| `Views/` (`Account/Auth.cshtml`, `Home/Index.cshtml`, `Shared/_AuthLayout.cshtml`, `Shared/_HomeLayout.cshtml`, `Shared/_HomeLayout.css`, `_ViewImports.cshtml`) | 6 | Razor-шаблоны экранов, которых нет в SPA-форме |
| `wwwroot/` (CSS/JS, Bootstrap, jQuery, three.js, `models/auth-object.glb`, favicon) | 72 | Статика Razor-UI; SPA несёт свои ассеты через nginx |
| `Controllers/AccountController.cs` | 1 | MVC-обёртка над `IAccountService` для Razor-форм логина/регистрации; SPA использует `AuthApiController` |
| `Controllers/HomeController.cs` | 1 | Razor-дашборд «библиотека ассетов»; SPA `/spa/library` использует `/api/models` |

Доказательство «не нужно»: ни один из этих файлов не читается SPA (SPA ходит
только в `/api/**` и `/spa/**`/`/assets/**`), E2E и смоук-сьюит §12 зелёные
после удаления. `JwtService`, `AuthCookie`, `FileStorageService`, `AppDbContext`,
миграции, `AuthApiController` и остальные JSON-контроллеры **сохранены** — ими
пользуется SPA.

### 2. `Program.cs` монолита

- `AddControllersWithViews()` → `AddControllers()`: Razor view-engine больше не
  нужен.
- `UseStaticFiles()` + `FileExtensionContentTypeProvider` — удалены: статика
  Razor (`wwwroot`) удалена, SPA отдаёт свои ассеты nginx.
- `UseExceptionHandler("/Home/Error")` — удалён: action `Home.Error` не
  существовал (TD-5, латентный баг), обработчик был сломан.
- `MapControllerRoute(name:"default", pattern:"{controller=Account}/…")` —
  удалён: конвенционный маршрут Razor-контроллеров, которых больше нет.
- **Добавлен `app.MapControllers()`.** Ключевой момент: все выжившие
  контроллеры — с атрибут-роутингом (`[ApiController]`, `[Route("api")]`). В
  оригинале их маппил `MapControllerRoute` (ASP.NET Core регистрирует
  атрибут-роуты при любом `Map*` контроллеров); после его удаления без
  `MapControllers()` весь `/api/**` отдавал 404 (обнаружено живым прогоном:
  `GET /api/health` → 404). `MapControllers()` — каноническая точка маппинга
  атрибут-роутов; после добавления `/api/**` работает.

### 3. `LoginAndReg.csproj`

- Удалены мёртвые `Remove`-записи `Pages\**` и `<None Include="wwwroot\js\…">`,
  `<Folder Include="wwwroot\Новая папка\">` (ссылки на удалённую статику, TD-10).
- `System.Reactive` **сохранён**: неиспользуемый, но не Razor-only (не
  доказанно мёртвый) — по правилу «сомнительное → TECH_DEBT», не удалять.

### 4. Маршрутизация Gateway (без catch-all)

Явные маршруты, неизвестные пути — `404` (JSON problem), без прокси-«сбора»
в монолит/frontend:

| Путь | Метод | Действие |
|---|---|---|
| `/api/**` | все | → монолит |
| `/spa/**`, `/assets/**` | GET/HEAD | → frontend (nginx) |
| `/` | GET/HEAD | `302 /spa/` (относительный `Location`) |
| `/Home/**`, `/Account/**` | GET/HEAD | `302 /spa/` (относительный `Location`) |
| `/Home/**`, `/Account/**` | иные методы | `404` |
| остальное | — | `404` (JSON problem) |

- Новый `LegacyRazorRedirectMiddleware`: перехватывает GET/HEAD на
  `/Home/**` и `/Account/**` → `302 /spa/`. Совпадает по префиксу **точно** или
  с завершающим `/` (чтобы `/Homepage`, `/Accountant` — lookalike — не
  редиректились, а уходили в 404). Относительный `Location` (без host/port) —
  тот же приём, что корневой редирект (3d.1): origin и нестандартный порт
  (https/Caddy) сохраняются.
- Удалён маршрут-«сбор» монолита (`monolithRoute` catch-all): он прятал
  404-и и противоречил требованию «неизвестные → 404».
- `app.MapFallback` → `404` JSON problem для неизвестных путей (без catch-all).
- Тесты Gateway: добавлены `Unknown_paths_return_404_and_reach_no_upstream`,
  `Legacy_razor_routes_redirect_to_spa_with_relative_location` (включая
  lookalike-404), изменены proxy/correlation-тесты на реальные `/api/**` пути;
  `Route_priority_catch_all_goes_to_monolith` заменён на 404-тест. Итог — 60 тестов.

### 5. Замеры (до/после)

| Метрика | До (`pre-razor-removal`) | После (3d.2) |
|---|---|---|
| Файлов в git (`legacy/monolith`) | 122 | 42 (−80) |
| Диск без `obj`/`bin` | 15M | 272K |
| `wwwroot/` | 15M | удалён |
| `Views/` | 64K | удалены |
| Docker-образ монолита | 360MB | 340MB |
| Время чистой сборки (no-cache) | 35.6s | 25.6s |
| Предупреждений сборки (уник.) | 7 | 7 (новых нет) |

## Последствия

- Монолит — **API-only** ASP.NET Core: JSON-контроллеры `/api/**`, `AddControllers`,
  `MapControllers()`, без view-engine и статики. `Dockerfile` (generic `COPY . .` +
  `dotnet publish`) Razor-специфичных правок не потребовал.
- 7 предупреждений сборки — **предсуществующие** (CS8618/CS0169 на
  `RegistrationViewModel`, `LoginViewModel`, `AccountService._fileStorageService`)
  и не являются Razor-only — не чинятся в этом шаге (TECH_DEBT).
- Мёртвый, но не Razor-only код **не удаляется** и фиксируется в
  `TECH_DEBT.md` (TD-7: `DeleteAccountAsync`, `FileUploadViewModel`,
  `AccountService._fileStorageService`; TD-10: `System.Reactive`).
- `FileStorageService`-whitelist (`.stl/.obj/.step/.stp/.glb`) **не сужен**:
  он server-side авторитетен для `/api/files/upload`; SPA принимает только
  `.glb` (`/api/config`). Решение о сужении — за человеком, не в этом шаге.
- Старые Razor-пути не «молча» 404-ят, а уводят на SPA (302), как и требовалось.
- Gateway-`/health/ready` (`UpstreamHealthCheck`) пробует корень `/` монолита и
  считает healthy любой статус <500 — он **не ловил** регрессию API-404
  (корень 404 — всё равно «достигнуто»). Регрессию поймал живой прогон `/api/health`.

## Альтернативы (отклонены)

- **Оставить Razor-контроллеры как «запасной» UI** — дублирует SPA, тянет
  `AddControllersWithViews` + статический view-engine + 15M `wwwroot` в образ;
  против требования «удалить Razor-only».
- **Оставить catch-all-маршрут монолита в Gateway** — прячет 404, против
  требования «неизвестные → 404, без catch-all».
- **Сразу удалить мёртвый код (`DeleteAccountAsync` и др.)** — он не доказанно
  Razor-only, а «недокументированно мёртв»; по правилу — в TECH_DEBT, не
  удалять без подтверждения.
- **Сменить `AddControllers` → чистое Minimal API** — сверх шага, ломает
  существующие `[ApiController]` и OpenAPI-генерацию; не в 3d.2.
