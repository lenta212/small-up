### UI

# For the PDA screen
comp-pda-ui = ID: [color=white]{ $owner }[/color], [color=yellow]{ CAPITALIZE($jobTitle) }[/color]
comp-pda-ui-blank = ID:
comp-pda-ui-owner = Владелец: [color=white]{ $actualOwnerName }[/color]
comp-pda-ui-owner-with-company = Владелец: [color=white]{ $actualOwnerName }[/color] [color={ $companyColor }]({ $companyName })[/color]
comp-pda-ui-device-status = ЗАЩИЩЁННЫЙ КАНАЛ // В СЕТИ
comp-pda-ui-home-tooltip = Главный экран
comp-pda-ui-close-program-tooltip = Закрыть активную программу
comp-pda-ui-eject-pai-button = Извлечь пИИ
comp-pda-ui-dashboard-title = Главный экран
comp-pda-ui-dashboard-description = Личность и состояние сектора — одним взглядом. Деньги и снабжение вынесены в защищённые «Службы».
comp-pda-ui-services-title = Службы Фронтира
comp-pda-ui-services-description = Деньги и снабжение проходят через защищённый канал сектора. Проверяйте получателя и сумму: в открытом космосе ошибочный перевод не вернуть.
comp-pda-ui-section-identity = ЛИЧНОСТЬ
comp-pda-ui-section-sector = СОСТОЯНИЕ СЕКТОРА
comp-pda-ui-section-finance = ФИНАНСЫ
comp-pda-ui-section-vessel = ЗАРЕГИСТРИРОВАННОЕ СУДНО
comp-pda-ui-copy-tooltip = Скопировать значение
comp-pda-ui-bank-copy-tooltip = Скопировать банковский ID
comp-pda-ui-copied = Скопировано: { $label }
comp-pda-ui-copy-owner = владелец
comp-pda-ui-copy-id = запись удостоверения
comp-pda-ui-copy-sector = сектор
comp-pda-ui-copy-alert = уровень тревоги
comp-pda-ui-copy-advisory = рекомендации сектора
comp-pda-ui-copy-time = время смены
comp-pda-ui-copy-balance = баланс
comp-pda-ui-copy-bank-id = банковский ID
comp-pda-ui-copy-support = счёт поддержки
comp-pda-ui-copy-vessel = зарегистрированное судно
comp-pda-ui-programs-title = Программы
comp-pda-ui-programs-description = Установленные и доступные картриджи КПК.
comp-pda-ui-settings-title = Настройки устройства
comp-pda-ui-settings-description = Звук, инструменты и защищённые службы.
comp-pda-ui-os-brand = LuaM POCKETLINK // ЗАЩИЩЕНО
comp-pda-io-program-list-button = Программы
comp-pda-io-services-button = Службы
comp-pda-io-settings-button = Настройки
comp-pda-io-program-fallback-title = Программа
comp-pda-io-no-programs-available = Нет доступных программ
pda-bound-user-interface-show-uplink-title = Открыть аплинк
pda-bound-user-interface-show-uplink-description = Получите доступ к своему аплинку
pda-bound-user-interface-lock-uplink-title = Закрыть аплинк
pda-bound-user-interface-lock-uplink-description = Предотвратите доступ к вашему аплинку персон без кода
comp-pda-ui-menu-title = КПК
comp-pda-ui-footer = Карманный Персональный Компьютер
comp-pda-ui-station = Сектор: [color=white]{ $station }[/color]
comp-pda-ui-station-alert-level = Тревога сектора: [color={ $color }]{ $level }[/color]
comp-pda-ui-station-alert-level-instructions = Рекомендации: [color=white]{ $instructions }[/color]
comp-pda-ui-station-time = Продолжительность смены: [color=white]{ $time }[/color]
comp-pda-ui-eject-id-button = Извлечь ID
comp-pda-ui-eject-pen-button = Извлечь ручку
comp-pda-ui-ringtone-button-description = Измените рингтон вашего КПК
comp-pda-ui-ringtone-button = Рингтон
comp-pda-ui-toggle-flashlight-button = Переключить фонарик
pda-bound-user-interface-music-button-description = Слушайте музыку на своём КПК
pda-bound-user-interface-music-button = Музыкальный инструмент
comp-pda-ui-unknown = Неизвестно
comp-pda-ui-unassigned = Не назначено
pda-notification-message = [font size=12][bold]КПК[/bold] { $header }: [/font]
    "{ $message }"
