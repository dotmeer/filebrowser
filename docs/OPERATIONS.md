# Эксплуатация

Операционные детали, не нужные для первого запуска: внутренности
Docker-образа, наблюдаемость, резервное копирование. Быстрый старт —
в `README.md`, пошаговые ручные проверки — в
`docs/MANUAL_TEST_SCENARIOS.md`.

## Docker-образ

- Multi-stage `Dockerfile`: сборка в `sdk:10.0`, запуск в `aspnet:10.0`;
- restore выполняется дважды: слой с csproj для кэша пакетов и повторный
  restore после `COPY . .` перед publish. Без второго restore манифест
  статических ресурсов .NET 10 теряет `_framework/blazor.web.js`, и весь
  интерактивный UI в контейнере получает 404 (BUG-001, I13);
- внутренний порт `8080` (`ASPNETCORE_URLS=http://+:8080`), только HTTP;
- процесс работает от непривилегированного пользователя образа
  (UID 1654 по умолчанию);
- UID/GID переопределяются через `--user`, `user:` в compose или
  переменные `FILEBROWSER_UID` / `FILEBROWSER_GID`;
- `compose.yaml` публикует порт только на loopback
  (`127.0.0.1:8080:8080`); доступ из LAN требует явной смены binding
  и собственных сетевых ограничений;
- `/config` — постоянный named volume (`app.db`); `config.yaml`
  монтируется read-only;
- вспомогательный сервис `config-permissions` перед запуском выдаёт
  владельца volume UID/GID процесса;
- `./data` монтируется в `/data` и соответствует примеру конфигурации;
- health check изнутри образа не настроен: в `aspnet`-образе нет
  `curl`. Проверяйте с хоста: `GET /health/live` (liveness) и
  `GET /health/ready` (readiness) — рабочее приложение отвечает `200`
  с телом `Healthy`; при развёртывании под оркестратором используйте
  эти маршруты в livenessProbe/readinessProbe, например:

  ```yaml
  livenessProbe:
    httpGet: { path: /health/live, port: 8080 }
  readinessProbe:
    httpGet: { path: /health/ready, port: 8080 }
  ```
- ключи Data Protection .NET пишутся в домашний каталог контейнера:
  они не переживают пересоздание контейнера и не зашифрованы. В MVP
  нет авторизации и сохраняемого состояния сеанса; после пересоздания
  контейнера обновите страницу в браузере.

## Скачивание архивом за обратным прокси

Архив собирается на лету и передаётся chunked-ответом без
`Content-Length`; для большой папки ответ может длиться минуты.
Если перед контейнером стоит nginx или Caddy:

- отключите буферизацию ответа (`proxy_buffering off` в nginx,
  `flush_interval -1` в Caddy), иначе прокси накапливает архив
  на диске перед отдачей;
- увеличьте таймауты чтения/записи (`proxy_read_timeout`,
  `proxy_send_timeout`) — иначе соединение обрывается на середине;
- не включайте сжатие (`gzip`/`brotli`) для `application/zip`:
  архив уже несжат по построению, повторное сжатие только тратит CPU.

## Загрузка файлов за обратным прокси

Загрузка передаёт тело запроса целиком и может длиться долго; при
превышении лимита сервер отвечает `413`, при обрыве — соединение
закрывается, а временный файл удаляется.

Если перед контейнером стоит nginx или Caddy:

- увеличьте допустимый размер тела запроса: в nginx
  `client_max_body_size 8g;` (или больше, если `uploads.maxFileBytes`
  в `config.yaml` больше); иначе прокси отклонит запрос раньше
  приложения;
- при необходимости отключите буферизацию тела
  (`proxy_request_buffering off` в nginx), чтобы прокси не копил файл
  на диске перед отправкой;
- увеличьте таймауты чтения/записи (`client_body_timeout`,
  `proxy_read_timeout`, `proxy_send_timeout`) для больших файлов;
- не включайте сжатие для тела загрузки.

Предел приложения задаётся в `config.yaml`:

```yaml
uploads:
  maxFileBytes: 8589934592
```

Kestrel получает этот лимит для запроса загрузки; стандартный лимит
Kestrel (около 30 МБ) для неё не действует.

## Служебные файлы в корнях

- Корень может совпадать с `/`, `/config` или другим каталогом, содержащим
  `config.yaml` и `app.db`; приложение специально не скрывает и не защищает
  такие файлы;
- не выбирайте такую конфигурацию без необходимости: переименование
  или удаление `app.db` разрушает историю задач, а повреждённая база
  останавливает последующий запуск;
- перед изменением `app.db` вручную делайте резервную копию средствами
  хоста при остановленном приложении;
- `DatabasePath` должен указывать на файл SQLite: существующий обычный
  файл, не являющийся базой, остановит запуск на `integrity_check`.

## Наблюдаемость и OTLP

Логи:

- JSON в stdout, UTC-время, scopes; содержимое файлов не логируется,
  физические пути наружу не раскрываются;
- `ProblemDetails` ошибок эндпоинта скачивания содержит `traceId`,
  совпадающий со scope лога.

Трассировка и метрики:

- OpenTelemetry: ASP.NET Core instrumentation (весь HTTP-пайплайн,
  включая Blazor и скачивание); HttpClient instrumentation удалён —
  приложение не выполняет исходящих HTTP-вызовов;
- span `filebrowser.task.execute` для каждой фоновой задачи
  (теги: id задачи, тип, состояние, число источников, корень назначения;
  физические пути не добавляются);
- meter `dotmeer.filebrowser`: `tasks.enqueued`, `tasks.rejected`,
  `tasks.started`, `tasks.finished`, `tasks.queue_depth`,
  `tasks.duration`, `tasks.bytes_processed`, `tasks.items_processed`;
- включены базовые ASP.NET Core и runtime-метрики.

OTLP-экспорт (опционально):

- по умолчанию выключен; приложение полностью работоспособно без него;
- активируется, если задан `OTEL_EXPORTER_OTLP_ENDPOINT` или
  signal-specific endpoint; `OTEL_SDK_DISABLED=true` отключает
  регистрацию OpenTelemetry;
- пример для `compose.yaml`:

```yaml
services:
  filebrowser:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317
      OTEL_EXPORTER_OTLP_PROTOCOL: grpc
      OTEL_EXPORTER_OTLP_HEADERS: Authorization=Bearer ${OTEL_TOKEN}
      OTEL_SERVICE_NAME: dotmeer.filebrowser
      OTEL_RESOURCE_ATTRIBUTES: service.namespace=home,deployment.environment=production
```

- `OTEL_TOKEN` держите вне репозитория (`.env`, secrets compose,
  переменные окружения хоста);
- для OTLP/HTTP используйте `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`
  и порт `4318`.

## Резервное копирование

- Резервируйте содержимое `/config`: `config.yaml` и `app.db`;
- копию делайте средствами хоста только при остановленном приложении;
- `app.db` должен лежать на локальной файловой системе, размещение
  на SMB/NFS не поддерживается;
- автоматические бэкапы приложением не создаются; повреждённая БД
  останавливает запуск, автовосстановления нет.

## Проверки

Актуальные результаты проверок образа и среды — в
`docs/PROJECT_STATE.md`. Пошаговые ручные сценарии приёмки — в
`docs/MANUAL_TEST_SCENARIOS.md`.
