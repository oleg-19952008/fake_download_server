using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace FakeDownloadServer
{
    /// <summary>
    /// Основной класс программы, реализующий HTTP-сервер для имитации загрузки файлов.
    /// Поддерживает ночной режим работы, систему банов IP-адресов, логирование и обработку запросов.
    /// </summary>
    class Program
    {
        private static readonly object nightModeLock = new object();
        /// <summary>Флаг, указывающий, находится ли сервер в спящем режиме (ночной режим).</summary>
        private static volatile bool isInSleepMode = false;
        /// <summary>Флаг принудительного запуска сервера в ночное время.</summary>
        private static volatile bool isForceRunRequested = false;
        /// <summary>Флаг включения ночного режима работы сервера.</summary>
        private static volatile bool isNightModeEnabled = true;
        /// <summary>Время начала ночного режима (час).</summary>
        private static int nightStartHour = 1;
        /// <summary>Время окончания ночного режима (час).</summary>
        private static int nightEndHour = 6;
        /// <summary>Минимальный размер файла в МБ для скачивания.</summary>
        private const int MinFileSizeMB = 1;
        /// <summary>Максимальный размер файла в МБ для скачивания.</summary>
        private const int MaxFileSizeMB = 10240;
        /// <summary>Список путей, разрешённых для доступа без ограничений.</summary>
        private static readonly string[] WhiteListedPaths = { "/", "/favicon.ico" };
        private static string logFileName;
        private static readonly object logLock = new object();
        private static BanManager banManager;
        private static List<string> randomHeaders = new List<string>();
        // Используем ThreadLocal для потокобезопасного Random на .NET Framework 4.8
        private static readonly ThreadLocal<Random> random = new ThreadLocal<Random>(() => new Random(), trackAllValues: true);
        private static readonly object headerLock = new object();
        private static CancellationTokenSource serverCts = new CancellationTokenSource();
        private static HttpListener listener;
        private static readonly object listenerLock = new object();
        private static List<string> suspiciousUserAgents = new List<string>();
        private static FileSystemWatcher fileWatcher;
        private static readonly object agentsLock = new object();
        /// <summary>Список размеров файлов в байтах для генерации главной страницы (не используется, теперь размеры задаются через JS).</summary>
        private static readonly List<long> FileSizes = new List<long>();

        /// <summary>
        /// Загружает список подозрительных User-Agent из файла suspicious_agents.txt
        /// </summary>
        private static void LoadSuspiciousUserAgents()
        {
            try
            {
                string agentsFile = "suspicious_agents.txt";
                if (File.Exists(agentsFile))
                {
                    var newAgents = File.ReadAllLines(agentsFile, Encoding.UTF8)
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                        .Select(line => line.Trim().ToLower())
                        .ToList();
                    
                    lock (agentsLock)
                    {
                        suspiciousUserAgents = newAgents;
                    }
                    Log($"Загружено {suspiciousUserAgents.Count} подозрительных User-Agent");
                }
                else
                {
                    Log("Файл suspicious_agents.txt не найден! Используется стандартный список.");
                    // Стандартный список если файл не найден
                    var defaultAgents = new List<string>
                    {
                        "curl", "wget", "python", "scanner", "bot", 
                        "spider", "crawler", "zgrab", "go-http-client",
                        "internetmeasurement", "palo alto networks"
                    };
                    lock (agentsLock)
                    {
                        suspiciousUserAgents = defaultAgents;
                    }
                }
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка загрузки списка подозрительных User-Agent", ex);
                var defaultAgents = new List<string>
                {
                    "curl", "wget", "python", "scanner", "bot", 
                    "spider", "crawler", "zgrab", "go-http-client",
                    "internetmeasurement", "palo alto networks"
                };
                lock (agentsLock)
                {
                    suspiciousUserAgents = defaultAgents;
                }
            }
        }

        /// <summary>
        /// Инициализирует наблюдение за изменениями в файле suspicious_agents.txt
        /// и автоматически перезагружает список при изменении файла.
        /// </summary>
        private static void InitializeFileWatcher()
        {
            try
            {
                string agentsFile = "suspicious_agents.txt";
                string directory = Path.GetDirectoryName(Path.GetFullPath(agentsFile)) ?? Directory.GetCurrentDirectory();
                string fileName = Path.GetFileName(agentsFile);

                fileWatcher = new FileSystemWatcher(directory, fileName);
                fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                fileWatcher.Changed += OnSuspiciousAgentsFileChanged;
                fileWatcher.EnableRaisingEvents = true;
                
                Log($"Мониторинг изменений файла {agentsFile} включен");
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка инициализации FileWatcher", ex);
            }
        }

        /// <summary>
        /// Обработчик события изменения файла suspicious_agents.txt.
        /// Перезагружает список подозрительных User-Agent при изменении файла.
        /// </summary>
        private static void OnSuspiciousAgentsFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Небольшая задержка чтобы файл полностью записался
                System.Threading.Thread.Sleep(100);
                Log($"Обнаружено изменение файла suspicious_agents.txt. Перезагрузка списка...");
                LoadSuspiciousUserAgents();
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка при обработке изменения файла suspicious_agents.txt", ex);
            }
        }

        /// <summary>
        /// Определяет, является ли текущее время ночным (в интервале ночного режима).
        /// </summary>
        /// <returns>True, если текущее время находится в интервале ночного режима, иначе False.</returns>
        private static bool IsNightTime()
        {
            var now = DateTime.Now;
            //    Console.WriteLine($"[{now:HH:mm:ss}] Проверка времени: NightStart={nightStartHour}, NightEnd={nightEndHour}");
            if (nightStartHour < nightEndHour)
            {
                return now.Hour >= nightStartHour && now.Hour < nightEndHour;
            }
            else
            {
                return now.Hour >= nightStartHour || now.Hour < nightEndHour;
            }
        }

        /// <summary>
        /// Асинхронно ожидает наступления дневного времени или принудительного запуска сервера.
        /// Периодически проверяет нажатие клавиши 'Y' для принудительного запуска в ночное время.
        /// </summary>
        private static async Task WaitForDayTime()
        {
            while (IsNightTime() && !isForceRunRequested && !serverCts.Token.IsCancellationRequested)
            {
                Log($"НОЧНОЙ РЕЖИМ: Сервер отключен с {nightStartHour}:00 до {nightEndHour}:00");
                Print("Нажмите 'Y' для принудительного запуска или любую другую клавишу для продолжения ожидания...");

                try
                {
                    var keyTask = Task.Run(() =>
                    {
                        try
                        {
                            return Console.ReadKey(true);
                        }
                        catch
                        {
                            return new ConsoleKeyInfo();
                        }
                    }, serverCts.Token);

                    var delayTask = Task.Delay(30000, serverCts.Token);
                    var completedTask = await Task.WhenAny(keyTask, delayTask);

                    if (completedTask == keyTask && !serverCts.Token.IsCancellationRequested)
                    {
                        var key = keyTask.Result;
                        if (key.KeyChar == 'Y' || key.KeyChar == 'y')
                        {
                            lock (nightModeLock)
                            {
                                isForceRunRequested = true;
                            }
                            Log($"ПРИНУДИТЕЛЬНЫЙ ЗАПУСК: Сервер запускается в ночное время");
                            LogToFile("ПРИНУДИТЕЛЬНЫЙ ЗАПУСК: Сервер запущен вручную в ночное время");
                            break;
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Загружает случайные заголовки из файла random_headers.txt для использования в ответах сервера.
        /// </summary>
        private static void LoadRandomHeaders()
        {
            try
            {
                string headersFile = "random_headers.txt";
                if (File.Exists(headersFile))
                {
                    randomHeaders = File.ReadAllLines(headersFile, Encoding.UTF8)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .ToList();
                    Log($"Загружено {randomHeaders.Count} рандомных заголовков");
                }
                else
                {
                    Log("Файл random_headers.txt не найден! Создайте файл и добавьте строки.");
                }
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка загрузки рандомных заголовков", ex);
            }
        }

        /// <summary>
        /// Возвращает случайный заголовок из загруженного списка.
        /// </summary>
        /// <returns>Случайный заголовок или строка "FILE NOT FOUND", если список пуст.</returns>
        private static string GetRandomHeader()
        {
            lock (headerLock)
            {
                if (randomHeaders.Count == 0)
                    return "FILE NOT FOUND";
                return randomHeaders[random.Value.Next(randomHeaders.Count)];
            }
        }

        /// <summary>
        /// Точка входа в приложение. Инициализирует сервер, загружает конфигурацию и запускает основной цикл обработки запросов.
        /// </summary>
        /// <param name="args">Аргументы командной строки (не используются).</param>
        static async Task Main(string[] args)
        {
            Thread.Sleep(1500);

            var startTime = DateTime.Now;

            logFileName = $"server_log_{startTime:yyyy-MM-dd_HH-mm-ss}.txt";

            Log($"Программа запущена: {startTime:yyyy-MM-dd HH:mm:ss}");

            banManager = new BanManager("bans.ini");
            LoadRandomHeaders();
            LoadSuspiciousUserAgents();
            InitializeFileWatcher();

            const int port = 5000;
            listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");

            try
            {
                listener.Start();
                Log($"Сервер успешно запущен на порту {port}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка запуска HttpListener: {ex.Message}");
                return;
            }

            // Запускаем задачу проверки ночного режима с периодической очисткой старых записей
            _ = Task.Run(async () =>
            {
                var cleanupTask = Task.Run(async () =>
                {
                    while (!serverCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(60000, serverCts.Token); // Каждую минуту
                            banManager?.CleanupOldTracking(2);
                        }
                        catch (TaskCanceledException) { break; }
                        catch (Exception ex) { LogErrorToFile("Ошибка очистки tracking", ex); }
                    }
                });
                
                await CheckNightModeAsync();
            });

            while (!serverCts.Token.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    bool isNightTimeNow = IsNightTime();
                    bool shouldSleep = isNightModeEnabled && isNightTimeNow;
                    
                    lock (nightModeLock)
                    {
                        shouldSleep = shouldSleep && !isForceRunRequested;
                    }

                    if (shouldSleep)
                    {
                        if (!isInSleepMode)
                        {
                            Log($"НОЧНОЙ РЕЖИМ: Обработка запросов приостановлена");
                            isInSleepMode = true;
                        }
                        await Task.Delay(1000, serverCts.Token);
                        continue;
                    }
                    else if (isInSleepMode)
                    {
                        Log($"ДНЕВНОЙ РЕЖИМ: Обработка запросов возобновлена");
                        isInSleepMode = false;
                        
                        lock (nightModeLock)
                        {
                            if (!isNightTimeNow)
                            {
                                isForceRunRequested = false;
                            }
                        }
                    }

                    Log($"Ожидание нового запроса...");
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                    Log($"Получен новый запрос: {context.Request.Url}");
                }
                catch (HttpListenerException ex)
                {
                    if (serverCts.Token.IsCancellationRequested)
                    {
                        Log($"Основной цикл остановлен (HttpListener закрыт)");
                        break;
                    }
                    Log($"Ошибка HttpListener: {ex.Message} (Код: {ex.ErrorCode})");
                    await Task.Delay(1000);
                    continue;
                }
                catch (Exception ex)
                {
                    Log($"Неожиданная ошибка в основном цикле: {ex.Message}");
                    await Task.Delay(1000);
                    continue;
                }

                if (context != null)
                {
                    _ = HandleRequestAsync(context);
                }
            }

            Log($"Сервер останавливается...");
            
            // Останавливаем FileWatcher
            if (fileWatcher != null)
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Dispose();
                Log("Мониторинг файла suspicious_agents.txt остановлен");
            }
            
            lock (listenerLock)
            {
                if (listener != null && listener.IsListening)
                {
                    listener.Stop();
                    listener.Close();
                }
            }
        }

        /// <summary>
        /// Асинхронно проверяет и управляет ночным режимом работы сервера.
        /// Периодически (каждые 5 секунд) проверяет текущее время и переключает режимы сна/бодрствования.
        /// </summary>
        private static async Task CheckNightModeAsync()
        {
            while (!serverCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, serverCts.Token);

                    bool isNightTimeNow = IsNightTime();
                    bool shouldSleep = isNightModeEnabled && isNightTimeNow;
                    
                    lock (nightModeLock)
                    {
                        shouldSleep = shouldSleep && !isForceRunRequested;
                    }

                    if (shouldSleep && !isInSleepMode)
                    {
                        Log($"НОЧНОЙ РЕЖИМ: Сервер в спящем режиме с {nightStartHour}:00 до {nightEndHour}:00");
                        Print("Нажмите 'Y' для принудительного запуска...");
                    }
                    else if (!shouldSleep && isInSleepMode)
                    {
                        Log($"ДНЕВНОЙ РЕЖИМ: Сервер работает нормально");
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogErrorToFile("Ошибка в CheckNightModeAsync", ex);
                    await Task.Delay(5000);
                }
            }
        }

        /// <summary>
        /// Асинхронно обрабатывает входящий HTTP-запрос от клиента.
        /// Проверяет бан IP, белый список путей, подозрительные запросы и передаёт обработку дальше.
        /// </summary>
        /// <param name="context">Контекст HTTP-запроса.</param>
        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var startTime = DateTime.Now;
            string clientIp = request.RemoteEndPoint?.Address?.ToString() ?? "Unknown";

            Log($"Обработка запроса от {clientIp} на {request.Url.AbsolutePath}");

            try
            {
                bool isLocal = IsLocalAddress(clientIp);
                Log($"IP {clientIp} локальный: {isLocal}");

                if (banManager.IsBanned(clientIp))
                {
                    // Проверяем наличие секретного кода в User-Agent, QueryString или пути
                    bool hasSecretCode = (request.UserAgent?.Contains("748_dark") == true) ||
                                        (request.Url.Query?.Contains("748_dark") == true) ||
                                        (request.Url.AbsolutePath == "/748_dark");

                    if (hasSecretCode)
                    {
                        Log($"Секретный код обнаружен, IP {clientIp} разбанен");

                        // Отправляем редирект на главную страницу
                        response.StatusCode = 302;
                        response.AddHeader("Location", "/");
                        Log($"Отправлен редирект на главную страницу для {clientIp}");

                        // Разбан и перезапуск
                        banManager.UnbanClient(clientIp);
                        Log($"Перезапуск приложения после разбана IP {clientIp}");
                        Log($"Перезапуск приложения...");
                        // Задержка перед перезапуском, чтобы редирект успел обработаться
                        await Task.Delay(1000); // 1 секунда задержки
                        
                        try
                        {
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                            {
                                System.Diagnostics.Process.Start(exePath);
                            }
                            else
                            {
                                Log("Ошибка: не удалось получить путь к исполняемому файлу");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogErrorToFile("Ошибка при перезапуске приложения", ex);
                        }

                        Environment.Exit(0);
                    }

                    Log($"Попытка подключения от забаненного IP: {clientIp}");
                    response.Abort();
                    return;
                }

                if (!isLocal && IsSuspiciousRequest(request))
                {
                    Log($"Подозрительный запрос от {clientIp}: UA='{request.UserAgent}'");
                    response.Abort();
                    return;
                }

                string requestPath = request.Url.AbsolutePath;
                if (!IsWhiteListed(requestPath))
                {
                    // Для всех IP (включая локальные) бан за запрос вне белого списка
                    var clientTracking = banManager.GetOrCreateClientTracking(clientIp);
                    clientTracking.BadRequestCount++;

                    Log($"Запрос не из белого списка от {clientIp}: {requestPath}. Счетчик: {clientTracking.BadRequestCount}/1");

                    if (clientTracking.BadRequestCount >= 1)
                    {
                        banManager.BanClient(clientIp, TimeSpan.FromMinutes(2));
                        Log($"КЛИЕНТ ЗАБАНЕН: {clientIp} на 2 минуты");

                        response.StatusCode = 403;
                        byte[] buffer = Encoding.UTF8.GetBytes("Доступ запрещён.");
                        response.ContentType = "text/plain; charset=utf-8";
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        return;
                    }

                    response.Abort();
                    return;
                }

                await ProcessRequestNormally(context, clientIp);
            }
            catch (Exception ex)
            {
                Log($"Ошибка в HandleRequestAsync для {clientIp}: {ex.Message}");
                LogErrorToFile($"Ошибка в HandleRequestAsync для {clientIp}: {request.Url}", ex);
                try
                {
                    response.StatusCode = 500;
                    byte[] buffer = Encoding.UTF8.GetBytes("Внутренняя ошибка сервера.");
                    response.ContentType = "text/plain; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch { }
            }
            finally
            {
                try
                {
                    if (response.OutputStream != null)
                    {
                        response.OutputStream.Close();
                    }
                    // response.Close() вызывается только если response ещё не закрыт через Abort()
                    // После Abort() повторный Close() вызовет исключение
                    Log($"Соединение для {clientIp} закрыто");
                }
                catch (ObjectDisposedException)
                {
                    Log($"Соединение для {clientIp} уже закрыто (ObjectDisposedException игнорируется)");
                }
                catch (Exception ex)
                {
                    LogErrorToFile($"Ошибка при закрытии соединения для {clientIp}", ex);
                }
            }
        }


        /// <summary>
        /// HashSet для быстрого поиска путей в белом списке (O(1)).
        /// </summary>
        private static readonly HashSet<string> WhiteListedPathsSet = new HashSet<string>(WhiteListedPaths);
        
        /// <summary>
        /// Проверяет, находится ли указанный путь в белом списке разрешённых путей.
        /// </summary>
        /// <param name="path">Путь для проверки.</param>
        /// <returns>True, если путь в белом списке, иначе False.</returns>
        private static bool IsWhiteListed(string path)
        {
            Log($"Проверка белого списка: {path}");
            
            // Проверяем точное совпадение с путями из белого списка через HashSet для O(1)
            if (WhiteListedPathsSet.Contains(path))
            {
                Log($"Путь {path} в белом списке");
                return true;
            }

            Log($"Путь {path} НЕ в белом списке");
            return false;
        }

        /// <summary>
        /// Определяет, является ли HTTP-запрос подозрительным на основе User-Agent и заголовков Accept.
        /// </summary>
        /// <param name="request">HTTP-запрос для проверки.</param>
        /// <returns>True, если запрос подозрительный, иначе False.</returns>
        private static bool IsSuspiciousRequest(HttpListenerRequest request)
        {
            string userAgent = request.UserAgent?.ToLower() ?? "";
            Log($"Проверка User-Agent: {userAgent}");

            // Проверка по списку из файла (с блокировкой для потокобезопасности)
            List<string> agentsToCheck;
            lock (agentsLock)
            {
                agentsToCheck = new List<string>(suspiciousUserAgents);
            }
            
            foreach (var suspiciousAgent in agentsToCheck)
            {
                if (userAgent.Contains(suspiciousAgent))
                {
                    Log($"Запрос помечен как подозрительный (UA: {suspiciousAgent})");
                    return true;
                }
            }

            // Проверка пустого User-Agent
            if (userAgent.Length == 0)
            {
                Log($"Запрос помечен как подозрительный (пустой UA)");
                return true;
            }

            if (request.Headers["Accept"]?.Contains("*/*") == true &&
                request.Headers["Accept-Language"] == null)
            {
                Log($"Запрос помечен как подозрительный (Accept headers)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Асинхронно обрабатывает запрос в нормальном режиме (генерация страницы или потоковая передача файла).
        /// </summary>
        /// <param name="context">Контекст HTTP-запроса.</param>
        /// <param name="clientIp">IP-адрес клиента.</param>
        private static async Task ProcessRequestNormally(HttpListenerContext context, string clientIp)
        {
            var request = context.Request;
            var response = context.Response;
            var startTime = DateTime.Now;
            bool downloadStarted = false;

            try
            {
                response.Headers.Add("X-Powered-By", GetRandomHeader());

                if (request.Url.AbsolutePath == "/")
                {
                    string page = GenerateIndexPage(request);
                    byte[] buffer = Encoding.UTF8.GetBytes(page);

                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    await LogRequest(request, response, startTime, "Главная страница");
                }
                else if (request.Url.AbsolutePath.StartsWith("/download/"))
                {
                    string sizePart = request.Url.AbsolutePath.Substring("/download/".Length);
                    
                    // Защита от path traversal: проверяем, что размер содержит только цифры
                    if (!System.Text.RegularExpressions.Regex.IsMatch(sizePart, @"^\d+$"))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        byte[] buffer = Encoding.UTF8.GetBytes("Неверный запрос.");
                        response.ContentType = "text/plain; charset=utf-8";
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        return;
                    }
                    
                    if (int.TryParse(sizePart, out int megabytes))
                    {
                        // Проверка диапазона размера файла
                        if (megabytes < MinFileSizeMB || megabytes > MaxFileSizeMB)
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            byte[] buffer = Encoding.UTF8.GetBytes($"Размер файла должен быть от {MinFileSizeMB} до {MaxFileSizeMB} МБ");
                            response.ContentType = "text/plain; charset=utf-8";
                            response.ContentLength64 = buffer.Length;
                            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            return;
                        }
                        
                        long fileSize = megabytes * 1024L * 1024;
                        string fileName = $"fake_{megabytes}MB.bin";

                        downloadStarted = true;
                        await LogDownloadStart(request, fileName, fileSize);
                        await StreamFakeFileAsync(response, fileSize, fileName, request);
                        await LogDownloadEnd(request, fileName, fileSize, startTime);
                    }
                    else
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        await LogRequest(request, response, startTime, "404 - Неверный размер файла");
                    }
                }
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 64)
            {
                if (downloadStarted)
                {
                    Log($"Загрузка прервана клиентом: {clientIp}");
                }
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Ошибка при обработке запроса от {clientIp}", ex);
                throw; // Перебрасываем для обработки в HandleRequestAsync
            }
            // Убрали finally с закрытием – теперь закрытие только в HandleRequestAsync
        }

        /// <summary>
        /// Проверяет, является ли IP-адрес локальным (localhost, приватные диапазоны IPv4).
        /// </summary>
        /// <param name="ip">IP-адрес для проверки.</param>
        /// <returns>True, если адрес локальный, иначе False.</returns>
        private static bool IsLocalAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip == "::1" || ip == "127.0.0.1" || ip == "localhost") return true;
            
            // IPv6 link-local и loopback
            if (ip.StartsWith("fe80::") || ip.StartsWith("::ffff:127.0.0.1")) return true;
            
            // Парсим IPv4 адрес для проверки приватных диапазонов
            if (IPAddress.TryParse(ip, out var address))
            {
                // Проверяем, является ли адрес IPv4
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    byte[] bytes = address.GetAddressBytes();
                    
                    // 10.0.0.0/8
                    if (bytes[0] == 10) return true;
                    
                    // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    
                    // 192.168.0.0/16
                    if (bytes[0] == 192 && bytes[1] == 168) return true;
                    
                    // Дополнительный диапазон 2.2.2.x
                    if (bytes[0] == 2 && bytes[1] == 2 && bytes[2] == 2) return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Генерирует HTML-страницу с списком доступных для загрузки файлов.
        /// </summary>
        /// <returns>HTML-строка главной страницы.</returns>
        private static string GenerateIndexPage(HttpListenerRequest request = null)
        {
            var sb = new StringBuilder();
            string clientIp = request?.RemoteEndPoint?.Address?.ToString() ?? "N/A";
            
            sb.Append("<!DOCTYPE html><html lang=\"ru\"><head>")
              .Append("<meta charset=\"utf-8\"/>")
              .Append("<title>Fake Download Server</title>")
              .Append("<style>")
              .Append("body { font-family: Arial, sans-serif; margin: 20px; }")
              .Append(".info-box { background:#f0f0f0; padding:10px; margin:10px 0; border-radius:5px; }")
              .Append(".slider-container { margin: 20px 0; }")
              .Append(".slider-container label { display: block; margin-bottom: 10px; font-weight: bold; }")
              .Append(".slider-value { font-size: 1.2em; color: #333; margin-left: 10px; }")
              .Append("input[type=range] { width: 300px; }")
              .Append(".download-btn { padding: 10px 20px; font-size: 16px; background: #4CAF50; color: white; border: none; cursor: pointer; border-radius: 5px; margin-top: 10px; }")
              .Append(".download-btn:hover { background: #45a049; }")
              .Append("</style></head><body>")
              .Append("<h1>Фейковые файлы для теста скорости</h1>");

            // Вывод информации о клиенте
            if (request != null)
            {
                string os = ParseOSFromUserAgent(request.UserAgent);
                string browser = ParseBrowserFromUserAgent(request.UserAgent);
                
                sb.Append("<div class=\"info-box\">")
                  .Append($"<p><strong>Ваш IP:</strong> {clientIp}</p>")
                  .Append($"<p><strong>ОС:</strong> {os}</p>")
                  .Append($"<p><strong>Браузер:</strong> {browser}</p>")
                  .Append("</div>");
            }

            // Интерактивный элемент выбора размера файла
            sb.Append("<div class=\"slider-container\">")
              .Append("<label for=\"fileSizeSlider\">Выберите размер файла (МБ): </label>")
              .Append("<input type=\"range\" id=\"fileSizeSlider\" min=\"1\" max=\"10240\" value=\"100\" oninput=\"updateSliderValue(this.value)\">")
              .Append("<span id=\"sliderValue\" class=\"slider-value\">100 МБ</span>")
              .Append("<br>")
              .Append("<button class=\"download-btn\" onclick=\"downloadFile()\">Скачать</button>")
              .Append("</div>");

            sb.Append("<script>")
              .Append("function updateSliderValue(value) {")
              .Append("document.getElementById('sliderValue').textContent = value + ' МБ';")
              .Append("}")
              .Append("function downloadFile() {")
              .Append("var size = document.getElementById('fileSizeSlider').value;")
              .Append($"window.location.href = '/download/' + size;")
              .Append("}")
              .Append("</script>");

            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Определяет операционную систему из User-Agent строки.
        /// </summary>
        private static string ParseOSFromUserAgent(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return "Неизвестно";
            
            if (userAgent.IndexOf("Windows NT 10.0", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 10/11";
            if (userAgent.IndexOf("Windows NT 6.3", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 8.1";
            if (userAgent.IndexOf("Windows NT 6.2", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 8";
            if (userAgent.IndexOf("Windows NT 6.1", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 7";
            if (userAgent.IndexOf("Mac OS X", StringComparison.OrdinalIgnoreCase) >= 0) return "macOS";
            if (userAgent.IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0) return "Linux";
            if (userAgent.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0) return "Android";
            if (userAgent.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0) return "iOS (iPhone)";
            if (userAgent.IndexOf("iPad", StringComparison.OrdinalIgnoreCase) >= 0) return "iOS (iPad)";
            
            return "Неизвестно";
        }

        /// <summary>
        /// Определяет браузер и его версию из User-Agent строки.
        /// </summary>
        private static string ParseBrowserFromUserAgent(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return "Неизвестно";
            
            // Chrome (должен быть перед Safari, т.к. Chrome содержит Safari)
            int chromeIndex = userAgent.IndexOf("Chrome/", StringComparison.OrdinalIgnoreCase);
            if (chromeIndex >= 0 && userAgent.IndexOf("Edg/") < 0 && userAgent.IndexOf("OPR/") < 0)
            {
                string version = ExtractVersion(userAgent, chromeIndex + 7);
                return $"Google Chrome {version}";
            }
            
            // Edge
            int edgeIndex = userAgent.IndexOf("Edg/", StringComparison.OrdinalIgnoreCase);
            if (edgeIndex >= 0)
            {
                string version = ExtractVersion(userAgent, edgeIndex + 4);
                return $"Microsoft Edge {version}";
            }
            
            // Opera
            int operaIndex = userAgent.IndexOf("OPR/", StringComparison.OrdinalIgnoreCase);
            if (operaIndex >= 0)
            {
                string version = ExtractVersion(userAgent, operaIndex + 4);
                return $"Opera {version}";
            }
            
            // Firefox
            int firefoxIndex = userAgent.IndexOf("Firefox/", StringComparison.OrdinalIgnoreCase);
            if (firefoxIndex >= 0)
            {
                string version = ExtractVersion(userAgent, firefoxIndex + 8);
                return $"Mozilla Firefox {version}";
            }
            
            // Safari (должен быть после Chrome)
            int safariIndex = userAgent.IndexOf("Version/", StringComparison.OrdinalIgnoreCase);
            if (safariIndex >= 0 && userAgent.IndexOf("Safari/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string version = ExtractVersion(userAgent, safariIndex + 8);
                return $"Safari {version}";
            }
            
            // IE
            int ieIndex = userAgent.IndexOf("MSIE ", StringComparison.OrdinalIgnoreCase);
            if (ieIndex >= 0)
            {
                string version = ExtractVersion(userAgent, ieIndex + 5);
                return $"Internet Explorer {version}";
            }
            
            return "Неизвестно";
        }

        /// <summary>
        /// Извлекает номер версии из строки начиная с указанной позиции.
        /// </summary>
        private static string ExtractVersion(string userAgent, int startPos)
        {
            if (startPos >= userAgent.Length) return "";
            
            int endPos = userAgent.IndexOf(' ', startPos);
            if (endPos < 0) endPos = userAgent.IndexOf(';', startPos);
            if (endPos < 0) endPos = userAgent.Length;
            
            return userAgent.Substring(startPos, endPos - startPos);
        }

        /// <summary>
        /// Асинхронно передаёт фиктивный файл клиенту с заданным размером и именем.
        /// </summary>
        /// <param name="response">HTTP-ответ для отправки данных.</param>
        /// <param name="totalSize">Общий размер файла в байтах.</param>
        /// <param name="fileName">Имя файла для заголовка Content-Disposition.</param>
        /// <param name="request">HTTP-запрос клиента.</param>
        private static async Task StreamFakeFileAsync(HttpListenerResponse response, long totalSize, string fileName, HttpListenerRequest request)
        {
            const int bufferSize = 1 * 1024 * 1024; // 1 МБ
            byte[] buffer = new byte[bufferSize];
            byte[] startData = Encoding.UTF8.GetBytes("ТЕСТ");
            byte[] endData = Encoding.UTF8.GetBytes("ТЕСТ");

            response.ContentType = "application/octet-stream";
            response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            response.ContentLength64 = totalSize;

            long sent = 0;
            Array.Clear(buffer, 0, buffer.Length);

            try
            {
                while (sent < totalSize)
                {
                    int toWrite = (int)Math.Min(bufferSize, totalSize - sent);

                    if (sent == 0)
                        Array.Copy(startData, 0, buffer, 0, Math.Min(startData.Length, toWrite));
                    else if (sent + toWrite >= totalSize)
                    {
                        Array.Clear(buffer, 0, toWrite);
                        int endPos = (int)(totalSize - sent - endData.Length);
                        if (endPos >= 0)
                            Array.Copy(endData, 0, buffer, endPos, endData.Length);
                        else
                        {
                            // Если endPos < 0, копируем только часть endData
                            int copyStart = endData.Length + endPos; // отрицательный endPos делает это меньше чем Length
                            if (copyStart < 0) copyStart = 0;
                            Array.Copy(endData, copyStart, buffer, 0, endData.Length - copyStart);
                        }
                    }

                    await response.OutputStream.WriteAsync(buffer, 0, toWrite);
                    sent += toWrite;
                }
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 64)
            {
                Log($"Загрузка прервана клиентом: {request.RemoteEndPoint?.Address} ({sent}/{totalSize} байт)");
                throw;
            }
        }

        /// <summary>
        /// Асинхронно получает DNS-имя хоста по IP-адресу.
        /// </summary>
        /// <param name="ipAddress">IP-адрес для разрешения.</param>
        /// <returns>DNS-имя хоста или IP-адрес, если разрешение не удалось.</returns>
        private static async Task<string> GetHostNameAsync(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return string.Empty;
            try
            {
                if (!ipAddress.Equals("::1") && !ipAddress.StartsWith("127."))
                    return (await Dns.GetHostEntryAsync(ipAddress)).HostName ?? ipAddress;
                return "localhost";
            }
            catch
            {
                return ipAddress;
            }
        }

        /// <summary>
        /// Асинхронно получает информацию о клиенте (DNS-имя и IP-адрес).
        /// </summary>
        /// <param name="request">HTTP-запрос клиента.</param>
        /// <returns>Строка с информацией о клиенте в формате "hostname (ip)".</returns>
        private static async Task<string> GetClientInfoAsync(HttpListenerRequest request)
        {
            string userHost = request.RemoteEndPoint?.Address?.ToString() ?? "Unknown";
            string hostName = await GetHostNameAsync(userHost);
            return $"{hostName} ({userHost})";
        }

        /// <summary>
        /// Асинхронно логирует информацию о завершённом HTTP-запросе.
        /// </summary>
        /// <param name="request">HTTP-запрос.</param>
        /// <param name="response">HTTP-ответ.</param>
        /// <param name="startTime">Время начала обработки запроса.</param>
        /// <param name="description">Описание запроса.</param>
        private static async Task LogRequest(HttpListenerRequest request, HttpListenerResponse response, DateTime startTime, string description)
        {
            try
            {
                var duration = DateTime.Now - startTime;
                string client = await GetClientInfoAsync(request);
                Log($"{client} -> {request.Url.AbsolutePath} ({description}) [{response.StatusCode}] {duration.TotalMilliseconds:F0}ms");
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка логирования запроса", ex);
            }
        }

        /// <summary>
        /// Асинхронно логирует начало загрузки файла.
        /// </summary>
        /// <param name="request">HTTP-запрос клиента.</param>
        /// <param name="fileName">Имя загружаемого файла.</param>
        /// <param name="fileSize">Размер файла в байтах.</param>
        private static async Task LogDownloadStart(HttpListenerRequest request, string fileName, long fileSize)
        {
            try
            {
                string client = await GetClientInfoAsync(request);
                Log($"{client} -> НАЧАЛО загрузки: {fileName} ({fileSize / (1024 * 1024)} MB)");
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка логирования начала загрузки", ex);
            }
        }

        /// <summary>
        /// Асинхронно логирует завершение загрузки файла с расчётом скорости.
        /// </summary>
        /// <param name="request">HTTP-запрос клиента.</param>
        /// <param name="fileName">Имя загруженного файла.</param>
        /// <param name="fileSize">Размер файла в байтах.</param>
        /// <param name="startTime">Время начала загрузки.</param>
        private static async Task LogDownloadEnd(HttpListenerRequest request, string fileName, long fileSize, DateTime startTime)
        {
            try
            {
                var duration = DateTime.Now - startTime;
                double speedMbps = (fileSize * 8.0 / (1024 * 1024)) / duration.TotalSeconds;
                string client = await GetClientInfoAsync(request);
                Log($"{client} -> ЗАВЕРШЕНО: {fileName} ({fileSize / (1024 * 1024)} MB) за {duration.TotalSeconds:F2} секунд ({speedMbps:F2} Mbit/s)");
            }
            catch (Exception ex)
            {
                LogErrorToFile("Ошибка логирования завершения загрузки", ex);
            }
        }

        /// <summary>
        /// Логирует сообщение в консоль и файл журнала.
        /// </summary>
        /// <param name="message">Сообщение для логирования.</param>
        private static void Log(string message)
        {
            string consoleMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(consoleMessage);
            lock (logLock)
            {
                File.AppendAllText(logFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }

        /// <summary>
        /// Выводит сообщение в консоль и логирует в файл (без временной метки в консоли).
        /// </summary>
        /// <param name="message">Сообщение для вывода.</param>
        private static void Print(string message)
        {
            Console.WriteLine(message);
            lock (logLock)
            {
                File.AppendAllText(logFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }

        /// <summary>
        /// Логирует сообщение только в файл журнала.
        /// </summary>
        /// <param name="message">Сообщение для логирования.</param>
        private static void LogToFile(string message)
        {
            try
            {
                lock (logLock)
                {
                    File.AppendAllText(logFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ошибка записи в лог-файл: {ex.Message}");
            }
        }

        /// <summary>
        /// Логирует сообщение об ошибке в консоль и файл журнала с деталями исключения.
        /// </summary>
        /// <param name="message">Описание ошибки.</param>
        /// <param name="ex">Объект исключения.</param>
        private static void LogErrorToFile(string message, Exception ex)
        {
            try
            {
                string errorLog = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ОШИБКА: {message} | {ex.GetType().Name}: {ex.Message} | StackTrace: {ex.StackTrace}";
                Console.Error.WriteLine(errorLog);
                lock (logLock)
                {
                    File.AppendAllText(logFileName, errorLog + Environment.NewLine);
                }
            }
            catch (Exception fileEx)
            {
                Console.Error.WriteLine($"Критическая ошибка записи в лог-файл: {fileEx.Message}");
            }
        }

        /// <summary>
        /// Менеджер банов IP-адресов. Управляет списком забаненных клиентов, отслеживает подозрительную активность.
        /// </summary>
        class BanManager
        {
            private readonly string _banFile;
            private readonly Dictionary<string, DateTime> _bannedClients = new Dictionary<string, DateTime>();
            private readonly Dictionary<string, ClientTracking> _clientTracking = new Dictionary<string, ClientTracking>();
            private readonly object _lock = new object();

            /// <summary>
            /// Инициализирует менеджер банов, загружая список банов из файла.
            /// </summary>
            /// <param name="banFile">Путь к файлу со списком банов.</param>
            public BanManager(string banFile)
            {
                _banFile = banFile;
                LoadBans();
            }

            /// <summary>
            /// Проверяет, забанен ли указанный IP-адрес.
            /// </summary>
            /// <param name="ip">IP-адрес для проверки.</param>
            /// <returns>True, если IP забанен и срок бана не истёк, иначе False.</returns>
            public bool IsBanned(string ip)
            {
                lock (_lock)
                {
                    Log($"Проверка бана для IP: {ip}");
                    if (_bannedClients.TryGetValue(ip, out DateTime banEnd))
                    {
                        if (banEnd > DateTime.Now)
                        {
                            Log($"IP {ip} забанен до {banEnd}");
                            return true;
                        }
                        _bannedClients.Remove(ip);
                        SaveBans();
                    }
                    return false;
                }
            }

            /// <summary>
            /// Банит указанный IP-адрес на заданный срок.
            /// </summary>
            /// <param name="ip">IP-адрес для блокировки.</param>
            /// <param name="duration">Продолжительность бана.</param>
            public void BanClient(string ip, TimeSpan duration)
            {
                lock (_lock)
                {
                    _bannedClients[ip] = DateTime.Now.Add(duration);
                    SaveBans();
                }
            }

            /// <summary>
            /// Получает или создаёт объект отслеживания активности для указанного IP.
            /// </summary>
            /// <param name="ip">IP-адрес клиента.</param>
            /// <returns>Объект ClientTracking для отслеживания активности.</returns>
            public ClientTracking GetOrCreateClientTracking(string ip)
            {
                lock (_lock)
                {
                    if (!_clientTracking.TryGetValue(ip, out ClientTracking tracking))
                    {
                        tracking = new ClientTracking();
                        _clientTracking[ip] = tracking;
                    }
                    return tracking;
                }
            }

            /// <summary>
            /// Загружает список банов из файла.
            /// </summary>
            private void LoadBans()
            {
                try
                {
                    if (File.Exists(_banFile))
                    {
                        var lines = File.ReadAllLines(_banFile);
                        foreach (var line in lines)
                        {
                            var parts = line.Split('|');
                            if (parts.Length == 2 && DateTime.TryParse(parts[1], out DateTime banEnd))
                            {
                                _bannedClients[parts[0]] = banEnd;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка загрузки файла банов: {ex.Message}");
                }
            }

            /// <summary>
            /// Разбанивает указанный IP-адрес, удаляя его из списка банов.
            /// </summary>
            /// <param name="ip">IP-адрес для разбана.</param>
            public void UnbanClient(string ip)
            {
                lock (_lock)
                {
                    if (_bannedClients.Remove(ip))
                    {
                        Log($"IP {ip} удалён из бан-листа");
                        SaveBans();
                    }
                }
            }

            /// <summary>
            /// Сохраняет текущий список банов в файл.
            /// </summary>
            private void SaveBans()
            {
                try
                {
                    var lines = _bannedClients.Select(x => $"{x.Key}|{x.Value:yyyy-MM-dd HH:mm:ss}").ToArray();
                    File.WriteAllLines(_banFile, lines);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка сохранения файла банов: {ex.Message}");
                }
            }

            /// <summary>
            /// Очищает старые записи отслеживания активности для предотвращения утечки памяти.
            /// </summary>
            /// <param name="maxAgeMinutes">Максимальный возраст записей в минутах (по умолчанию 60).</param>
            public void CleanupOldTracking(int maxAgeMinutes = 60)
            {
                lock (_lock)
                {
                    var now = DateTime.Now;
                    var keysToRemove = new List<string>();
                    
                    foreach (var kvp in _clientTracking)
                    {
                        // Если счетчик ошибок был сброшен или запись очень старая - удаляем
                        if (kvp.Value.BadRequestCount == 0)
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                    }
                    
                    foreach (var key in keysToRemove)
                    {
                        _clientTracking.Remove(key);
                    }
                    
                    if (keysToRemove.Count > 0)
                    {
                        Log($"Очищено {keysToRemove.Count} старых записей отслеживания");
                    }
                }
            }
        }

        /// <summary>
        /// Класс для отслеживания активности клиента (счетчики ошибок и т.д.).
        /// </summary>
        class ClientTracking
        {
            /// <summary>Счётчик недопустимых запросов от клиента.</summary>
            public int BadRequestCount { get; set; }
        }
    }
}