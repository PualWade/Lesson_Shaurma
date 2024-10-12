using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    private static readonly string botToken = "7718382132:AAGDhWoo91daut7A98RcS5S9-YmCc_pIeZc"; // Замените на ваш токен бота
    private static ITelegramBotClient botClient;

    // ID чата сотрудника
    private static long workerChatId = 406865885; // Замените на ID чата сотрудника

    // Путь к файлу SQLite базы данных
    private static readonly string dbFile = "shawarma_orders.db";

    // Каталог шаурмы
    private static List<(string PhotoUrl, string Name, string Description, string MeatSticker, string FeatureSticker, int Weight, int Price)> shawarmaCatalog = new List<(string, string, string, string, string, int, int)>
    {
        ("https://avatars.mds.yandex.net/i?id=3e75994bd52ee131b38f5de3b9ea5baa64b41e7f-9863327-images-thumbs&n=13",
        "Куриная классическая",
        "Состав: куриное филе, свежие овощи, лаваш, фирменный соус. Идеальная комбинация для тех, кто любит классику!",
        "🍗", "🥙", 300, 250),

        ("https://bonapizza.ru/wp-content/uploads/2023/07/1662355860_24-kartinkof-club-p-novie-i-krasivie-kartinki-shaurma-25.jpg",
        "Куриная с картошкой фри",
        "Состав: куриное филе, картофель фри, овощи и пикантный соус. Для любителей сытных и вкусных закусок!",
        "🍗", "🍟", 350, 300),

        ("https://www.slivki.by/znijki-media/w1180_728/default/1009921/1647334491_.jpg",
        "Куриная сырная",
        "Состав: куриное филе, расплавленный сыр, овощи, соус. Вкусная шаурма с нежным сырным вкусом.",
        "🍗", "🧀", 400, 350),

        // Говяжьи виды шаурмы
        ("https://sarpike.ru/wp-content/uploads/2022/02/shayrma_nch_72490022_154151102658559_3930624980547592045_n-1.jpg",
        "Говяжья классическая",
        "Состав: сочная говядина, свежие овощи, лаваш, фирменный соус. Идеальная для любителей мяса!",
        "🥩", "🥙", 300, 350),

        ("https://avatars.mds.yandex.net/get-altay/2812564/2a00000174112cabfdf02ab43f984738f379/XXL",
        "Говяжья с картошкой фри",
        "Состав: говядина, картофель фри, овощи, соус. Максимальная порция энергии для дня!",
        "🥩", "🍟", 350, 400),

        ("https://avatars.mds.yandex.net/get-altay/4699294/2a0000018d5ae1ac478374d2b1797fb76a2b/XXL_height",
        "Говяжья сырная",
        "Состав: говядина, расплавленный сыр, овощи, соус. Невероятная смесь вкусов для истинных гурманов.",
        "🥩", "🧀", 400, 450),
    };

    private static int currentShawarmaIndex = 0;
    private static string selectedDeliveryTime = "";

    // Словарь для хранения корзин пользователей
    private static Dictionary<long, Dictionary<int, int>> userCarts = new Dictionary<long, Dictionary<int, int>>();

    // Хранение пользователей, ожидающих ввода пользовательского времени
    private static HashSet<long> usersAwaitingCustomTime = new HashSet<long>();

    // Хранение пользователей, ожидающих ввода нового времени для изменения заказа
    private static Dictionary<long, int> usersAwaitingTimeChange = new Dictionary<long, int>();

    static async Task Main(string[] args)
    {
        botClient = new TelegramBotClient(botToken);

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"Бот {me.FirstName} запущен.");

        // Создаем или проверяем базу данных
        SetupDatabase();

        var cancellationToken = new CancellationTokenSource().Token;
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>() // Получаем все типы обновлений
        };

        botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken);

        Console.ReadLine();
    }

    // Метод для настройки базы данных и таблицы
    private static void SetupDatabase()
    {
        if (!System.IO.File.Exists(dbFile))
        {
            SQLiteConnection.CreateFile(dbFile);
            Console.WriteLine("Создан файл базы данных.");
        }

        using (var connection = new SQLiteConnection($"Data Source={dbFile};Version=3;"))
        {
            connection.Open();

            // Создаем таблицу, если она не существует
            string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER,
                    UserName TEXT,
                    Product TEXT,
                    DeliveryTime TEXT,
                    OrderTime TEXT
                )";
            using (var command = new SQLiteCommand(createTableQuery, connection))
            {
                command.ExecuteNonQuery();
            }

            // Проверяем, существует ли столбец Status
            string checkColumnQuery = "PRAGMA table_info(Orders)";
            using (var command = new SQLiteCommand(checkColumnQuery, connection))
            {
                using (var reader = command.ExecuteReader())
                {
                    bool statusColumnExists = false;
                    while (reader.Read())
                    {
                        if (reader["name"].ToString() == "Status")
                        {
                            statusColumnExists = true;
                            break;
                        }
                    }

                    // Если столбец Status не существует, добавляем его
                    if (!statusColumnExists)
                    {
                        string addColumnQuery = "ALTER TABLE Orders ADD COLUMN Status TEXT DEFAULT 'Pending'";
                        using (var addColumnCommand = new SQLiteCommand(addColumnQuery, connection))
                        {
                            addColumnCommand.ExecuteNonQuery();
                            Console.WriteLine("Добавлен столбец Status в таблицу Orders.");
                        }
                    }
                }
            }

            connection.Close();
        }
    }

    // Основной метод обработки обновлений
    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Type == UpdateType.Message && update.Message!.Type == MessageType.Text)
        {
            var message = update.Message;

            if (usersAwaitingCustomTime.Contains(message.Chat.Id))
            {
                await ProcessCustomTimeInput(message.Chat.Id, message.Text, message.From);
            }
            else if (usersAwaitingTimeChange.ContainsKey(message.Chat.Id))
            {
                await ProcessTimeChangeInput(message.Chat.Id, message.Text, message.From);
            }
            else
            {
                // Меню для обычных пользователей
                await HandleCustomerMenu(message.Chat.Id, message.Text);
            }
        }
        else if (update.Type == UpdateType.CallbackQuery)
        {
            var callbackQuery = update.CallbackQuery;

            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            // Обработка выбора шаурмы или времени доставки
            if (callbackQuery.Data.StartsWith("shawarma_"))
            {
                await UpdateShawarma(chatId, int.Parse(callbackQuery.Data.Split('_')[1]), messageId, callbackQuery.From.Id);
            }
            else if (callbackQuery.Data == "buy")
            {
                await ShowCart(chatId, messageId, callbackQuery.From.Id);
            }
            else if (callbackQuery.Data == "clear_cart")
            {
                await ClearCart(chatId, messageId, callbackQuery.From.Id);
            }
            else if (callbackQuery.Data == "increase")
            {
                await UpdateCartQuantity(chatId, messageId, callbackQuery.From.Id, 1);
            }
            else if (callbackQuery.Data == "decrease")
            {
                await UpdateCartQuantity(chatId, messageId, callbackQuery.From.Id, -1);
            }
            else if (callbackQuery.Data == "confirm_order")
            {
                await ShowDeliveryOptions(chatId, messageId);
            }
            else if (callbackQuery.Data == "back_to_catalog")
            {
                await ShowShawarma(chatId, callbackQuery.From.Id);
            }
            else if (callbackQuery.Data.StartsWith("cancel_order_"))
            {
                int orderId = int.Parse(callbackQuery.Data.Split('_')[2]);
                await CancelOrder(chatId, orderId);
            }
            else if (callbackQuery.Data.StartsWith("change_time_"))
            {
                int orderId = int.Parse(callbackQuery.Data.Split('_')[2]);
                await InitiateTimeChange(chatId, orderId);
            }
            else
            {
                await HandleDeliveryTimeSelection(callbackQuery.Data, chatId, callbackQuery.From);
            }
        }
    }

    private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Ошибка API Telegram: {apiRequestException.ErrorCode} — {apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }

    // Метод для отображения информации о шаурме
    private static async Task ShowShawarma(long chatId, long userId)
    {
        var shawarma = shawarmaCatalog[currentShawarmaIndex];

        int quantity = 0;
        if (userCarts.ContainsKey(userId) && userCarts[userId].ContainsKey(currentShawarmaIndex))
        {
            quantity = userCarts[userId][currentShawarmaIndex];
        }

        string caption = $"{shawarma.Name}\n{shawarma.Description}\nВес: {shawarma.Weight} г\nЦена: {shawarma.Price} руб.";
        if (quantity > 0)
        {
            caption += $"\n\nВ корзине: {quantity} шт.";
        }

        await botClient.SendPhotoAsync(
            chatId: chatId,
            photo: InputFile.FromUri(new Uri(shawarma.PhotoUrl)),
            caption: caption,
            replyMarkup: CreateShawarmaNavigationButtons(userId)
        );
    }

    // Обновление информации о шаурме
    private static async Task UpdateShawarma(long chatId, int newShawarmaIndex, int messageId, long userId)
    {
        currentShawarmaIndex = newShawarmaIndex;
        var shawarma = shawarmaCatalog[currentShawarmaIndex];

        int quantity = 0;
        if (userCarts.ContainsKey(userId) && userCarts[userId].ContainsKey(currentShawarmaIndex))
        {
            quantity = userCarts[userId][currentShawarmaIndex];
        }

        string caption = $"{shawarma.Name}\n{shawarma.Description}\nВес: {shawarma.Weight} г\nЦена: {shawarma.Price} руб.";
        if (quantity > 0)
        {
            caption += $"\n\nВ корзине: {quantity} шт.";
        }

        try
        {
            await botClient.EditMessageMediaAsync(
                chatId: chatId,
                messageId: messageId,
                media: new InputMediaPhoto(InputFile.FromUri(new Uri(shawarma.PhotoUrl)))
            );
        }
        catch (Exception)
        {
            // Игнорируем ошибку, если фотография такая же
        }

        await botClient.EditMessageCaptionAsync(
            chatId: chatId,
            messageId: messageId,
            caption: caption,
            replyMarkup: CreateShawarmaNavigationButtons(userId)
        );
    }

    // Обновление количества в корзине
    private static async Task UpdateCartQuantity(long chatId, int messageId, long userId, int change)
    {
        if (!userCarts.ContainsKey(userId))
        {
            userCarts[userId] = new Dictionary<int, int>();
        }

        if (!userCarts[userId].ContainsKey(currentShawarmaIndex))
        {
            userCarts[userId][currentShawarmaIndex] = 0;
        }

        userCarts[userId][currentShawarmaIndex] += change;

        if (userCarts[userId][currentShawarmaIndex] < 0)
        {
            userCarts[userId][currentShawarmaIndex] = 0;
        }

        // Удаляем товар из корзины, если количество равно нулю
        if (userCarts[userId][currentShawarmaIndex] == 0)
        {
            userCarts[userId].Remove(currentShawarmaIndex);
        }

        // Обновляем отображение шаурмы
        await UpdateShawarma(chatId, currentShawarmaIndex, messageId, userId);
    }

    // Очистка корзины
    private static async Task ClearCart(long chatId, int messageId, long userId)
    {
        if (userCarts.ContainsKey(userId))
        {
            userCarts[userId].Clear();
        }

        await UpdateShawarma(chatId, currentShawarmaIndex, messageId, userId);
    }

    // Создание кнопок для выбора шаурмы с количеством заказанных единиц
    private static InlineKeyboardMarkup CreateShawarmaNavigationButtons(long userId)
    {
        var buttons = new List<InlineKeyboardButton[]>();

        // Первый ряд - кнопки "-" и "+"
        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("➖", "decrease"),
            InlineKeyboardButton.WithCallbackData("➕", "increase")
        });

        // Второй ряд для куриных видов шаурмы
        var chickenButtons = new List<InlineKeyboardButton>();
        for (int i = 0; i < 3; i++)
        {
            int quantity = 0;
            if (userCarts.ContainsKey(userId) && userCarts[userId].ContainsKey(i))
            {
                quantity = userCarts[userId][i];
            }

            string buttonText = $"{shawarmaCatalog[i].MeatSticker}{shawarmaCatalog[i].FeatureSticker}";
            if (quantity > 0)
            {
                buttonText += $" ({quantity})";
            }

            if (i == currentShawarmaIndex)
            {
                buttonText = $"⭐ {buttonText} ⭐";
            }

            chickenButtons.Add(InlineKeyboardButton.WithCallbackData(buttonText, $"shawarma_{i}"));
        }
        buttons.Add(chickenButtons.ToArray());

        // Третий ряд для говяжьих видов шаурмы
        var beefButtons = new List<InlineKeyboardButton>();
        for (int i = 3; i < shawarmaCatalog.Count; i++)
        {
            int quantity = 0;
            if (userCarts.ContainsKey(userId) && userCarts[userId].ContainsKey(i))
            {
                quantity = userCarts[userId][i];
            }

            string buttonText = $"{shawarmaCatalog[i].MeatSticker}{shawarmaCatalog[i].FeatureSticker}";
            if (quantity > 0)
            {
                buttonText += $" ({quantity})";
            }

            if (i == currentShawarmaIndex)
            {
                buttonText = $"⭐ {buttonText} ⭐";
            }

            beefButtons.Add(InlineKeyboardButton.WithCallbackData(buttonText, $"shawarma_{i}"));
        }
        buttons.Add(beefButtons.ToArray());

        // Четвертый ряд - Кнопка "Очистить корзину"
        if (userCarts.ContainsKey(userId) && userCarts[userId].Values.Any(qty => qty > 0))
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("Очистить корзину", "clear_cart")
            });
        }

        // Пятый ряд - Кнопка "Перейти к оформлению"
        if (userCarts.ContainsKey(userId) && userCarts[userId].Values.Any(qty => qty > 0))
        {
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Перейти к оформлению", "buy") });
        }

        return new InlineKeyboardMarkup(buttons);
    }

    // Показать корзину
    private static async Task ShowCart(long chatId, int messageId, long userId)
    {
        if (!userCarts.ContainsKey(userId) || userCarts[userId].Count == 0)
        {
            await botClient.AnswerCallbackQueryAsync(
                callbackQueryId: userId.ToString(),
                text: "Ваша корзина пуста. Добавьте товары перед покупкой.",
                showAlert: true
            );
            return;
        }

        var cart = userCarts[userId];
        string cartDetails = "Ваш заказ:\n\n";
        int total = 0;

        foreach (var item in cart)
        {
            if (item.Value > 0)
            {
                var shawarma = shawarmaCatalog[item.Key];
                int itemTotal = shawarma.Price * item.Value;
                cartDetails += $"{shawarma.Name} - {item.Value} шт. x {shawarma.Price} руб. = {itemTotal} руб.\n";
                total += itemTotal;
            }
        }

        cartDetails += $"\nОбщая стоимость: {total} руб.";

        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Подтвердить заказ", "confirm_order") },
            new[] { InlineKeyboardButton.WithCallbackData("Вернуться в каталог", "back_to_catalog") }
        });

        await botClient.EditMessageCaptionAsync(
            chatId: chatId,
            messageId: messageId,
            caption: cartDetails,
            replyMarkup: inlineKeyboard
        );
    }

    // Метод для отображения выбора времени доставки
    private static async Task ShowDeliveryOptions(long chatId, int messageId)
    {
        // Создаем новую клавиатуру с кнопками выбора времени доставки
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Как можно скорее", "now"), InlineKeyboardButton.WithCallbackData("Через 20 минут", "20min") },
            new[] { InlineKeyboardButton.WithCallbackData("Через 30 минут", "30min"), InlineKeyboardButton.WithCallbackData("Через 1 час", "1hour") },
            new[] { InlineKeyboardButton.WithCallbackData("Выбрать свое время", "custom_time") }
        });

        // Изменяем описание под текущей фотографией шаурмы и заменяем кнопки на выбор времени доставки
        await botClient.EditMessageCaptionAsync(
            chatId: chatId,
            messageId: messageId,
            caption: "Выберите время доставки для вашего заказа:",
            replyMarkup: inlineKeyboard
        );
    }

    // Обработка выбора времени доставки
    private static async Task HandleDeliveryTimeSelection(string callbackData, long chatId, User user)
    {
        DateTime deliveryTime = DateTime.Now;

        switch (callbackData)
        {
            case "now":
                selectedDeliveryTime = "Как можно скорее";
                deliveryTime = DateTime.Now;
                break;
            case "20min":
                deliveryTime = deliveryTime.AddMinutes(20);
                selectedDeliveryTime = $"Через 20 минут (в {deliveryTime:HH:mm})";
                break;
            case "30min":
                deliveryTime = deliveryTime.AddMinutes(30);
                selectedDeliveryTime = $"Через 30 минут (в {deliveryTime:HH:mm})";
                break;
            case "1hour":
                deliveryTime = deliveryTime.AddHours(1);
                selectedDeliveryTime = $"Через 1 час (в {deliveryTime:HH:mm})";
                break;
            case "custom_time":
                await botClient.SendTextMessageAsync(chatId, "Введите свое время в формате HH:mm (например, 14:30):");
                usersAwaitingCustomTime.Add(chatId);
                return;
        }

        await FinalizeOrder(chatId, user, deliveryTime);
    }

    // Обработка пользовательского ввода времени
    private static async Task ProcessCustomTimeInput(long chatId, string userInput, User user)
    {
        if (DateTime.TryParseExact(userInput, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime customTime))
        {
            DateTime now = DateTime.Now;
            DateTime todayCustomTime = new DateTime(now.Year, now.Month, now.Day, customTime.Hour, customTime.Minute, 0);

            if (todayCustomTime.TimeOfDay >= new TimeSpan(10, 0, 0) && todayCustomTime.TimeOfDay <= new TimeSpan(21, 0, 0))
            {
                if (todayCustomTime >= now)
                {
                    selectedDeliveryTime = $"К {todayCustomTime:HH:mm}";
                    usersAwaitingCustomTime.Remove(chatId);
                    await FinalizeOrder(chatId, user, todayCustomTime);
                }
                else
                {
                    await botClient.SendTextMessageAsync(chatId, "Вы указали время в прошлом. Пожалуйста, введите будущее время в формате HH:mm (например, 14:30):");
                }
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Извините, мы работаем с 10:00 до 21:00. Пожалуйста, введите время в этом диапазоне.");
            }
        }
        else
        {
            await botClient.SendTextMessageAsync(chatId, "Неверный формат времени. Пожалуйста, введите время в формате HH:mm (например, 14:30):");
        }
    }

    // Финализация заказа
    private static async Task FinalizeOrder(long chatId, User user, DateTime deliveryTime)
    {
        // Формируем информацию о заказе
        string orderInfo = $"Новый заказ!\nПользователь: {user.FirstName} {user.LastName} (@{user.Username})\n" +
                           $"Время доставки: {selectedDeliveryTime}\n\n";

        var cart = userCarts[user.Id];
        int total = 0;
        string productDetails = "";

        foreach (var item in cart)
        {
            if (item.Value > 0)
            {
                var shawarma = shawarmaCatalog[item.Key];
                int itemTotal = shawarma.Price * item.Value;
                orderInfo += $"{shawarma.Name} - {item.Value} шт. x {shawarma.Price} руб. = {itemTotal} руб.\n";
                productDetails += $"{shawarma.Name} - {item.Value} шт.; ";
                total += itemTotal;
            }
        }

        orderInfo += $"\nОбщая стоимость: {total} руб.";

        // Сохраняем заказ в базе данных
        using (var connection = new SQLiteConnection($"Data Source={dbFile};Version=3;"))
        {
            connection.Open();
            string insertQuery = @"
                INSERT INTO Orders (UserId, UserName, Product, DeliveryTime, OrderTime, Status)
                VALUES (@UserId, @UserName, @Product, @DeliveryTime, @OrderTime, @Status)";
            using (var command = new SQLiteCommand(insertQuery, connection))
            {
                command.Parameters.AddWithValue("@UserId", user.Id);
                command.Parameters.AddWithValue("@UserName", $"{user.FirstName} {user.LastName}");
                command.Parameters.AddWithValue("@Product", productDetails);
                command.Parameters.AddWithValue("@DeliveryTime", deliveryTime.ToString("yyyy-MM-dd HH:mm"));
                command.Parameters.AddWithValue("@OrderTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                command.Parameters.AddWithValue("@Status", "Pending");
                command.ExecuteNonQuery();
            }
            connection.Close();
        }

        await botClient.SendTextMessageAsync(workerChatId, orderInfo);

        await botClient.SendTextMessageAsync(chatId, "Ваш заказ оформлен! Спасибо за покупку.");

        // Очищаем корзину пользователя
        userCarts[user.Id].Clear();
    }

    // Меню для пользователя
    private static async Task HandleCustomerMenu(long chatId, string messageText)
    {
        switch (messageText)
        {
            case "/start":
                await SendWelcomeMessage(chatId);
                break;
            case "Каталог":
                currentShawarmaIndex = 0;
                await ShowShawarma(chatId, chatId);
                break;
            case "Меню":
                await SendMenu(chatId);
                break;
            case "О нас":
                await SendAboutUs(chatId);
                break;
            case "Мои заказы":
                await ShowUserOrders(chatId);
                break;
            case "Акции":
                await SendPromotions(chatId);
                break;
            case "Помощь":
                await SendHelp(chatId);
                break;
            default:
                await botClient.SendTextMessageAsync(chatId, "Я не понимаю такую команду. Пожалуйста, используйте меню.");
                break;
        }
    }

    // Приветственное сообщение
    private static async Task SendWelcomeMessage(long chatId)
    {
        string welcomeText = "Добро пожаловать в наш шаурма-бот!\n" +
                             "У нас самая вкусная шаурма в Брянске!\n" +
                             "📍 Адрес: Брянск, ул. Степная 13\n" +
                             "🕙 Время работы: с 10:00 до 21:00\n\n" +
                             "Выберите опцию из меню ниже, чтобы продолжить.";

        await botClient.SendPhotoAsync(
            chatId: chatId,
            photo: InputFile.FromUri("https://avatars.mds.yandex.net/i?id=f22e901ea6d3e96191b50919fd0526bec950d1f449a98924-12629451-images-thumbs&n=13"),
            caption: welcomeText,
            replyMarkup: CreateMainMenuKeyboard()
        );
    }

    // Главное меню для пользователей
    private static ReplyKeyboardMarkup CreateMainMenuKeyboard()
    {
        var replyKeyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "Каталог", "Меню" },
            new KeyboardButton[] { "О нас", "Мои заказы", "Помощь" }
        })
        {
            ResizeKeyboard = true
        };
        return replyKeyboard;
    }

    // Отправка меню
    private static async Task SendMenu(long chatId)
    {
        string menuText = "Наше меню:\n" +
                          "🍗🥙 Куриная классическая - 250 руб.\n" +
                          "🍗🍟 Куриная с картошкой фри - 300 руб.\n" +
                          "🍗🧀 Куриная сырная - 350 руб.\n" +
                          "🥩🥙 Говяжья классическая - 350 руб.\n" +
                          "🥩🍟 Говяжья с картошкой фри - 400 руб.\n" +
                          "🥩🧀 Говяжья сырная - 450 руб.\n\n" +
                          "Выберите 'Каталог' в меню, чтобы оформить заказ.";

        // Замените на действительный URL изображения меню
        string menuImageUrl = "https://yastatic.net/naydex/yandex-search/1D6Ok8o18/e30844yRj3/iRE3gHLE-iljbYrRCMuWfk0ummDWTB-MCTL58oGnI7MiJunNIMRe3_hMiN0g3K0s7RHzASG1vOFgYjyaZ99Uo85Ecsw1hOd3MC6ThQDIk3xQc9lxyhYb0RNwpf3G_32-JSyO9gXkfbPVKvGiXvC1dbZHo0k8o6uJCw";

        await botClient.SendPhotoAsync(
            chatId: chatId,
            photo: InputFile.FromUri(menuImageUrl),
            caption: menuText
        );
    }

    // Отправка информации "О нас"
    private static async Task SendAboutUs(long chatId)
    {
        string aboutUsText = "О нас:\n\n" +
                             "Наша шаурмечная открылась в сердце Брянска на улице Степная 13. Мы начинали как маленькая семейная закусочная и выросли благодаря любви наших клиентов к нашей вкусной и сытной шаурме.\n\n" +
                             "Наши повара используют только свежие ингредиенты и оригинальные рецепты, чтобы каждая шаурма была неповторимой. Приходите и убедитесь сами!\n\n" +
                             "🕙 Время работы: с 10:00 до 21:00\n" +
                             "📍 Адрес: Брянск, ул. Степная 13";

        await botClient.SendTextMessageAsync(chatId, aboutUsText);
    }

    // Показать активные заказы пользователя
    private static async Task ShowUserOrders(long chatId)
    {
        using (var connection = new SQLiteConnection($"Data Source={dbFile};Version=3;"))
        {
            connection.Open();
            string selectQuery = @"
                SELECT Id, Product, DeliveryTime, Status
                FROM Orders
                WHERE UserId = @UserId AND Status = 'Pending'";
            using (var command = new SQLiteCommand(selectQuery, connection))
            {
                command.Parameters.AddWithValue("@UserId", chatId);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.HasRows)
                    {
                        while (reader.Read())
                        {
                            int orderId = reader.GetInt32(0);
                            string product = reader.GetString(1);
                            DateTime deliveryTime = DateTime.Parse(reader.GetString(2));
                            string status = reader.GetString(3);

                            string orderDetails = $"Заказ №{orderId}\n" +
                                                  $"Товары: {product}\n" +
                                                  $"Время доставки: {deliveryTime:yyyy-MM-dd HH:mm}\n";

                            TimeSpan timeRemaining = deliveryTime - DateTime.Now;

                            if (timeRemaining.TotalMinutes > 10)
                            {
                                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                                {
                                    new[] {
                                        InlineKeyboardButton.WithCallbackData("Отменить заказ", $"cancel_order_{orderId}"),
                                        InlineKeyboardButton.WithCallbackData("Изменить время готовности", $"change_time_{orderId}")
                                    }
                                });

                                await botClient.SendTextMessageAsync(chatId, orderDetails, replyMarkup: inlineKeyboard);
                            }
                            else
                            {
                                orderDetails += "⚠️ Заказ не может быть отменен или изменен, так как до его выполнения осталось менее 10 минут.";
                                await botClient.SendTextMessageAsync(chatId, orderDetails);
                            }
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, "У вас нет активных заказов.");
                    }
                }
            }
            connection.Close();
        }
    }

    // Отмена заказа
    private static async Task CancelOrder(long chatId, int orderId)
    {
        using (var connection = new SQLiteConnection($"Data Source={dbFile};Version=3;"))
        {
            connection.Open();
            string selectQuery = @"
                SELECT DeliveryTime
                FROM Orders
                WHERE Id = @OrderId AND UserId = @UserId AND Status = 'Pending'";
            using (var command = new SQLiteCommand(selectQuery, connection))
            {
                command.Parameters.AddWithValue("@OrderId", orderId);
                command.Parameters.AddWithValue("@UserId", chatId);

                var deliveryTimeObj = command.ExecuteScalar();

                if (deliveryTimeObj != null)
                {
                    DateTime deliveryTime = DateTime.Parse(deliveryTimeObj.ToString());
                    TimeSpan timeRemaining = deliveryTime - DateTime.Now;

                    if (timeRemaining.TotalMinutes > 10)
                    {
                        // Обновляем статус заказа
                        string updateQuery = "UPDATE Orders SET Status = 'Canceled' WHERE Id = @OrderId";
                        using (var updateCommand = new SQLiteCommand(updateQuery, connection))
                        {
                            updateCommand.Parameters.AddWithValue("@OrderId", orderId);
                            updateCommand.ExecuteNonQuery();
                        }

                        await botClient.SendTextMessageAsync(chatId, $"Заказ №{orderId} был отменен.");

                        // Уведомляем сотрудника
                        await botClient.SendTextMessageAsync(workerChatId, $"Заказ №{orderId} был отменен пользователем.");
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, "Невозможно отменить заказ, до его выполнения осталось менее 10 минут.");
                    }
                }
                else
                {
                    await botClient.SendTextMessageAsync(chatId, "Заказ не найден или уже был отменен.");
                }
            }
            connection.Close();
        }
    }

    // Инициация изменения времени доставки
    private static async Task InitiateTimeChange(long chatId, int orderId)
    {
        usersAwaitingTimeChange[chatId] = orderId;
        await botClient.SendTextMessageAsync(chatId, "Введите новое время доставки в формате HH:mm (например, 15:30):");
    }

    // Обработка ввода нового времени доставки
    private static async Task ProcessTimeChangeInput(long chatId, string userInput, User user)
    {
        int orderId = usersAwaitingTimeChange[chatId];

        if (DateTime.TryParseExact(userInput, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime newTime))
        {
            DateTime now = DateTime.Now;
            DateTime todayNewTime = new DateTime(now.Year, now.Month, now.Day, newTime.Hour, newTime.Minute, 0);

            if (todayNewTime.TimeOfDay >= new TimeSpan(10, 0, 0) && todayNewTime.TimeOfDay <= new TimeSpan(21, 0, 0))
            {
                using (var connection = new SQLiteConnection($"Data Source={dbFile};Version=3;"))
                {
                    connection.Open();
                    string selectQuery = @"
                        SELECT DeliveryTime
                        FROM Orders
                        WHERE Id = @OrderId AND UserId = @UserId AND Status = 'Pending'";
                    using (var command = new SQLiteCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("@OrderId", orderId);
                        command.Parameters.AddWithValue("@UserId", chatId);

                        var deliveryTimeObj = command.ExecuteScalar();

                        if (deliveryTimeObj != null)
                        {
                            DateTime oldDeliveryTime = DateTime.Parse(deliveryTimeObj.ToString());
                            TimeSpan timeRemaining = oldDeliveryTime - now;

                            if (timeRemaining.TotalMinutes > 10)
                            {
                                // Обновляем время доставки
                                string updateQuery = "UPDATE Orders SET DeliveryTime = @NewDeliveryTime WHERE Id = @OrderId";
                                using (var updateCommand = new SQLiteCommand(updateQuery, connection))
                                {
                                    updateCommand.Parameters.AddWithValue("@NewDeliveryTime", todayNewTime.ToString("yyyy-MM-dd HH:mm"));
                                    updateCommand.Parameters.AddWithValue("@OrderId", orderId);
                                    updateCommand.ExecuteNonQuery();
                                }

                                await botClient.SendTextMessageAsync(chatId, $"Время доставки для заказа №{orderId} было изменено на {todayNewTime:HH:mm}.");

                                // Уведомляем сотрудника
                                await botClient.SendTextMessageAsync(workerChatId, $"Заказ №{orderId} был изменен пользователем. Новое время доставки: {todayNewTime:HH:mm}.");
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(chatId, "Невозможно изменить время доставки, до выполнения заказа осталось менее 10 минут.");
                            }
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(chatId, "Заказ не найден или уже был отменен.");
                        }
                    }
                    connection.Close();
                }

                usersAwaitingTimeChange.Remove(chatId);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Извините, мы работаем с 10:00 до 21:00. Пожалуйста, введите время в этом диапазоне.");
            }
        }
        else
        {
            await botClient.SendTextMessageAsync(chatId, "Неверный формат времени. Пожалуйста, введите время в формате HH:mm (например, 14:30):");
        }
    }

    // Метод отправки информации о промоакциях
    private static async Task SendPromotions(long chatId)
    {
        await botClient.SendTextMessageAsync(chatId, "🎉 Акции:\n\nСкидка 15% для студентов на все виды шаурмы при предъявлении студенческого билета.\n\nНе упустите шанс насладиться нашей вкусной шаурмой по выгодной цене!");
    }

    // Метод отправки помощи
    private static async Task SendHelp(long chatId)
    {
        await botClient.SendTextMessageAsync(chatId,
            "Как пользоваться ботом:\n" +
            "1. Нажмите на 'Каталог', чтобы выбрать шаурму.\n" +
            "2. Используйте '+' и '-', чтобы добавить товар в корзину.\n" +
            "3. Нажмите 'Перейти к оформлению', чтобы оформить заказ и выбрать время доставки.\n" +
            "4. В 'Моих заказах' вы можете просмотреть активные заказы, отменить или изменить время доставки, если до выполнения заказа осталось более 10 минут.\n\n" +
            "Если у вас возникли проблемы или вопросы, свяжитесь с нами, и мы с радостью поможем!\n" +
            "Контакт для связи: @support_username"
        );
    }
}
