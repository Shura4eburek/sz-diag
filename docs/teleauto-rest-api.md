# TeleAuto REST — данные заявок без кликов по Telemart

Для кого: ИИ, ведущий диагностику по СЗ (что спрашивать и как читать ответ), и разработчик
sz-diag. В `szcli` подключено: `sz get`, `sz orders`, `sz call` (раздел 10).

Коротко: с **TeleAuto v1.6.0** заявки, заказы, збірки и история покупок клиента читаются
**напрямую из бэкенда Telemart** — тем же REST API, которым пользуется сам `Telemart.Client`.
Окна клиента, `session.begin/end` и «не трогай мышь» больше не нужны. Ответ приходит
за секунды, а не за несколько минут, и сразу типизированный: числа, даты, серийники
отдельными полями, а не текст ячеек грида.

`sz.fetch` (UI Automation) остаётся запасным путём, на случай если REST недоступен.

Первоисточники на стороне TeleAuto:
- `docs/telemart-api.md` — HTTP API целиком (раздел 5, «Нативный путь»);
- `docs/superpowers/specs/2026-09-29-telemart-native-api-design.md` — разведка, контракт
  бэкенда, резолвер состава ПК, проверки на живых заявках.

---

## 1. Что нужно, чтобы работало

| Условие | Если нет |
|---|---|
| TeleAuto **≥ v1.6.0** запущен | `unavailable` (сервис не отвечает) / `not_found` «нет инструмента sz.get» на старой версии |
| TeleAuto **разблокирован PIN'ом**, в нём сохранены логин/пароль Telemart | `locked` |
| Поднят **VPN** (Pritunl) — `*.telemart.ua` доступны только через него | `unreachable` |
| Рядом с `TeleAuto.exe` лежит файл **`telemart_client_secret`** (в релиз не входит) | `auth_failed` |

Запущенный `Telemart.Client` **не нужен**.

---

## 2. Транспорт — тот же, что у `sz fetch`

Ничего нового в `ErpApiClient` не требуется: тот же `POST /call`, тот же заголовок
`X-TeleAuto-Token`, тот же файл токена рядом с запущенным `TeleAuto.exe`.

```json
POST /call
{ "name": "sz.get", "arguments": { "number": 161716 } }
```

Отличия от `sz.fetch`, важные для кода:

- **Не оборачивать в `ErpSession`.** REST не трогает окна и захват не берёт; `session.begin`
  тут лишний и только заблокирует параллельный `sz fetch`.
- **Таймаут** хватит 60–120 с (`sz.get` делает ~7 запросов к бэкенду, обычно укладывается
  в несколько секунд). 600 с из `ErpOptions` оставить только для `sz.fetch`.
- Можно звать **фоном** и параллельно с работой человека — мышь и клавиатура не нужны.
- Номера — числа (`161716`), строка тоже принимается.

---

## 3. Инструменты

| Инструмент | Аргументы | Что отдаёт |
|---|---|---|
| **`sz.get`** | `number` | всё по заявке одним пакетом (раздел 4) — **начинать с него** |
| `api.request` | `number` | заявка целиком: `{ "request": {...} }` |
| `api.discussions` | `number` | переписка: `{ "discussions": [...] }` |
| `api.repairs` | `number` | ремонты в СЦ: `{ "repairs": [...] }` |
| `api.order` | `order_id` | заказ со строками товаров: `{ "order": {...} }` |
| `api.order.requests` | `order_id` | все СЗ на заказ: `{ "requests": [...] }` |
| `api.order.assemblies` | `order_id` | карточки сборок заказа: `{ "assemblies": [...] }` |
| `api.assembly` | `assembly_id` | карточка сборки: `{ "assembly": {...} }` |
| `api.assembled_computer` | `series` | собранный ПК по серии: `{ "computer": {...} }` |
| **`api.customer.orders`** | `phone` **или** `number`, опц. `limit` (50) | заказы клиента по телефону, новые сверху, с товарами |

Если ресурса нет, `api.order` / `api.assembly` / `api.assembled_computer` отдают `null`
в своём поле, а `api.request` — ошибку `not_found`.

