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
    class Program
    {
        private static readonly object nightModeLock = new object();
        private static bool isInSleepMode = false;
        private static bool isForceRunRequested = false;

        private static readonly long[] FileSizes = { 100 * 1024L * 1024, 200 * 1024L * 1024, 500 * 1024L * 1024, 1000 * 1024L * 1024 };
        private static bool isNightModeEnabled = true;
        private static int nightStartHour = 1;
        private static int nightEndHour = 6;
        private static readonly string[] WhiteListedPaths = { "/", "/favicon.ico", "/download/100", "/download/200", "/download/500", "/download/1000", "/748_dark" };
        private static string logFileName;
        private static readonly object logLock = new object();
        private static BanManager banManager;
        private static List<string> randomHeaders = new List<string>();
        // Используем ThreadLocal для потокобезопасного Random на .NET Framework 4.8
        private static readonly ThreadLocal<Random> random = new ThreadLocal<Random>(() => new Random());
        private static readonly object headerLock = new object();
        private static CancellationTokenSource serverCts = new CancellationTokenSource();
        private static HttpListener listener;
        private static readonly object listenerLock = new object();

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

        private static string GetRandomHeader()
        {
            lock (headerLock)
            {
                if (randomHeaders.Count == 0)
                    return "FILE NOT FOUND";
                return randomHeaders[random.Value.Next(randomHeaders.Count)];
            }
        }
        static async Task Main(string[] args)
        {
            Thread.Sleep(1500);

            var startTime = DateTime.Now;

            logFileName = $"server_log_{startTime:yyyy-MM-dd_HH-mm-ss}.txt";

            Log($"Программа запущена: {startTime:yyyy-MM-dd HH:mm:ss}");

            banManager = new BanManager("bans.ini");
            LoadRandomHeaders();

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

            _ = Task.Run(CheckNightModeAsync);

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
            lock (listenerLock)
            {
                if (listener != null && listener.IsListening)
                {
                    listener.Stop();
                    listener.Close();
                }
            }
        }

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
                    // Проверяем наличие секретного кода "748_dark" в User-Agent, QueryString или пути
                    bool hasSecretCode = (request.UserAgent?.Contains("748_dark") == true) ||
                                        (request.Url.Query?.Contains("748_dark") == true) ||
                                        (request.Url.AbsolutePath == "/748_dark");

                    if (hasSecretCode)
                    {
                        Log($"Секретный код '748_dark' обнаружен, IP {clientIp} разбанен");

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
                        banManager.BanClient(clientIp, TimeSpan.FromDays(200000));
                        Log($"КЛИЕНТ ЗАБАНЕН: {clientIp} на 200000 лет");

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
                    response.OutputStream?.Close();
                    response.Close();
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


        private static bool IsWhiteListed(string path)
        {
            Log($"Проверка белого списка: {path}");
            
            // Проверяем точное совпадение с путями из белого списка
            if (WhiteListedPaths.Contains(path))
            {
                Log($"Путь {path} в белом списке");
                return true;
            }

            Log($"Путь {path} НЕ в белом списке");
            return false;
        }

        private static bool IsSuspiciousRequest(HttpListenerRequest request)
        {
            string userAgent = request.UserAgent?.ToLower() ?? "";
            Log($"Проверка User-Agent: {userAgent}");

            if (userAgent.Contains("curl") ||
                userAgent.Contains("wget") ||
                userAgent.Contains("python") ||
                userAgent.Contains("scanner") ||
                userAgent.Contains("bot") ||
                userAgent.Contains("spider") ||
                userAgent.Contains("crawler") ||
                userAgent.Length == 0)
            {
                Log($"Запрос помечен как подозрительный");
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
                    string page = GenerateIndexPage();
                    byte[] buffer = Encoding.UTF8.GetBytes(page);

                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    await LogRequest(request, response, startTime, "Главная страница");
                }
                else if (request.Url.AbsolutePath.StartsWith("/download/"))
                {
                    string sizePart = request.Url.AbsolutePath.Substring("/download/".Length);
                    if (int.TryParse(sizePart, out int megabytes))
                    {
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

        private static bool IsLocalAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip == "::1" || ip == "127.0.0.1" || ip == "localhost") return true;
            if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.") || ip.StartsWith("172.19.") || ip.StartsWith("172.20.") || ip.StartsWith("172.21.") || ip.StartsWith("172.22.") || ip.StartsWith("172.23.") || ip.StartsWith("172.24.") || ip.StartsWith("172.25.") || ip.StartsWith("172.26.") || ip.StartsWith("172.27.") || ip.StartsWith("172.28.") || ip.StartsWith("172.29.") || ip.StartsWith("172.30.") || ip.StartsWith("172.31.")) return true;
            if (ip.StartsWith("2.2.2.")) return true;
            if (ip.StartsWith("fe80::") || ip.StartsWith("::ffff:127.0.0.1")) return true;
            return false;
        }

        private static string GenerateIndexPage()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"ru\"><head>")
              .Append("<meta charset=\"utf-8\"/>")
              .Append("<title>Fake Download Server</title></head><body>")
              .Append("<h1>Фейковые файлы для теста скорости</h1><ul>");

            foreach (var size in FileSizes)
            {
                int mb = (int)(size / (1024 * 1024));
                sb.Append($"<li><a href=\"/download/{mb}\">{mb} МБ</a></li>");
            }

            sb.Append("</ul></body></html>");
            return sb.ToString();
        }

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

        private static async Task<string> GetClientInfoAsync(HttpListenerRequest request)
        {
            string userHost = request.RemoteEndPoint?.Address?.ToString() ?? "Unknown";
            string hostName = await GetHostNameAsync(userHost);
            return $"{hostName} ({userHost})";
        }

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

        private static void Log(string message)
        {
            string consoleMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(consoleMessage);
            lock (logLock)
            {
                File.AppendAllText(logFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }

        private static void Print(string message)
        {
            Console.WriteLine(message);
            lock (logLock)
            {
                File.AppendAllText(logFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }

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

        class BanManager
        {
            private readonly string _banFile;
            private readonly Dictionary<string, DateTime> _bannedClients = new Dictionary<string, DateTime>();
            private readonly Dictionary<string, ClientTracking> _clientTracking = new Dictionary<string, ClientTracking>();
            private readonly object _lock = new object();

            public BanManager(string banFile)
            {
                _banFile = banFile;
                LoadBans();
            }

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

            public void BanClient(string ip, TimeSpan duration)
            {
                lock (_lock)
                {
                    _bannedClients[ip] = DateTime.Now.Add(duration);
                    SaveBans();
                }
            }

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

            // Очистка старых записей отслеживания для предотвращения утечки памяти
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

        class ClientTracking
        {
            public int BadRequestCount { get; set; }
        }
    }
}