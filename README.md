# filebrowser

Self-hosted веб-файлменеджер для просмотра и управления файлами через браузер.
Рассчитан на одного владельца или небольшую группу пользователей в доверенной
домашней сети.

Требования и границы MVP — в `docs/SPEC.md`. Текущее состояние — в
`docs/PROJECT_STATE.md`.

## Статус (I01 — каркас)

Реализован каркас приложения: solution, Blazor Web App (Interactive Server),
минимальный HTTP-endpoint `/healthz`, тестовый проект, контейнерный запуск.

Файловые операции, конфигурация (`config.yaml`), SQLite и фоновые задачи
ещё не реализованы — они выполняются в итерациях I02+.

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
docker run --rm -p 127.0.0.1:8080:8080 filemanager
```

Compose (сборка + запуск):

```text
docker compose up --build
docker compose down
```

## Контейнер

- Многоступенчатый `Dockerfile`: сборка в `sdk:10.0`, запуск в `aspnet:10.0`
  от непривилегированного пользователя (UID 1654 из образа; при необходимости
  переопределяется через `--user` / `user:` в compose);
- внутренний порт 8080 (`ASPNETCORE_URLS=http://+:8080`), только HTTP;
- `compose.yaml` публикует по умолчанию только loopback
  (`127.0.0.1:8080:8080`). Для доступа из LAN измените binding намеренно —
  сетевые ограничения в этом случае являются ответственностью оператора;
- `/config` — постоянный том для будущих `config.yaml` и `app.db`
  (задействуется с итерации I02). Для свежего named volume при
  непривилегированном пользователе потребуется настроить владельца и права
  тома — это будет учтено в итерации конфигурации;
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