---

## 4. Ответ `sz.get`

```jsonc
{
  "request":       { ... },   // заявка целиком, поля бэкенда Telemart (раздел 5)
  "discussions":   [ ... ],   // переписка по заявке, от старых к новым
  "repairs":       [ ... ],   // отправки в сервисный центр и их заключения
  "configuration": { ... },   // из чего собран ПК (раздел 6)
  "siblings":      [ ... ],   // другие СЗ на тот же заказ
  "customer":      { "phone": "0977667583", "orders_total": 13 }
}
```

`customer` — только подсказка, что у клиента есть история покупок; сами заказы не
вкладываются, чтобы пакет не раздувался. Их достаёт `api.customer.orders` (раздел 7).

### `discussions[]`

```jsonc
{ "id": 50715, "message": "дефект проявлявся при увімкненні-вимкненні…",
  "author": "Юсипів Михайло", "created_on": "2026-09-11T17:53:03",
  "created_by": 617, "by_service_manager": false }
```

`by_service_manager: false` — пишет не сервис, а магазин/менеджер: там часто то, что клиент
сказал на приёмке и что увидели в магазине.

### `repairs[]`

```jsonc
{ "id": 63421, "state_id": 7, "product_name": "ОЗП Kingston DDR4 32GB …",
  "serial_number": null, "defect": "Помилки в пам'яті OCCT CPU+RAM",
  "comment": "…накладні…", "service_center_conclusion": "50802634X0080",
  "service_center_id": 44, "created_on": "…", "completed_on": "…" }
```

### `siblings[]`

```jsonc
{ "id": 160753, "state_id": 7, "product_id": 774302, "product_name": "SAMA 3307 …",
  "sn": null, "stated_defect": null, "created_on": null, "completed_on": null }
```

Повторные обращения по тому же ПК. Список отдаёт сокращённые записи — детали брать
через `api.request` / `api.discussions` / `api.repairs` по `id`.

---

## 5. Поля заявки (`request`)

Имена — как отдаёт бэкенд Telemart: `snake_case`, **включая его опечатку `apppearance`**
(три «p»). Кодировка UTF-8, даты ISO без зоны (киевское время).

| Поле | Смысл | Было в `sz.fetch` |
|---|---|---|
| `id` | номер СЗ | `number` |
| `stated_defect` | заявленный дефект (со слов клиента/приёмки) | `Дефект` |
| `apppearance` | внешний вид | — |
| `inspection` | осмотр при приёмке | — |
| `completeness_comment` | комплектность («тільки пк», «коробка…») | `Комплектація` |
| `comment` | поле «Коментар» заявки (свежее, без кэша) | `Коментар` |
| `requirement_text` | требование («Гарантійний, 30 днів») | `Вимога` |
| `requirement_resolution_text` | решение по требованию | `Рішення` |
| `customer_state_text(_ukr)` | статус, как его видит клиент | `Стан` |
| `sn` | серийник изделия (см. ниже — **три формы**) | `SN` |
| `order_id` | заказ, по которому заявка | `Замовлення` |
| `product_id`, `product_name(_ukr/_en)` | изделие заявки | `Назва` |
| `state_id` | статус заявки (словарь ниже) | — |
| `location`, `location_text` | где изделие сейчас | — |
| `diagnostic_on`, `diagnostic_by` | когда и кто начал диагностику | — |
| `discussions_count`, `documents_count`, `calls_count`, `complaints_count` | сколько чего есть | — |
| `fio`, `phone`, `email` | клиент | — |
| `created_on`, `received_on`, `modified_on`, `completed_on` | даты | — |

**`sn` бывает трёх форм** — по нему одному тип изделия не определять, это делает
`configuration.source`:

- `1521343-29721` — серия собранного у нас ПК: `{заказ сборки}-{номер сборки}`;
- `T8YVCM00L6007UP` — настоящий серийник устройства (отдельный товар);
- `SR-161716` — заглушка у кастомной сборки (или пусто).

### Словари

