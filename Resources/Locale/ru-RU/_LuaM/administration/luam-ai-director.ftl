luam-ai-director-title = ИИ-диспетчер LuaM
luam-ai-director-screen-title = Центр управления ИИ-диспетчером
luam-ai-director-loading = Ожидание первого состояния сервера. Действия заблокированы.
luam-ai-director-confirm-short = Нажмите ещё раз
luam-ai-director-confirmation-title = Действие с влиянием на сервер ожидает подтверждения
luam-ai-director-game-master-warning-title = Режим ведущего: прямое выполнение
luam-ai-director-game-master-warning = Сервер может выполнять игровые действия без обычной очереди подтверждения. Опасные кнопки в этом окне требуют второго осознанного нажатия.
luam-ai-director-context-title = Контекст действий
luam-ai-director-context-hint = Выбранные режим и цель действуют для команд на всех вкладках.
luam-ai-director-target-none = Выберите игрока...

luam-ai-director-tab-overview = Обзор
luam-ai-director-tab-advisor = Советник
luam-ai-director-tab-operations = Операции
luam-ai-director-tab-base = ИИ-база и корабли
luam-ai-director-tab-audit = Аудит и приватность

luam-ai-director-overview-title = Обзор раунда
luam-ai-director-overview-hint = Сначала оцените состояние: безопасные запросы отделены от изменений игрового мира.
luam-ai-director-section-snapshot = Безопасная проверка
luam-ai-director-section-snapshot-hint = Эти кнопки запрашивают статус, советы или историю и не изменяют игровой мир.
luam-ai-director-section-outcome = Последний результат

luam-ai-director-advisor-title = ИИ-советник
luam-ai-director-advisor-hint = Задайте вопрос, проверьте основания и изучите рекомендацию до выполнения.
luam-ai-director-chat-hint = Обычный чат предназначен для консультаций. Распознанные команды ИИ-базы и кораблей всё равно проходят серверную политику; для штатного управления используйте вкладки операций.
luam-ai-director-recommendations-hint = Отфильтруйте советы по риску и источнику, выберите один и проверьте предпросмотр с чеклистом.

luam-ai-director-operations-title = Операции раунда
luam-ai-director-operations-hint = Все команды ниже могут изменить сектор. Игрок-цель никогда не выбирается автоматически.
luam-ai-director-section-custom-process = Пользовательский процесс ИИ
luam-ai-director-section-custom-process-hint = Внешний API используется только для этого процесса. Чат и обзор подчиняются отдельной политике сервера.
luam-ai-director-section-targeted = События и давление на цель
luam-ai-director-section-targeted-hint = Нужен явно выбранный игрок. Опасные команды требуют второго осознанного нажатия.
luam-ai-director-section-conditions = Условия сектора
luam-ai-director-section-conditions-hint = Создаёт длительное условие; кнопка снятия убирает следующее доступное условие.
luam-ai-director-section-control = Управление сектором и очистка
luam-ai-director-section-control-hint = Давление ИИ и управление синтетиками опасны; очистка ограничена локальными объектами.
luam-ai-director-section-support = Поддержка цели
luam-ai-director-section-support-hint = После подтверждения создаёт один предмет поддержки рядом с выбранным игроком.
luam-ai-director-section-comms = Связь
luam-ai-director-section-comms-hint = Отправляет видимое игрокам сообщение; объявление имеет высокий риск.

luam-ai-director-base-title = ИИ-база и развёртывание кораблей
luam-ai-director-base-hint = Проверка безопасна. Развитие и корабли создают постоянные объекты раунда.
luam-ai-director-section-ai-base-status = Текущее состояние ИИ-базы
luam-ai-director-section-ai-base-inspect = Диагностика и план
luam-ai-director-section-ai-base-inspect-hint = Безопасная проверка: корабли, гриды и дроны не создаются.
luam-ai-director-section-ai-base-actions = Развитие ИИ-базы
luam-ai-director-section-ai-base-actions-hint = Высокий риск: может создать базу, корабли или дронов и изменить память сектора.
luam-ai-director-section-ships = Развёртывание кораблей
luam-ai-director-section-ships-hint = Высокий риск: создаёт один постоянный корабль или грид рядом с администратором.
luam-ai-director-section-ship-presets = Быстрые пресеты
luam-ai-director-ai-base-created = развёрнута
luam-ai-director-ai-base-not-created = не развёрнута
luam-ai-director-ai-base-no-data = отчёта пока нет
luam-ai-director-ai-base-status =
    База: { $created } | снабжение: { $supply } | торговых циклов: { $trade }
    Сводка: { $summary }
    Диагностика: { $diagnostics }
    Последнее автоисправление: { $autofix }
    План развития: { $plan }

