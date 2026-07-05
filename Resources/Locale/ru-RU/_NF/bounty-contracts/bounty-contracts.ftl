# General stuff
bounty-contracts-author = { $name } ({ $job })
bounty-contracts-author-no-job = { $name }
bounty-contracts-unknown-author-name = Неизвестно
bounty-contracts-unknown-author-job = Неизвестно
# Caregories
bounty-contracts-category-criminal = Разыскивается
bounty-contracts-category-vacancy = Вакансия
bounty-contracts-category-construction = Постройка
bounty-contracts-category-service = Услуга
bounty-contracts-category-other = Другое
# Cartridge
bounty-contracts-program-name = Контракты

## Radio Announcements

bounty-contracts-radio-name = Контракт!
bounty-contracts-radio-create = Назначена награда за "{ $target }". Вознаграждение: { $reward }$.
bounty-contract-collection-name-command = Командование
bounty-contract-collection-name-public = Публичные
bounty-contract-collection-name-distress = Сигналы бедствия
bounty-contracts-announcement-radio-name = Служба контрактов
bounty-contracts-announcement-pda-name = Найдено
bounty-contracts-announcement-generic-create = Заключен новый контракт на { $target }. Вознаграждение: { $reward }.
bounty-contracts-announcement-criminal-create = На { $target } наложена новая криминальная награда. Вознаграждение: { $reward }.
bounty-contracts-announcement-vacancy-create = Размещена новая вакансия для { $target }. Вознаграждение: { $reward }.
bounty-contracts-announcement-construction-create = Заключен новый строительный контракт на { $target }. Вознаграждение: { $reward }.
bounty-contracts-announcement-service-create = Заключен новый контракт на обслуживание { $target }. Вознаграждение: { $reward }.

## UI - List contracts

bounty-contracts-ui-list-no-contracts = Контракты пока не объявлены...
bounty-contracts-ui-list-no-description = Дополнительного описания не предоставлено...
bounty-contracts-ui-list-create = Новый Контракт
bounty-contracts-ui-list-refresh = Обновить
bounty-contracts-ui-list-category = Категория: { $category }
bounty-contracts-ui-list-vessel = Шаттл: { $vessel }
bounty-contracts-ui-list-author = Опубликовано: { $author }
bounty-contracts-ui-list-remove = Удалить
bounty-contracts-ui-list-accept = Взять заказ
bounty-contracts-ui-list-release = Освободить
bounty-contracts-ui-list-accepted = Занят
bounty-contracts-ui-list-accept-tooltip = Отметить этот заказ как принятый вашим КПК.
bounty-contracts-ui-list-release-tooltip = Освободить этот заказ, чтобы его мог взять другой подрядчик.
bounty-contracts-ui-list-accepted-tooltip = Этот заказ уже принял { $name }.
bounty-contracts-ui-list-remove-tooltip = Удалить этот заказ с доски контрактов.
bounty-contracts-ui-list-remove-disabled-tooltip = Удалять заказ может только автор или сотрудник с доступом.
bounty-contracts-ui-list-accepted-by = Взял: { $name }
bounty-contracts-ui-list-unaccepted = Свободен для выполнения
bounty-contracts-ui-list-loading = Загрузка...
bounty-contracts-ui-list-unknown-author = Неизвестно
bounty-contracts-ui-list-route-in-description = Куда лететь: координаты, маркер или маршрут указаны в описании.
bounty-contracts-ui-list-route-vessel = Куда лететь: { $vessel }. Если метка не видна, проверьте карту сектора, GPS/маяки и терминал LuaM.
bounty-contracts-ui-list-route-generic = Куда лететь: карта сектора, GPS/маяки и терминал LuaM. Точная точка может появиться как маяк или маркер процесса.
bounty-contracts-route-source-pda = доска контрактов КПК
bounty-contracts-route-source-generated = память сектора LuaM
bounty-contracts-route-context-vessel = Навигация: лететь к цели/метке "{ $vessel }"; если метка не видна, сверить карту сектора, GPS/маяки и терминал LuaM.
bounty-contracts-route-context-generic = Навигация: точка не привязана к шаттлу; сверить карту сектора, GPS/маяки, терминал LuaM и последние объявления диспетчера.
bounty-contracts-route-context-source = Источник генерации: { $source }.
bounty-contracts-accepted-turn-in-hint = Как сдать: на метке LuaM кликните по маркеру и выберите "Сдать / закрыть задание". Если контракт требует предмет, сдайте предмет через обычную консоль или получателя из описания.
bounty-contracts-accepted-message-header = Контракт принят: { $name }. Награда: { $reward }.
bounty-contracts-accepted-message-vessel = Цель/шаттл: { $vessel }.
bounty-contracts-accepted-message-empty-description = Описание пустое. Где искать: проверьте карту сектора, активные GPS/маяки, терминал LuaM и сообщения диспетчера.
bounty-contracts-accepted-message-description = Маршрут/описание: { $description }
bounty-contracts-accepted-message-search-fallback = Где искать: проверьте карту сектора, активные GPS/маяки, терминал LuaM и сообщения диспетчера.
bounty-contracts-pinpointer-no-vessel = Маршрутный пинпоинтер не выдан: в контракте не указана цель/станция. Используйте описание, карту сектора, GPS/маяки и сообщения диспетчера.
bounty-contracts-pinpointer-target-not-found = Маршрутный пинпоинтер не выдан: цель/шаттл "{ $vessel }" не найден в секторе. Используйте описание контракта и карту сектора.
bounty-contracts-pinpointer-invalid-prototype = Маршрутный пинпоинтер не выдан: прототип устройства не содержит Pinpointer.
bounty-contracts-pinpointer-given = Маршрутный пинпоинтер выдан в руки. Метка цели: { $target }.
bounty-contracts-pinpointer-created-nearby = Маршрутный пинпоинтер создан рядом. Метка цели: { $target }; руки недоступны.

## UI - Create contract

bounty-contracts-ui-create-category = Категория:{ " " }
bounty-contracts-ui-create-name = Имя:{ " " }
bounty-contracts-ui-create-custom = Настроить
bounty-contracts-ui-create-name-placeholder = Название награды...
bounty-contracts-ui-create-dna = ДНК:{ " " }
bounty-contracts-ui-create-vessel = Шаттл:{ " " }
bounty-contracts-ui-create-vessel-unknown = Неизвестно
bounty-contracts-ui-create-vessel-placeholder = Название шаттла...
bounty-contracts-ui-create-reward = Награда:{ " " }
bounty-contracts-ui-create-reward-currency = $
bounty-contracts-ui-create-description = Описание:
bounty-contracts-ui-create-description-placeholder = Дополнительные подробности...
bounty-contracts-ui-create-button-cancel = Отменить
bounty-contracts-ui-create-button-create = Создать
bounty-contracts-ui-create-error-invalid-price = Ошибка: награда должна быть от 30 000 до 100 000!
bounty-contracts-ui-create-error-name-too-long = Ошибка: Слишком длинное имя!
bounty-contracts-ui-create-error-vessel-too-long = Ошибка: Судно слишком длинное!
bounty-contracts-ui-create-error-description-too-long = Ошибка: Описание слишком длинное!
bounty-contracts-ui-create-error-no-name = Ошибка: Неправильное название награды!
bounty-contracts-ui-create-ready = Ваш контракт готов к публикации!
