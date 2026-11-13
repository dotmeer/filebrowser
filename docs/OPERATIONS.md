# Эксплуатация

Быстрый старт — в [README](../README.md), ручная приёмка — в
[MANUAL_TEST_SCENARIOS](MANUAL_TEST_SCENARIOS.md).

## Docker и хранение

- Multi-stage образ: `sdk:10.0` → `aspnet:10.0`, внутренний HTTP-порт 8080.
- Процесс непривилегированный (UID 1654 по умолчанию). Переопределение:
  `--user`, Compose `user:` или `FILEBROWSER_UID` / `FILEBROWSER_GID`.
- Compose публикует только `127.0.0.1:8080:8080`. LAN-binding меняется явно.
- `/config` — постоянный writable volume с БД; YAML монтируется read-only.
  Сервис `config-permissions` назначает владельца volume перед запуском.
- Restore после `COPY . .` в Dockerfile нужен для корректного манифеста
  static assets. При изменении сборки проверяйте `/_framework/blazor.web.js`
  и подключение circuit, а не только HTML главной страницы.
- Health check внутри образа не настроен. С хоста доступны `/health/live`
  и `/health/ready`: оба возвращают `200 Healthy`, если приложение отвечает.
  Проверок зависимостей, включая SQLite, сейчас нет.
- Ключи Data Protection находятся в домашнем каталоге контейнера, не
  зашифрованы и не переживают его пересоздание. После пересоздания обновите
  страницу, чтобы получить новые antiforgery-токены.

Пробы оркестратора:

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
```

## Обратный прокси

Потоковый ZIP не имеет Content-Length и может передаваться долго.
Для nginx отключите `proxy_buffering`, для Caddy задайте `flush_interval -1`,
если нужна немедленная передача потока. Настройте таймауты чтения/записи
под большие скачивания. Дополнительное HTTP-сжатие ZIP обычно не требуется;
сам архив использует store, поэтому выигрыш сжатия зависит от содержимого.

Для загрузки файлов:

- nginx: `client_max_body_size 8g;` или больше, если увеличен лимит приложения;
- при необходимости `proxy_request_buffering off`, чтобы прокси не копил
  тело загрузки на диске;
- согласуйте `client_body_timeout`, `proxy_read_timeout`, `proxy_send_timeout`
  с ожидаемой длительностью передачи; для Caddy настройте соответствующие
  ограничения и таймауты вашего развёртывания.

Лимит приложения — `uploads.maxFileBytes` в YAML, по умолчанию 8 ГиБ.
Он применяется к запросу upload вместо стандартного лимита Kestrel около
30 МБ. Ответ 413 может прийти от прокси раньше приложения.

## Служебные файлы и резервное копирование

Не размещайте конфигурацию/SQLite внутри доступного корня без необходимости.
Они видны в листинге. Фоновое удаление защищает активные пути конфигурации,
БД и sidecar-файлов; общей защиты от переименования/перемещения нет.

SQLite должна лежать на локальной ФС, один экземпляр приложения на БД.
Недоступная/повреждённая база останавливает запуск; проверка целостности
не является автоматическим восстановлением.

Резервируйте `config.yaml` и БД средствами хоста при остановленном приложении.
Если DatabasePath вынесен из `/config`, включите его отдельно. Автобэкапов нет.
Не используйте `docker compose down -v` для обычной остановки: это удалит
volume с историей. Пользовательские файлы резервируются отдельно.

## Наблюдаемость и OTLP

Логи — JSON в stdout, UTC, scopes и traceId. Содержимое файлов не логируется.
ProblemDetails файловых эндпоинтов содержит traceId для сопоставления с логом.
Есть ASP.NET Core/runtime instrumentation, spans задач/поиска, метрики очереди,
прогресса и результатов; upload и ZIP имеют собственные метрики.

OTLP по умолчанию выключен. Включается через `OTEL_EXPORTER_OTLP_ENDPOINT`
или signal-specific endpoint. `OTEL_SDK_DISABLED=true` отключает регистрацию
OpenTelemetry. Collector разворачивается отдельно и не обязателен для работы.

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

Для OTLP/HTTP: `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, порт 4318.
Токены храните вне репозитория (секреты окружения/Compose).
Результаты реально выполненных проверок — в [PROJECT_STATE](PROJECT_STATE.md).