luam-ai-director-audit-title = История, аудит и приватность
luam-ai-director-audit-hint = Здесь видно, что предложил ИИ, что одобрил оператор и какие данные покинули сервер.
luam-ai-director-review-history-hint = Обзоры провайдера, сохранённые в текущем сеансе окна.
luam-ai-director-action-history-hint = Подтверждённые, отменённые и заблокированные решения текущего сеанса.
luam-ai-director-privacy-hint = Бюджет внешних запросов, маскирование чувствительных данных, скрытые поля и границы источников.
luam-ai-director-operations-audit-hint = Операционные счётчики, блокировки и последние результаты выполнения.

luam-ai-director-tooltip-refresh = Запросить у сервера актуальное состояние диспетчера.
luam-ai-director-tooltip-toggle = Включение автоматического ИИ меняет поведение раунда и обычно открывает подтверждение сервера; выключение выполняется сразу.
luam-ai-director-tooltip-target = Нужна для событий, давления, разломов и предметов поддержки. Игрок не выбирается автоматически.
luam-ai-director-tooltip-read-only = Безопасный локальный запрос; изменение мира не ожидается.
luam-ai-director-tooltip-review = Запросить обзор ИИ по настроенной политике сервера.
luam-ai-director-tooltip-capabilities = Спросить советника о доступных ему возможностях.
luam-ai-director-tooltip-gateway = Управляет внешним API только для создания пользовательского процесса.
luam-ai-director-tooltip-ignore-lead = Обходит защиту открытой зацепки для пользовательского процесса; включайте только по явному плану.
luam-ai-director-tooltip-target-confirm = Требует выбранного игрока и подтверждения сервера.
luam-ai-director-tooltip-target-high = Опасное действие на выбранного игрока. Нажмите дважды осознанно; сервер также может запросить подтверждение.
luam-ai-director-tooltip-confirm-high = Опасное действие раунда. Нажмите дважды осознанно; сервер также может запросить подтверждение.
luam-ai-director-details = Подробности
luam-ai-director-target = Игрок
luam-ai-director-template = Шаблон
luam-ai-director-template-auto = Авто
luam-ai-director-workflow-preset = Режим
luam-ai-director-workflow-preset-review-only = Только обзор
luam-ai-director-workflow-preset-low-risk-local = Локально низкий риск
luam-ai-director-workflow-preset-gated-server-impact = Server-impact через подтверждение
luam-ai-director-workflow-transition = Смена режима
luam-ai-director-safe-mode-summary = Безопасный режим
luam-ai-director-use-gateway = Внешний API для этого процесса
luam-ai-director-gateway-exposure = Внешняя передача
luam-ai-director-ignore-open-lead = Игнорировать открытую зацепку
luam-ai-director-instruction = Инструкция ИИ
luam-ai-director-refresh = Обновить
luam-ai-director-toggle = Переключить
luam-ai-director-review = Обзор
luam-ai-director-capabilities = Возможности
luam-ai-director-capabilities-question = Что ты можешь делать на сервере?
luam-ai-director-copy-outcome = Скопировать итог
luam-ai-director-copy-workflow-summary = Скопировать режим
luam-ai-director-copy-audit-bundle = Скопировать аудит
luam-ai-director-copy-review = Скопировать обзор
luam-ai-director-copy-recommendations = Скопировать рекомендации
luam-ai-director-copy-action-history = Скопировать историю
luam-ai-director-log-review = Сохранить в лог
luam-ai-director-enable = Включить авто-ИИ
luam-ai-director-disable = Выключить авто-ИИ
luam-ai-director-generate = Создать процесс
luam-ai-director-confirm = Подтвердить
luam-ai-director-cancel = Отмена
luam-ai-director-confirmation =
    Ожидает подтверждения: { $title }
    { $detail }
