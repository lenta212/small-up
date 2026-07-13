comp-pda-ui-balance = Баланс: [color=white]{ $balance }[/color]
comp-pda-ui-balance-payroll =
    Баланс: [color=white]{ $balance }[/color]
    Зарплата: [color=white]{ $hourly }/час[/color], следующая выплата через [color=white]{ $minutes } мин[/color].
comp-pda-ui-shuttle-deed = Зарегистрированный Шаттл: [color=white]{ $shipname }[/color]
comp-pda-ui-bank-id = Банковский ID: [color=white]{ $id }[/color]
comp-pda-ui-bank-id-copied = [color=green]Банковский ID { $id } скопирован.[/color]
comp-pda-ui-bank-transfer-recipient-placeholder = ID получателя
comp-pda-ui-bank-transfer-amount-placeholder = Сумма
comp-pda-ui-bank-transfer-send = Перевести
comp-pda-ui-bank-transfer-confirm = Подтвердить перевод
comp-pda-ui-bank-transfer-cancel = Отмена
comp-pda-ui-bank-transfer-acknowledge = Подтвердить результат
comp-pda-ui-bank-transfer-confirmation = [color=yellow]Подтвердите перевод[/color]\nПолучатель: [bold]{ $recipient }[/bold]\nБанковский ID: [bold]{ $id }[/bold]\nСумма: [bold]{ $amount }[/bold]\nОперация: { $operation }
comp-pda-ui-bank-transfer-recovered = [color=yellow]Восстановлен выполненный перевод[/color]\nПолучатель: [bold]{ $recipient }[/bold] [{ $id }]\nСумма: [bold]{ $amount }[/bold]\nБаланс после перевода: { $balance }\nОперация: { $operation }\nПодтвердите этот сохранённый результат перед созданием нового перевода.
comp-pda-ui-bank-transfer-confirmation-ready = Проверьте имя получателя, банковский ID и сумму, затем подтвердите перевод.
comp-pda-ui-bank-transfer-confirmation-expired = [color=orange]Время подтверждения истекло. Сформируйте перевод заново.[/color]
comp-pda-ui-bank-transfer-cancelled = Перевод отменён до перемещения денег.
comp-pda-ui-bank-transfer-acknowledged = Сохранённый результат перевода подтверждён.
comp-pda-ui-bank-transfer-reconcile-required = [color=orange]Идёт сверка банковского журнала. Новые переводы временно отключены.[/color]
comp-pda-ui-bank-transfer-missing-recipient = [color=orange]Введите банковский ID получателя.[/color]
comp-pda-ui-bank-transfer-invalid-amount = [color=orange]Введите положительную целую сумму.[/color]
comp-pda-ui-bank-transfer-no-sender = [color=red]Перевод не выполнен: пользователь КПК не найден.[/color]
comp-pda-ui-bank-transfer-recipient-not-found = [color=red]Перевод не выполнен: банковский ID { $id } не найден. Попросите получателя один раз открыть КПК, чтобы ID зарегистрировался, затем скопируйте ID заново.[/color]
comp-pda-ui-bank-transfer-same-account = [color=orange]Нельзя перевести деньги на свой же счет.[/color]
comp-pda-ui-bank-transfer-insufficient-funds = [color=red]Перевод не выполнен: недостаточно средств.[/color]
comp-pda-ui-bank-transfer-blocked = [color=red]Перевод заблокирован для этого счета.[/color]
comp-pda-ui-bank-transfer-recipient-overflow = [color=red]Перевод не выполнен: счет получателя не может принять эту сумму.[/color]
comp-pda-ui-bank-transfer-pending = [color=orange]Банковский перевод уже обрабатывается. Подождите.[/color]
comp-pda-ui-bank-transfer-conflict = [color=orange]Не удалось подтвердить перевод. Проверьте баланс перед повторной попыткой.[/color]
comp-pda-ui-bank-transfer-outcome-unknown = [color=red]Результат перевода не удалось проверить. Повтор заблокирован до перезапуска сервера; проверьте баланс или обратитесь к администратору.[/color]
comp-pda-ui-bank-transfer-internal-error = [color=red]Не удалось подтвердить статус перевода. Проверьте баланс перед повторной попыткой.[/color]
comp-pda-ui-bank-transfer-failed = [color=red]Перевод не выполнен ({ $reason }).[/color]
comp-pda-ui-bank-transfer-success = [color=green]Отправлено { $amount } игроку { $recipient } [{ $id }]. Баланс: { $balance }. Операция: { $operation }.[/color]
comp-pda-ui-bank-transfer-received = Получен банковский перевод: { $amount } от { $sender }.
comp-pda-ui-donation-shop-title = Магазин поддержки LuaM
comp-pda-ui-donation-shop-access = Магазин поддержки: [color=white]{ $balance } { $currency }[/color]. Доступ до: [color=white]{ $until }[/color].
comp-pda-ui-donation-shop-locked = Магазин поддержки закрыт. Баланс: [color=white]{ $balance } { $currency }[/color]. Нужен ручной доступ администратора: 1 единица = 1 месяц.
comp-pda-ui-donation-shop-no-access-until = нет активного доступа
comp-pda-ui-donation-shop-copy = Донат-баланс: { $balance } { $currency }; доступ до: { $until }
comp-pda-ui-donation-shop-entry = [bold]{ $name }[/bold]: { $description } Цена: { $price } { $currency }.
comp-pda-ui-donation-shop-buy = Купить
comp-pda-ui-donation-shop-owned = Куплено
comp-pda-ui-donation-shop-insufficient = Нужно { $price } { $currency }
comp-pda-ui-donation-shop-locked-button = Закрыто
comp-pda-ui-donation-shop-status-no-user = [color=red]Пользователь магазина не найден.[/color]
comp-pda-ui-donation-shop-status-unknown-item = [color=red]Позиция магазина не найдена.[/color]
comp-pda-ui-donation-shop-status-locked = [color=orange]Доступ к магазину не активен. Администратор должен выдать единицы доступа; 1 единица = 1 месяц.[/color]
comp-pda-ui-donation-shop-status-owned = [color=orange]Эта награда уже куплена.[/color]
comp-pda-ui-donation-shop-status-insufficient = [color=red]Недостаточно LC: { $balance }/{ $price } { $currency }.[/color]
comp-pda-ui-donation-shop-status-purchased = [color=green]Куплено: { $item }. Баланс: { $balance } { $currency }.[/color]
comp-pda-ui-donation-shop-certificate-nearby = Сертификат напечатан рядом.
comp-pda-ui-donation-shop-certificate-content = Сертификат поддержки сектора LuaM. Игрок: { $player }. Аккаунт: { $user }. Награда: { $item }. Выдано: { $date }. Сертификат является косметическим и не дает оперативного преимущества.
comp-pda-ui-donation-shop-announcement = Сигнал поддержки LuaM зарегистрирован для { $player }. Память сектора отмечает вклад; оперативное преимущество не назначено.
comp-pda-ui-donation-shop-item-supporter-badge = Знак поддержки
comp-pda-ui-donation-shop-item-supporter-badge-desc = Постоянная косметическая отметка поддержки в записях LuaM.
comp-pda-ui-donation-shop-item-pda-gold-frame = Золотая рамка КПК
comp-pda-ui-donation-shop-item-pda-gold-frame-desc = Косметическое право на визуальную рамку КПК для последующего внедрения.
comp-pda-ui-donation-shop-item-sector-certificate = Сертификат сектора
comp-pda-ui-donation-shop-item-sector-certificate-desc = Печатает бумажный сертификат поддержки в руки.
comp-pda-ui-donation-shop-item-luam-announcement = Объявление LuaM
comp-pda-ui-donation-shop-item-luam-announcement-desc = Отправляет нейтральное объявление поддержки сектора без игрового преимущества.