`state_id` заявки: 1 Звернення · 2 В роботі · 3 Готова · 4 У СЦ · 5 Прийнята ·
6 Завершена · 7 Скасована · 8 На узгодженні.

`location`: 1 Клієнт · 2 Склад · 3 Сервіс · 4 Постачальник · 5 В дорозі.

`state_id` сборки (`configuration.assembly`): 1 Очікування · 2 На складі · 3 Збирається ·
4 Завершена · 5 Зібрана · 6 Тестується · 7 Розібрана · 8 Розбирається.

---

## 6. Состав ПК — `configuration`

```jsonc
{
  "source": "prefab",               // prefab | our_build | no_our_assembly
  "series": "1521343-29721",        // только у prefab
  "assembly": {                     // карточка сборки или null
    "id": 29721, "order_id": 1521343, "nomenclature_series": "1521343-29721",
    "product_name": "HEXO Gaming RTX4060 Pro …", "state_id": 4,
    "assembly_date": "…", "started_on": "…", "assembled_on": "…", "assembled_by": 328,
    "start_test_on": "…", "start_test_by": 328, "completed_on": "…",
    "employee_comment": "…", "customer_comment": null, "system_comment": null
  },
  "components": [                   // null у no_our_assembly
    { "product_id": 417530, "name": "AMD Ryzen 5 5600 …", "quantity": 1,
      "serial_numbers": ["9AFE487U40336_100-000000927"],
      "category_id": 531, "price": 4168.0 }
  ],
  "serviced_item": { "product_id": 698312, "name": "HEXO Gaming RTX4060 Pro …",
                     "sn": "1521343-29721" }
}
```

| `source` | Что это | Откуда состав | Пример |
|---|---|---|---|
| `prefab` | собранный у нас ПК с серией (HEXO / Evolve / Drugon Lucky) | по серии, серийники добраны из карточки сборки | 163419 |
| `our_build` | наша сборка под заказ (кастом) | карточка сборки заказа, цены — из строк заказа | 161716, 159948 |
| `no_our_assembly` | у нас куплено только изделие заявки | состава от нас нет, `components: null` | 161211 |

Что важно при чтении:

- **У `our_build` товар заявки — любой компонент** (на 161716 это корпус). Не считать
  `request.product_name` «машиной»: машина — это `components`.
- **`prefab`: состав лежит под исходным заказом сборки** (`1521343`), а не под заказом,
  по которому клиент купил ПК (`order_id` заявки). Резолвер это учитывает сам.
- Строка услуги «Сборка и тестирование системы» (`product_id 89819`) в `components` не
  попадает. Пакеты услуг вне сборки («Быстрый старт» и т.п.) — тоже.
- `category_id` есть только у `prefab`; `price` — у `prefab` и `our_build`.
- **`no_our_assembly` ≠ «состав неизвестен навсегда».** Частый случай: клиент собрал ПК сам
  из комплектующих, купленных у нас **другими** заказами. Это видно только по истории
  клиента — раздел 7.

---

## 7. История покупок клиента — `api.customer.orders`

```json
{ "name": "api.customer.orders", "arguments": { "number": 161211 } }
{ "name": "api.customer.orders", "arguments": { "phone": "+380 97 766 75 83", "limit": 20 } }
```

```jsonc
{
  "phone": "0977667583",
  "total": 13,
  "orders": [
    { "id": 1737150, "created_on": "2025-10-07T20:07:46", "completed_on": "…",
      "state_id": 4, "state_text": "…", "service_requests_count": 0,
      "products": [
        { "product_id": 612345, "name": "AMD Ryzen 7 9800X3D …", "quantity": 1,
          "price": 21000.0, "serial_numbers": ["…"], "is_service": false }
      ] }
  ]
}
```

- Телефон — в любом виде (`+380…`, `380…`, `0…`, с пробелами и скобками).
- Заказы — **от новых к старым**, сразу со строками товаров; догружать не нужно.
- `is_service: true` — услуга, не товар. Так видна **платная диагностика**
  («Услуга ''Диагностика''», рядом бывает «Гостевой ПК»).
- В истории есть и отменённые/неподтверждённые заказы — смотреть на `state_text`,
  не считать всё купленным.