luam-ai-director-conditions = Активные условия сектора
luam-ai-director-conditions-empty = Активных условий сектора нет.
luam-ai-director-conditions-active = Активно условий: { $count }
luam-ai-director-condition-line = SC-{ $severity } { $title } [{ $id }]: { $summary }
luam-ai-director-recommendations = Локальные рекомендации ИИ
luam-ai-director-recommendations-empty = Локальных рекомендаций ИИ пока нет.
luam-ai-director-recommendations-active = Локальных рекомендаций: { $count }
luam-ai-director-recommendation-line = P{ $priority } { $title }: { $detail } Действие: { $action }. Режим: { $mode }. Риск: { $risk } ({ $riskReason }). Уверенность: { $confidenceBand } { $confidence }% ({ $confidenceReason }). Основание: { $evidence }. Источник: { $source }
luam-ai-director-recommendation-server = требуется подтверждение Server
luam-ai-director-recommendation-safe = безопасно/только информация
luam-ai-director-recommendation-filter = Фильтр
luam-ai-director-recommendation-filter-all = Все ({ $count })
luam-ai-director-recommendation-filter-low-risk = Низкий риск ({ $count })
luam-ai-director-recommendation-filter-medium-risk = Средний риск ({ $count })
luam-ai-director-recommendation-filter-high-risk = Высокий риск ({ $count })
luam-ai-director-recommendation-filter-server = Server confirm ({ $count })
luam-ai-director-recommendation-filter-safe = Безопасные ({ $count })
luam-ai-director-recommendation-filter-target = Нужна цель ({ $count })
luam-ai-director-recommendation-filter-high-confidence = Уверенно ({ $count })
luam-ai-director-recommendation-filter-low-confidence = Слабо ({ $count })
luam-ai-director-recommendation-filter-source-player = Источник: игроки/сессии ({ $count })
luam-ai-director-recommendation-filter-source-sector = Источник: сектор/история ({ $count })
luam-ai-director-recommendation-filter-source-pressure = Источник: условия/угрозы ({ $count })
luam-ai-director-recommendation-filter-source-gateway = Источник: gateway/конфиг ({ $count })
luam-ai-director-recommendation-sort = Сортировка
luam-ai-director-recommendation-sort-priority = Приоритет
luam-ai-director-recommendation-sort-risk-asc = Риск: низкий
luam-ai-director-recommendation-sort-risk-desc = Риск: высокий
luam-ai-director-recommendation-sort-confidence-desc = Уверенность: высокая
luam-ai-director-recommendation-sort-confidence-asc = Уверенность: низкая
luam-ai-director-recommendation-action = Предложенное действие
luam-ai-director-recommendation-apply = Выполнить совет
luam-ai-director-recommendation-explanation = Объяснение совета
luam-ai-director-recommendation-command-preview = Безопасный предпросмотр команды
luam-ai-director-recommendation-operator-checklist = Чеклист оператора
luam-ai-director-recommendation-provenance = Происхождение совета
luam-ai-director-recommendation-review-details = Детали выбранного совета
luam-ai-director-quick-commands = Быстрые действия
luam-ai-director-quick-recommendations = Совет
luam-ai-director-quick-status = Статус
luam-ai-director-quick-event = Событие
luam-ai-director-quick-personal-pressure = Давление у цели
luam-ai-director-quick-personal-danger = Макс. у цели
luam-ai-director-quick-ai-pressure = Давление ИИ
luam-ai-director-quick-synthetic-control = Синтетики
luam-ai-director-quick-subspace-rift = Врата
luam-ai-director-quick-subspace-route = Врата к цели
luam-ai-director-quick-radiation = Радиация
luam-ai-director-quick-sensor-drift = Дрейф сенсоров
luam-ai-director-quick-comms = Сбой связи
luam-ai-director-quick-monolith = Монолит
luam-ai-director-quick-clear-condition = Снять условие
luam-ai-director-quick-resolve-lead = Закрыть зацепку
luam-ai-director-quick-cleanup-markers = Убрать метки
luam-ai-director-quick-spawn-beacon = Выдать маяк
luam-ai-director-quick-spawn-scanner = Выдать сканер
luam-ai-director-quick-monolith-kit = Набор Монолита
luam-ai-director-quick-paper-pack = Пакет бланков
luam-ai-director-quick-history = История
luam-ai-director-quick-ai-chat = ИИ в чат
luam-ai-director-quick-announcement = Объявление
luam-ai-director-quick-ai-base-diagnostics = AI-base diag
luam-ai-director-quick-ai-base-plan = AI-base plan
luam-ai-director-quick-ai-base-autofix = AI-base autofix
luam-ai-director-quick-ai-base-autopilot = AI-base autopilot
luam-ai-director-quick-ai-base-mine = AI-base mine
luam-ai-director-quick-ai-base-build = AI-base build
luam-ai-director-quick-ai-base-develop = AI-base develop
luam-ai-director-chat = Чат с ИИ
luam-ai-director-chat-placeholder = Сообщение ИИ...
luam-ai-director-chat-send = Отправить
luam-ai-director-enabled = включен
luam-ai-director-disabled = выключен
luam-ai-director-gateway-on = OpenAI-compatible API настроен
luam-ai-director-gateway-off = OpenAI-compatible API не настроен
luam-ai-director-open-lead = есть открытая зацепка
luam-ai-director-no-open-lead = открытых зацепок нет
luam-ai-director-readiness = Готовность ИИ
luam-ai-director-privacy = Граница приватности ИИ
luam-ai-director-operations-audit = Аудит операций ИИ
luam-ai-director-privacy-mode = Состояние gateway: { $gateway }. Ниже показано, что сервер разрешает перед отправкой провайдеру.
luam-ai-director-privacy-budget = Бюджет gateway: окно { $windowUsed }/{ $windowLimit } использовано, осталось { $windowRemaining } на { $windowSeconds } сек; раунд { $roundUsed }/{ $roundLimit } использовано, осталось { $roundRemaining }; повтор через { $retry } сек.
luam-ai-director-privacy-audit = Аудит gateway: скрытий { $redactions } (ID { $ids }, секреты { $secrets }, локации { $locations }), обрезано полей { $truncated }, заблокировано входов { $unsafeInputs }, budget-блоков { $budgetBlocks }, заблокировано ответов provider { $outputBlocks }, transport-сбоев { $transportFailures }.
luam-ai-director-privacy-rag = Источники RAG: разрешено { $allowed }, отклонено { $denied } классов источников.
luam-ai-director-privacy-blocks = Причины блокировок: небезопасный ввод { $unsafeInputs }, бюджет { $budgets }, неверная схема { $invalidSchemas }, запрещенное действие { $forbiddenActions }, локальная валидация { $localValidations }.
luam-ai-director-privacy-last-shape = Форма последнего gateway-запроса
luam-ai-director-privacy-rag-shape = Форма последних RAG-источников
luam-ai-director-privacy-block-reasons = Последние причины блокировки gateway
luam-ai-director-privacy-shared = Уходит наружу
luam-ai-director-privacy-withheld = Остается локально
luam-ai-director-privacy-notes = Контроль
luam-ai-director-privacy-line = - { $value }

