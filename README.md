# filebrowser

Self-hosted веб-файлменеджер для просмотра и управления файлами через браузер.
Рассчитан на одного владельца или небольшую группу пользователей в доверенной
домашней сети.

Требования и границы MVP — в `docs/SPEC.md`. Текущее состояние — в
`docs/PROJECT_STATE.md`.

## Статус (I02 — конфигурация и безопасная адресация)

Реализован каркас приложения: solution, Blazor Web App (Interactive Server),
минимальный HTTP-endpoint `/healthz`, тестовый проект, контейнерный запуск,
строгая загрузка YAML-конфигурации и безопасное разрешение файловых путей.

Чтение содержимого каталогов, файловые операции, SQLite и фоновые задачи
ещё не реализованы.

## Конфигурация

Приложение читает YAML один раз при старте. По умолчанию требуется
`/config/config.yaml`; путь можно изменить стандартной настройкой ASP.NET Core
`ConfigPath`.

Перед локальным запуском создайте конфигурацию из примера и замените путь
корня на существующий абсолютный путь вашей ОС:

```powershell
Copy-Item config.example.yaml config.yaml
$env:ConfigPath = (Resolve-Path config.yaml)
dotnet run --project src/dotmeer.filebrowser
```

`config.yaml` не добавляется в Git. В конфигурации обязателен хотя бы один
корень, `workers` допускает значения от 1 до 16 (по умолчанию 2). Имена
корней уникальны без учёта регистра и соответствуют
`[a-z0-9][a-z0-9_-]{0,63}`. Корни не могут пересекаться между собой или с
каталогом конфигурации; символические ссылки в их существующих компонентах
запрещены. Имя корня является частью публичных ссылок, поэтому его смена
делает ранее сохранённые URL нерабочими.

Публичная адресация файлов использует только `{ root, path }`, где `path`
относителен и использует `/`. Абсолютные пути, `.`/`..`, обратный слеш и NUL
отклоняются. Физические пути не предназначены для клиентского контракта.

Недоступный корень не препятствует запуску: его доступность проверяется при
каждом обращении. Отсутствующий или невалидный `config.yaml` завершает запуск.

## Требования

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
  (версия зафиксирована в `global.json`: `10.0.401`, политика roll-forward —
  `latestFeature`);
- Docker (опционально) для запуска в контейнере.

## Команды

### Сборка и тесты

```text
dotnet build
dotnet test
```

### Запуск локально

Профиль по умолчанию из `launchSettings.json`:

```text
dotnet run --project src/dotmeer.filebrowser
```

С явным адресом (внутренний порт контейнера — 8080):

```text
dotnet run --project src/dotmeer.filebrowser --urls http://127.0.0.1:8080
```

### Health check

`GET /healthz`:

```text
curl http://127.0.0.1:8080/healthz
```

PowerShell:

```powershell
(Invoke-WebRequest http://127.0.0.1:8080/healthz).StatusCode
```

Ответ работающего приложения: `200` с телом `{"status":"ok"}`.

### Docker

Сборка образа:

```text
docker build -t filemanager .
```

Запуск контейнера:

```text
docker run --rm -p 127.0.0.1:8080:8080 \
  -v ./config.yaml:/config/config.yaml:ro \
  -v ./data:/data filemanager
```

Compose (сборка + запуск):

```text
docker compose up --build
docker compose down
```

Перед первым запуском Compose создайте `config.yaml`:

```text
copy config.example.yaml config.yaml
docker compose up --build
```

## Контейнер

- Многоступенчатый `Dockerfile`: сборка в `sdk:10.0`, запуск в `aspnet:10.0`
  от непривилегированного пользователя (UID 1654 из образа; при необходимости
  переопределяется через `--user` / `user:` в compose);
- внутренний порт 8080 (`ASPNETCORE_URLS=http://+:8080`), только HTTP;
- `compose.yaml` публикует по умолчанию только loopback
  (`127.0.0.1:8080:8080`). Для доступа из LAN измените binding намеренно —
  сетевые ограничения в этом случае являются ответственностью оператора;
- `/config` — постоянный named volume для `app.db`; Compose монтирует
  `./config.yaml` в `/config/config.yaml` только для чтения. Сервис
  `config-permissions` перед запуском выдаёт named volume UID/GID процесса;
  по умолчанию это `1654:1654`, при необходимости задайте
  `FILEBROWSER_UID` и `FILEBROWSER_GID`;
- `./data` монтируется в `/data`, которому соответствует пример конфигурации;
- health check изнутри образа не настроен: в `aspnet`-образе нет `curl`.
  Проверяйте `GET /healthz` с хоста.
- Проверено в среде разработки: `docker build -t filemanager .`,
  `docker compose up --build` и smoke-тест контейнера — `/healthz` отвечает
  `200`; приложение в контейнере работает от непривилегированного
  пользователя (uid 1654, не root).

## `/healthz`

- `200` — приложение готово обслуживать запросы;
- `503` — появится в следующих итерациях при введении критичных зависимостей
  (например недоступной SQLite);
- проверка не выполняет запись в корни данных;
- тело ответа не содержит физических путей.

## Структура репозитория

```text
.
├── dotmeer.filebrowser.slnx
├── Dockerfile
├── compose.yaml
├── global.json
├── docs/                  # SPEC и состояние проекта
├── src/dotmeer.filebrowser/       # Blazor Web App (Interactive Server), Minimal API
└── tests/dotmeer.filebrowser.Tests/  # xUnit, WebApplicationFactory
```

## Известные ограничения

- Нет авторизации: приложение предназначено только для доверенной сети;
- загрузка файлов, скачивание папок (ZIP), предпросмотр, поиск — вне MVP;
- детальные правила — в `docs/SPEC.md` (границы MVP, §3).