Пример (СЗ 161211): по заявке у нас куплен только видак, `source: no_our_assembly`. По
истории — 13 заказов: 9800X3D + B850, DDR5 64GB, AIO, SSD, БП 1200W. То есть ПК собран
клиентом из нашей комплектухи, и диагностировать можно по реальному составу.

---

## 8. Как ИИ вести заявку

Команды — `szcli sz get` / `sz orders` / `sz call` (раздел 10); ниже по именам инструментов.

1. **`sz.get <СЗ>`** (`szcli sz get <СЗ>`) — всегда первым. Прочитать `stated_defect`, `inspection`,
   `apppearance`, `comment`, всю `discussions`, `repairs`.
2. **Состав** — из `configuration.components`. Если `source: no_our_assembly`, и при этом
   `customer.orders_total > 1` или в `comment`/переписке есть «комплектуючі купувалися
   у нас», «весь пк», номера заказов — **`api.customer.orders`**, затем собрать состав
   из заказов **до даты заявки**, отбросив услуги и отменённые (`szcli sz orders <СЗ>`).
3. **Повторные обращения** — `siblings`. По каждой интересной: `api.discussions` и
   `api.repairs` — что уже меняли, что нашёл СЦ. Три СЗ на один заказ — сильный сигнал.
4. **Номера заказов в тексте переписки** — `api.order <id>`; сборки по серии из текста —
   `api.assembled_computer <серия>` или `api.assembly <номер после дефиса>`.
5. Если REST ответил `unreachable` / `locked` / `auth_failed` — это среда, а не данные:
   сказать человеку, что починить (раздел 9), и **не** уходить молча в `sz.fetch`
   (он кликает по экрану и требует, чтобы человек не трогал машину).

---

## 9. Ошибки

Успех — HTTP 200 и `result`. Ошибка — не-200 и `{"code": "...", "message": "..."}`.

| code | Причина | Что делать | Код возврата `szcli` |
|---|---|---|---|
| `locked` | TeleAuto не разблокирован PIN'ом / нет логина-пароля | ввести PIN в TeleAuto | 8 |
| `unreachable` | нет связи с `*.telemart.ua` | поднять VPN | 9 |
| `auth_failed` | Telemart не выдал токен | проверить пароль в TeleAuto и файл `telemart_client_secret` | 10 |
| `api_error` | бэкенд вернул ошибку, текст в `message` | повторить; если стабильно — в TeleAuto | 1 |
| `not_found` | нет такой заявки / нет аргумента | проверить номер | 6 |

Остальные коды — как у `sz fetch`: `2` кривой номер СЗ или аргументы · `3` TeleAuto не
отвечает или нет файла токена.

---

## 10. Команды `szcli`

Подключено в `ErpCommand` (разбор — `SzDiag.Erp/ErpRest.cs`). Без `ErpSession`, таймаут 120 с,
можно звать из сессии заявки и фоном.

| Команда | Что делает |
|---|---|
| `szcli sz get <СЗ> [--json]` | `sz.get`: сводка в терминал (дефект, коментар, состав, другие СЗ, подсказка про историю клиента); сырой ответ — `kb/СЗ/<номер>/erp-rest.json`. `--json` — вывести сырой ответ вместо сводки |
| `szcli sz orders <СЗ\|телефон> [--limit N] [--json]` | `api.customer.orders`: 6 цифр — номер СЗ (телефон берётся из заявки), иначе телефон в любом виде |
| `szcli sz call <инструмент> ['<json>']` | любой `sz.get` / `api.*`, ответ — json как есть. UI-инструменты (`session.*`, `filters.*`, `sz.fetch`…) не пускает: код `2` |

`sz get` в kb пишет **только** `erp-rest.json`: `erp.json`, блок `erp:початок/кінець` в
`запит.md` и frontmatter остаются за `sz fetch`.

```powershell
szcli sz get 161716
szcli sz orders 161211 --limit 20
szcli sz call api.order '{"order_id": 1951256}'
szcli sz call api.assembled_computer '{"series": "1521343-29721"}'
```