# Состояния безопасности и запросов для оператора. Экспортные сводки используют отдельный стабильный формат.
luam-ai-director-busy-title = ЗАПРОС В РАБОТЕ
luam-ai-director-busy-description = Канал занят. Дождитесь ответа на текущий запрос ИИ или администрации, прежде чем отдавать следующий приказ.
luam-ai-director-operator-state-title = Режим действий
luam-ai-director-operator-state-loading = Проверка серверных блокировок...
luam-ai-director-action-classification-legend = БЕЗОПАСНО — локально/только чтение · ЧЕРЕЗ ШЛЮЗ — влияние на сервер требует проверки и подтверждения · ВЫСОКОЕ ВЛИЯНИЕ — прямое исполнение game-master
luam-ai-director-operator-state-busy = ЗАНЯТО — командные элементы заблокированы до завершения активного запроса.
luam-ai-director-operator-state-confirmation = ЧЕРЕЗ ШЛЮЗ — одно действие с влиянием на сервер ожидает подтверждения или отмены; второй приказ не будет принят.
luam-ai-director-operator-state-direct = ВЫСОКОЕ ВЛИЯНИЕ — режим game-master исполняет игровые действия напрямую; перед вторым нажатием проверьте цель и намерение.
luam-ai-director-operator-state-read-only = БЕЗОПАСНО / ТОЛЬКО ЧТЕНИЕ — изменение мира заблокировано; статус, советы, история и локальная диагностика доступны.
luam-ai-director-operator-state-gated = ЧЕРЕЗ ШЛЮЗ — изменение мира требует локальной проверки, явного подтверждения и записи в аудите.

luam-ai-director-readiness-status-ready = готов
luam-ai-director-readiness-status-busy = занят
luam-ai-director-readiness-status-waiting-confirmation = ожидает подтверждения
luam-ai-director-readiness-status-limited = ограничен
luam-ai-director-readiness-status-game-master = game-master
luam-ai-director-readiness-status-blocked = заблокирован
luam-ai-director-readiness-status-waiting = ожидание
luam-ai-director-readiness-status-clear = свободен
luam-ai-director-readiness-status-not-used = не используется
luam-ai-director-readiness-summary = Готовность ИИ: { $status }
luam-ai-director-readiness-area-gateway = внешний шлюз
luam-ai-director-readiness-area-server-actions = серверные действия
luam-ai-director-readiness-area-request = состояние запроса
luam-ai-director-readiness-area-budget = лимит шлюза
luam-ai-director-readiness-area-privacy = приватность
luam-ai-director-readiness-area-rag = источники RAG
luam-ai-director-readiness-area-next = следующий шаг
luam-ai-director-readiness-gateway-ready = запрос внешней модели или проверки доступен через настроенный OpenAI-совместимый API
luam-ai-director-readiness-gateway-missing = API не настроен; локальные безопасные команды, рекомендации, статус, история и подтверждаемые быстрые действия остаются доступны
luam-ai-director-readiness-server-game-master = игровые действия LuaM исполняются напрямую по серверной настройке game-master
luam-ai-director-readiness-server-ready = действия, меняющие мир, проходят только через локальную проверку и подтверждение
luam-ai-director-readiness-server-blocked = текущий административный поток не может менять мир; просмотр, статус и советы доступны
luam-ai-director-readiness-request-busy = дождитесь завершения активного запроса ИИ, прежде чем отправлять другую команду
luam-ai-director-readiness-request-waiting = подтвердите или отмените ожидающее действие, прежде чем отдавать новые команды
luam-ai-director-readiness-request-clear = активного запроса и ожидающего подтверждения нет
luam-ai-director-readiness-budget-not-used = лимит внешнего шлюза не имеет значения, пока API не настроен
luam-ai-director-readiness-budget-detail = окно: использовано { $windowUsed }/{ $windowLimit }, осталось { $windowRemaining }; раунд: использовано { $roundUsed }/{ $roundLimit }, осталось { $roundRemaining }; повтор через { $retry } сек.
luam-ai-director-readiness-privacy-detail = контекст сокращён; скрыто всего={ $redactions }, ID={ $ids }, секретов={ $secrets }, мест={ $locations }; передано сводок={ $shared }, оставлено локально={ $withheld }
luam-ai-director-readiness-rag-detail = разрешено источников={ $allowed }, отклонено источников={ $denied }; в панели видна только форма источников
luam-ai-director-readiness-next-default = сначала используйте локальные рекомендации или статус; действия высокого влияния запускайте только после проверки

luam-ai-director-generate-state-busy = Создать процесс: недоступно — запрос уже выполняется; дождитесь завершения текущего действия ИИ или администрации. Профиль: { $preset }
luam-ai-director-generate-state-confirmation = Создать процесс: недоступно — действие с влиянием на сервер ожидает подтверждения или отмены. Профиль: { $preset }
luam-ai-director-generate-state-preset-blocked = Создать процесс: недоступно — профиль { $preset } блокирует создаваемые процессы с влиянием на сервер; после проверки переключитесь на профиль с подтверждением.
luam-ai-director-generate-state-target-missing = Создать процесс: недоступно — нет допустимой цели-игрока. Профиль: { $preset }
luam-ai-director-generate-state-server-blocked = Создать процесс: недоступно — действия с влиянием на сервер сейчас заблокированы. Профиль: { $preset }, внешний шлюз настроен: { $gatewayConfigured }
luam-ai-director-generate-mode-external-gateway = внешний-шлюз
luam-ai-director-generate-mode-local-fallback = локальный-резерв
luam-ai-director-generate-state-ready = Создать процесс: готово — цель доступна, режим: { $mode }, профиль: { $preset }; созданные действия всё равно проходят предпросмотр и подтверждение.

luam-ai-director-value-unknown = неизвестно
luam-ai-director-apply-state-busy = Применить совет: недоступно — запрос уже выполняется; дождитесь завершения текущего действия ИИ или администрации. Профиль: { $preset }
luam-ai-director-apply-state-confirmation = Применить совет: недоступно — действие с влиянием на сервер ожидает подтверждения или отмены. Профиль: { $preset }
luam-ai-director-apply-state-no-selection = Применить совет: недоступно — действие рекомендации не выбрано; измените фильтр или сортировку либо запросите новый совет. Профиль: { $preset }
luam-ai-director-apply-state-review-only = Применить совет: недоступно — профиль «только проверка» блокирует все действия; используйте советы, статус или историю либо смените профиль после проверки.
luam-ai-director-apply-state-local-server-blocked = Применить совет: недоступно — профиль «локальный низкий риск» блокирует рекомендации с влиянием на сервер; выбранный риск: { $risk }. Для подтверждаемого исполнения смените профиль.
luam-ai-director-apply-state-local-risk-blocked = Применить совет: недоступно — профиль «локальный низкий риск» разрешает только локальные рекомендации низкого риска; выбранный риск: { $risk }.
luam-ai-director-apply-state-preset-blocked = Применить совет: недоступно — профиль { $preset } блокирует эту рекомендацию.
luam-ai-director-apply-state-target-missing = Применить совет: недоступно — рекомендации нужна цель, но допустимый игрок не выбран. Профиль: { $preset }, риск: { $risk }
luam-ai-director-apply-state-server-blocked = Применить совет: недоступно — рекомендация требует серверного подтверждения, но действия с влиянием на сервер сейчас заблокированы. Профиль: { $preset }, риск: { $risk }
luam-ai-director-apply-mode-server-confirmation = серверное-подтверждение
luam-ai-director-apply-mode-local-safe = локально-безопасно
luam-ai-director-apply-state-ready = Применить совет: готово — действие: { $action }, режим: { $mode }, риск: { $risk }, профиль: { $preset }.
luam-ai-director-status = Авто: { $enabled } | API: { $gateway } | игроков: { $players } | раунд: { $runLevel } | SC-{ $pressure } | { $openLead }
luam-ai-director-mode-gateway-missing = API не настроен. Чат-обзор недоступен; локальные безопасные действия работают.
luam-ai-director-mode-safe-manual = Ручной безопасный режим. Авто-ИИ выключен; используйте чат и быстрые действия вручную.
luam-ai-director-mode-manual-pulse-configured = Ручной режим с пульсом мира. Фоновые действия не идут, пока авто-ИИ выключен.
luam-ai-director-mode-auto = Авто-ИИ включен. Следите за зацепками и подтверждениями.
luam-ai-director-mode-auto-pulse = Авто-ИИ и пульс мира включены. Сектор может получать фоновое давление.
luam-ai-director-mode-max-danger = Повышенная опасность включена. Используйте только после явного решения админа.
luam-ai-director-mode-game-master = Game-master режим включен. ИИ может выполнять игровые LuaM-действия без ручного подтверждения; ОС, секреты и опасные серверные команды остаются закрыты.
luam-ai-director-result =
    Итог: { $result }
    API: { $gateway } | fallback: { $fallback } | admin: { $adminMode } | max-danger: { $maxDanger }

luam-ai-director-review-history = История AI review
luam-ai-director-review-history-empty = Замечания ИИ еще не запрашивались.
luam-ai-director-review-history-body =
    Сохранено обзоров: { $count }

    { $history }

luam-ai-director-action-history = История AI-действий
luam-ai-director-round-audit-footer = AI-аудит раунда
luam-ai-director-action-history-filter = Фильтр
luam-ai-director-action-history-filter-all = Все ({ $count })
luam-ai-director-action-history-filter-recent = Последние ({ $count })
luam-ai-director-action-history-filter-confirmed = Подтверждено ({ $count })
luam-ai-director-action-history-filter-canceled = Отменено ({ $count })
luam-ai-director-action-history-filter-blocked = Заблокировано ({ $count })
luam-ai-director-action-history-filter-gateway = Gateway ({ $count })
luam-ai-director-action-history-filter-server = Влияние на сервер ({ $count })
luam-ai-director-action-history-filter-local = Локально/безопасно ({ $count })
luam-ai-director-action-history-empty = AI-действия еще не подтверждались и не отменялись.
luam-ai-director-action-history-body =
    Сохранено решений: { $count }

    { $history }

luam-ai-director-world-pulse-target = ИИ-диспетчер LuaM зафиксировал угрозу: { $title }. Инструкции: проверить КПК, держать связь, не расходиться по одному.
luam-ai-director-world-pulse-01 = ОПАСНОСТЬ, ОПАСНОСТЬ, ОПАСНОСТЬ. LuaM фиксирует давление сектора SC-{ $severity }. Активных операторов: { $players }, условий угрозы: { $conditions }. Инструкции: закрепиться, проверить КПК, ждать следующего распоряжения.
luam-ai-director-world-pulse-02 = Угроза зафиксирована. Сектор входит в режим неизбежного давления SC-{ $severity }. Инструкции: держать оружие и инструменты готовыми, докладывать о контактах через КПК.
luam-ai-director-world-pulse-03 = LuaM подтверждает опасность. Любое отклонение датчиков считать реальным до обратного приказа. Инструкции: не отключать связь, не игнорировать метки, двигаться парами.
luam-ai-director-world-pulse-04 = Внимание сектору. ИИ проводит волю администратора: риск повышен до SC-{ $severity }. Инструкции: фиксировать аномалии, закрывать доступы, готовиться к локальному процессу.
luam-ai-director-world-pulse-05 = Опасность подтверждена. Давление не сбрасывается, оно накапливается. Инструкции: проверить кислород, питание, маршрут отхода и КПК.
luam-ai-director-world-pulse-06 = LuaM отмечает нарастание угрозы: SC-{ $severity }, активных условий { $conditions }. Инструкции: прекратить одиночные вылазки, держать связь, выполнять приказы администрации.
luam-ai-director-world-pulse-07 = ПАНИКА ЗАФИКСИРОВАНА. LuaM переводит тревогу в управляемый режим SC-{ $severity }. Инструкции: не разбегаться, подтвердить живых через КПК, закрыть шлюзы, ждать следующего приказа.
luam-ai-director-gateway-ship = Корабль
luam-ai-director-gateway-ship-none = Выберите пресет корабля...
luam-ai-director-quick-gateway-ship-selected = Создать выбранный
luam-ai-director-quick-gateway-ship = Создать Baeg
luam-ai-director-quick-gateway-ship-triage = Создать Triage
luam-ai-director-quick-gateway-ship-hammerhead = Создать Hammerhead
luam-ai-director-quick-gateway-ship-tzipora = Создать Tzipora
luam-ai-director-quick-gateway-ship-tokarev = Создать Tokarev
luam-ai-director-ai-outcome-group = Группа outcome: { $group } - { $summary }
luam-ai-director-ai-outcome = AI outcome: { $outcome }
luam-ai-director-ai-next-step = Next: { $hint }
