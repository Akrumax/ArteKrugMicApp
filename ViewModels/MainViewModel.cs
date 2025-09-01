using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vosk;

namespace ArteKrugMicApp.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    // ========== Публичные свойства для привязки UI ==========
    public ObservableCollection<string> Microphones { get; } = new ObservableCollection<string>();

    [ObservableProperty]
    private string? _selectedMicrophone;

    public ObservableCollection<string> Languages { get; } = new ObservableCollection<string> { "Русский", "English" };

    [ObservableProperty]
    private string _selectedLanguage = "Русский";

    public ObservableCollection<string> Genders { get; } = new ObservableCollection<string> { "Женский", "Мужской" };

    [ObservableProperty]
    private string _selectedGender = "Мужской";

    [ObservableProperty]
    private bool _isEnabled = false;
    partial void OnIsEnabledChanged(bool value)
    {
        if (value) _ = StartProcessingAsync();
        else StopProcessing();
    }

    [ObservableProperty]
    private string _status = "Готов";

    // ========== Внутренние поля ==========
    private readonly MMDeviceEnumerator _deviceEnumerator = new MMDeviceEnumerator();

    // Захват аудио и буферизация
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _bufferedProvider;
    private MMDevice? _captureDevice;

    // STT (Vosk)
    private Model? _voskModel;
    private VoskRecognizer? _recognizer;
    private const int TARGET_SR = 16000; // Частота дискретизации для Vosk

    // Очередь TTS и поток
    private readonly ConcurrentQueue<string> _ttsQueue = new ConcurrentQueue<string>();
    private Thread? _ttsThread;
    private volatile bool _ttsThreadRun = false;

    // Воспроизведение (в виртуальный кабель, пока я не сделал)
    private WasapiOut? _playOut;
    private MMDevice? _playDevice;

    // Фоновый поток для STT
    private Thread? _sttThread;
    private volatile bool _sttThreadRun = false;

    // Папка моделей (путь относительно исполняемого exe)
    private readonly string _modelsFolder;

    // Диагностический дамп в WAV (после ресемплинга в 16k PCM16 моно, с дампом помогла ИИ)
    // По умолчанию включено для диагностики; после отладки можно сделать false.
    private readonly bool _enableDebugDump = true;
    private WaveFileWriter? _debugWriter;

    // Для обработки partial и накопленного финального текста
    private string _lastPartial = "";
    private DateTime _lastPartialTime = DateTime.MinValue;
    private string _finalRecognizedText = "";

    [ObservableProperty]
    private string _recognizedText = "";

    [RelayCommand]
    private void OpenEspeakFolder()
    {
        try
        {
            string path = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PostStatus("Не удалось открыть папку приложения: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenModelsFolder()
    {
        try
        {
            string modelsPath = _modelsFolder ?? Path.Combine(AppContext.BaseDirectory, "models");
            if (!Directory.Exists(modelsPath))
                Directory.CreateDirectory(modelsPath);
            Process.Start(new ProcessStartInfo { FileName = modelsPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PostStatus("Не удалось открыть папку моделей: " + ex.Message);
        }
    }

    public MainViewModel()
    {
        _modelsFolder = Path.Combine(AppContext.BaseDirectory, "models");
        PopulateDevices();
        StartTtsThread();
        Status = "Готов. Проверьте models\\ и наличие espeak-ng.exe рядом с приложением.";
        Debug.WriteLine($"Папка приложения: {AppContext.BaseDirectory}");
    }

    // Перечисление устройств захвата и заполнение коллекции Microphones
    private void PopulateDevices()
    {
        try
        {
            var devs = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            Microphones.Clear();
            foreach (var d in devs)
            {
                Microphones.Add(d.FriendlyName);
            }
            if (Microphones.Count > 0)
                SelectedMicrophone = Microphones[0];

            Debug.WriteLine($"Найдено устройств захвата: {Microphones.Count}");
        }
        catch (Exception ex)
        {
            Status = "Ошибка перечисления устройств: " + ex.Message;
            Debug.WriteLine("Исключение при перечислении устройств: " + ex);
        }
    }

    // ================== Запуск / Остановка обработки аудиопотока с микрофона ==================
    private async Task StartProcessingAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _StartProcessingBackground();
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => { Status = "Ошибка старта: " + ex.Message; IsEnabled = false; });
                CleanupAfterStop();
                Debug.WriteLine("Исключение в StartProcessingAsync: " + ex);
            }
        });
    }

    // Вспомогательный метод: выполняется в фоновом потоке
    private void _StartProcessingBackground()
    {
        Dispatcher.UIThread.Post(() => Status = "Инициализация...");

        // Проверяем модель Vosk по выбранному языку
        string language = SelectedLanguage.StartsWith("Рус") ? "ru" : "en";
        string modelName = language == "ru" ? "vosk-model-small-ru-0.22" : "vosk-model-small-en-us-0.15";
        string pathModel = Path.Combine(_modelsFolder, modelName);
        if (!Directory.Exists(pathModel))
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = $"Модель {modelName} не найдена в {_modelsFolder}. Поместите модель и повторите.";
                IsEnabled = false;
            });
            Debug.WriteLine($"Папка модели не найдена: {pathModel}");
            return;
        }

        try
        {
            Debug.WriteLine($"Загрузка модели Vosk из: {pathModel}...");
            _voskModel = new Model(pathModel);
            _recognizer = new VoskRecognizer(_voskModel, TARGET_SR);
            _recognizer.SetMaxAlternatives(0);
            _recognizer.SetWords(false);
            Debug.WriteLine("Модель Vosk успешно загружена.");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = "Ошибка загрузки модели Vosk: " + ex.Message;
                IsEnabled = false;
            });
            Debug.WriteLine("Исключение при загрузке модели Vosk: " + ex);
            return;
        }

        // Подготовка устройства захвата по имени
        var list = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var selected = list.FirstOrDefault(d => d.FriendlyName == SelectedMicrophone);
        if (selected == null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = "Не выбран микрофон либо устройство недоступно.";
                IsEnabled = false;
            });
            Debug.WriteLine($"Выбранный микрофон '{SelectedMicrophone}' не найден среди устройств.");
            return;
        }
        _captureDevice = selected;

        try
        {
            // Создаём WasapiCapture для выбранного устройства (shared mode)
            _capture = new WasapiCapture(_captureDevice);
            Debug.WriteLine($"Создан WasapiCapture. Формат устройства: {_capture.WaveFormat.SampleRate} Гц, {_capture.WaveFormat.Channels} канал(ов), {_capture.WaveFormat.BitsPerSample} бит");
            // Создаём BufferedWaveProvider с форматом устройства
            _bufferedProvider = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferLength = _capture.WaveFormat.AverageBytesPerSecond * 5 // 5 сек буфер
            };

            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
            _capture.StartRecording();
            Debug.WriteLine("Захват запущен.");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = "Ошибка инициализации захвата: " + ex.Message;
                IsEnabled = false;
            });
            Debug.WriteLine("Исключение при инициализации захвата: " + ex);
            return;
        }

        // Подготовка устройства воспроизведения — ищем VB-Cable (CABLE Input) - пока лишь поиск установленног овнешнег одарйвера, сам синтез речи ещё не сделан
        var renderDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        _playDevice = renderDevices.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input") || d.FriendlyName.Contains("VB-Audio"));
        if (_playDevice != null)
        {
            _playOut = new WasapiOut(_playDevice, AudioClientShareMode.Shared, true, 200);
            Debug.WriteLine($"Воспроизведение инициализировано на устройстве: {_playDevice.FriendlyName}");
        }
        else
        {
            var defaultDev = renderDevices.FirstOrDefault();
            if (defaultDev != null)
            {
                _playOut = new WasapiOut(defaultDev, AudioClientShareMode.Shared, true, 200);
                Debug.WriteLine($"Воспроизведение: резервное устройство по умолчанию: {defaultDev.FriendlyName}");
            }
            else
            {
                Debug.WriteLine("Не найдено устройств воспроизведения для вывода.");
            }
        }

        // Если включён debug dump, создаём WaveFileWriter для проверки того, что подаём в Vosk
        if (_enableDebugDump)
        {
            try
            {
                string dumpPath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "debug_input_16k_mono.wav");
                // Перезапиьс файла
                if (File.Exists(dumpPath)) File.Delete(dumpPath);
                _debugWriter = new WaveFileWriter(dumpPath, new WaveFormat(TARGET_SR, 16, 1));
                Debug.WriteLine("Инициализирован debug дамп: " + dumpPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Не удалось инициализировать debug дамп: " + ex);
                _debugWriter = null;
            }
        }

        // Запуск фонового потока STT
        _sttThreadRun = true;
        _sttThread = new Thread(SttWorker) { IsBackground = true };
        _sttThread.Start();

        Dispatcher.UIThread.Post(() => Status = "Распознавание запущено.");
    }

    private void StopProcessing()
    {
        try
        {
            Status = "Остановка...";
            // Остановка захвата
            if (_capture != null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.StopRecording();
            }

            // Остановка STT-потока
            _sttThreadRun = false;
            _sttThread?.Join(500);

            // Остановка TTS-потока аккуратно
            Thread.Sleep(200);

            _ttsThreadRun = false;
            _ttsThread?.Join(500);

            CleanupAfterStop();
            Status = "Остановлено.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка остановки: " + ex.Message;
            Debug.WriteLine("Исключение при остановке: " + ex);
        }
    }

    private void CleanupAfterStop()
    {
        try
        {
            _capture?.Dispose();
            _capture = null;
            _bufferedProvider = null;

            _recognizer?.Dispose();
            _recognizer = null;
            _voskModel?.Dispose();
            _voskModel = null;

            _playOut?.Dispose();
            _playOut = null;
            _playDevice = null;
            _captureDevice = null;

            // Закрываем debug writer, если есть
            try
            {
                _debugWriter?.Dispose();
                _debugWriter = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Ошибка при освобождении debug writer: " + ex);
            }

            _finalRecognizedText = "";
            _lastPartial = "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Исключение в CleanupAfterStop: " + ex);
        }
    }

    // ================== Обработчик захвата ==================
    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            Debug.WriteLine($"Capture_DataAvailable: получено байт = {e.BytesRecorded}.");
            if (_capture != null)
            {
                Debug.WriteLine($"Формат устройства: {_capture.WaveFormat.SampleRate} Гц, {_capture.WaveFormat.Channels} канал(ов), {_capture.WaveFormat.BitsPerSample} бит");
            }
            _bufferedProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Debug.WriteLine($"Буферировано байт после добавления: {_bufferedProvider?.BufferedBytes ?? 0}");
        }
        catch (Exception ex)
        {
            PostStatus("Ошибка записи в буфер захвата: " + ex.Message);
            Debug.WriteLine("Исключение в Capture_DataAvailable: " + ex);
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        PostStatus("Запись остановлена.");
        Debug.WriteLine("Capture_RecordingStopped: " + (e.Exception != null ? e.Exception.ToString() : "без исключения"));
    }

    // ================== Фоновый поток STT ==================
    private void SttWorker()
    {
        Debug.WriteLine("Фоновый STT-поток запущен.");
        while (_sttThreadRun)
        {
            try
            {
                if (_bufferedProvider == null || _recognizer == null)
                {
                    Thread.Sleep(30);
                    continue;
                }

                var inFormat = _bufferedProvider.WaveFormat;
                ISampleProvider sampleProvider = _bufferedProvider.ToSampleProvider();

                // Преобразование в моно
                if (inFormat.Channels == 2)
                {
                    sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    };
                }
                else if (inFormat.Channels != 1)
                {
                    PostStatus("Устройство выдаёт более 2 каналов — требуется дополнительная обработка.");
                    Debug.WriteLine($"Неподдерживаемое количество каналов: {inFormat.Channels}");
                    Thread.Sleep(200);
                    continue;
                }

                // Ресемплинг
                if (inFormat.SampleRate != TARGET_SR)
                {
                    Debug.WriteLine($"Выполняется ресемплинг с {inFormat.SampleRate} до {TARGET_SR}");
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TARGET_SR);
                }

                // Читаем блоки
                int blockSamples = TARGET_SR / (int)(TARGET_SR / 100 * 1.25);
                float[] floatBuffer = new float[blockSamples];
                byte[] byteBuffer = new byte[blockSamples * 2];

                int read = sampleProvider.Read(floatBuffer, 0, blockSamples);
                if (read == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Вычисляем максимальную абсолютную амплитуду для диагностики громкости речи из аудиопотока
                float maxAbs = 0f;
                for (int i = 0; i < read; i++)
                {
                    float a = Math.Abs(floatBuffer[i]);
                    if (a > maxAbs) maxAbs = a;
                }
                Debug.WriteLine($"STT: прочитано семплов={read}, макс амплитуда={maxAbs:0.00000}");

                // Конвертация float в PCM16LE
                int bytesToSend = 0;
                for (int i = 0; i < read; i++)
                {
                    float sample = floatBuffer[i];
                    if (sample > 1f) sample = 1f;
                    if (sample < -1f) sample = -1f;
                    short s = (short)(sample * short.MaxValue);
                    byteBuffer[bytesToSend++] = (byte)(s & 0xff);
                    byteBuffer[bytesToSend++] = (byte)((s >> 8) & 0xff);
                }

                // Если включён дамп, записываем PCM16 в debug WAV, спасибо ИИ - можно прослушать, как нас "слышало" приложение: файл создаётся в папке
                try
                {
                    if (_debugWriter != null && bytesToSend > 0)
                    {
                        _debugWriter.Write(byteBuffer, 0, bytesToSend);
                        _debugWriter.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Исключение при записи в debugWriter: " + ex);
                }

                Debug.WriteLine($"STT: байт для отправки={bytesToSend}");

                bool accepted = false;
                try
                {
                    accepted = _recognizer.AcceptWaveform(byteBuffer, bytesToSend);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Исключение в AcceptWaveform: " + ex);
                }

                if (accepted)
                {
                    string json = _recognizer.Result();
                    string text = ParseVoskJson(json, preferPartial: false);
                    Debug.WriteLine($"Vosk Result JSON: {json}");

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _finalRecognizedText = string.IsNullOrEmpty(_finalRecognizedText) ? text : (_finalRecognizedText + " " + text);
                        EnqueueTts(text);
                        PostStatus($"[result] {text}");
                        Dispatcher.UIThread.Post(() =>
                        {
                            RecognizedText = _finalRecognizedText;
                        });
                    }
                    _lastPartial = "";
                }
                else
                {
                    string partialJson = _recognizer.PartialResult();
                    string p = ParseVoskJson(partialJson, preferPartial: true);
                    Debug.WriteLine($"Vosk Partial JSON: {partialJson} -> '{p}'");
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var now = DateTime.UtcNow;
                        if (p != _lastPartial || (now - _lastPartialTime).TotalMilliseconds > 400)
                        {
                            _lastPartial = p;
                            _lastPartialTime = now;

                            Dispatcher.UIThread.Post(() =>
                            {
                                RecognizedText = string.IsNullOrEmpty(_finalRecognizedText) ? p : (_finalRecognizedText + " [" + p + "]");
                            });

                            if (p.Length > 3)
                            {
                                EnqueueTts(p);
                                PostStatus($"[partial] {p}");
                            }
                        }
                    }
                }

                // Если сигнал слишком мал (тишина или тихо говорим), показываем подсказку
                if (maxAbs < 0.005f)
                {
                    // Возможно, низкий уровень микрофона или неправильное устройство
                    PostStatus("Внимание: сигнал очень мал (почти тишина). Проверьте уровень микрофона в Windows.");
                }

            }
            catch (Exception ex)
            {
                PostStatus("Ошибка в STT-потоке: " + ex.Message);
                Debug.WriteLine("Исключение в SttWorker: " + ex);
                Thread.Sleep(200);
            }
        }
        Debug.WriteLine("Фоновый STT-поток остановлен.");
    }

    // ================== Очередь TTS + рабочий поток ==================
    private void StartTtsThread()
    {
        _ttsThreadRun = true;
        _ttsThread = new Thread(TtsWorker) { IsBackground = true };
        _ttsThread.Start();
    }

    private void TtsWorker()
    {
        Debug.WriteLine("Фоновый TTS-поток запущен.");
        while (_ttsThreadRun)
        {
            if (_ttsQueue.TryDequeue(out string text))
            {
                try
                {
                    string lang = SelectedLanguage.StartsWith("Рус") ? "ru" : "en";
                    bool female = SelectedGender == "Женский";
                    string voiceArg = lang == "ru" ? (female ? "ru+f3" : "ru+m1") : (female ? "en+f3" : "en+m2");

                    string espeakExe = Path.Combine(AppContext.BaseDirectory, "espeak-ng.exe");
                    if (!File.Exists(espeakExe))
                    {
                        string espeakFromPath = TryFindExecutableInPath("espeak-ng.exe");
                        if (!string.IsNullOrEmpty(espeakFromPath))
                        {
                            espeakExe = espeakFromPath;
                            Debug.WriteLine($"espeak-ng найден в PATH: {espeakExe}");
                        }
                        else
                        {
                            PostStatus("espeak-ng.exe не найден рядом с приложением и не найден в PATH. Поместите espeak-ng.exe рядом с exe или добавьте в PATH.");
                            Thread.Sleep(500);
                            continue;
                        }
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = espeakExe,
                        Arguments = $"--stdout -v {voiceArg}",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi)!)
                    {
                        using (var sw = proc.StandardInput)
                        {
                            sw.Write(text);
                        }

                        using (var ms = new MemoryStream())
                        {
                            try
                            {
                                proc.StandardOutput.BaseStream.CopyTo(ms);
                                string err = proc.StandardError.ReadToEnd();
                                if (!string.IsNullOrEmpty(err))
                                {
                                    Debug.WriteLine("espeak-ng stderr: " + err);
                                }
                                proc.WaitForExit(5000);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("Ошибка чтения stdout у espeak-ng: " + ex);
                                PostStatus("Ошибка при вызове espeak-ng: " + ex.Message);
                                continue;
                            }

                            ms.Position = 0;

                            try
                            {
                                if (ms.Length == 0)
                                {
                                    Debug.WriteLine("espeak-ng вернул пустой stdout (нет wav). Проверьте версию espeak-ng и аргументы голосов.");
                                    PostStatus("espeak-ng не вернул аудио. Проверьте поддержку голосов и права на запуск espeak-ng.");
                                    continue;
                                }

                                using (var reader = new WaveFileReader(ms))
                                {
                                    Debug.WriteLine($"Воспроизведение TTS: {text} (длина wav {ms.Length} байт, частота {reader.WaveFormat.SampleRate}, каналы {reader.WaveFormat.Channels})");
                                    if (_playOut != null)
                                    {
                                        _playOut.Init(reader);
                                        _playOut.Play();
                                        while (_playOut.PlaybackState == PlaybackState.Playing)
                                            Thread.Sleep(10);
                                    }
                                    else
                                    {
                                        using (var wo = new WaveOutEvent())
                                        {
                                            ms.Position = 0;
                                            using (var r2 = new WaveFileReader(ms))
                                            {
                                                wo.Init(r2);
                                                wo.Play();
                                                while (wo.PlaybackState == PlaybackState.Playing)
                                                    Thread.Sleep(10);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                PostStatus("Ошибка воспроизведения TTS: " + ex.Message);
                                Debug.WriteLine("Исключение при воспроизведении TTS: " + ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PostStatus("Ошибка в TTS-воркере: " + ex.Message);
                    Debug.WriteLine("Исключение в TTS worker: " + ex);
                }
            }
            else
            {
                Thread.Sleep(20);
            }
        }
        Debug.WriteLine("Фоновый TTS-поток остановлен.");
    }

    // Добавляет текст в очередь для синтеза
    private void EnqueueTts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Trim().Length < 2) return;
        _ttsQueue.Enqueue(text.Trim());
    }

    // ================== Вспомогательные функции ==================
    private static string ParseVoskJson(string json, bool preferPartial)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            var j = JObject.Parse(json);
            if (preferPartial)
            {
                if (j.TryGetValue("partial", out var p)) return p.ToString();
            }
            if (j.TryGetValue("text", out var t)) return t.ToString();
            if (j.TryGetValue("partial", out var p2)) return p2.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Исключение при разборе JSON от Vosk: " + ex);
        }
        return string.Empty;
    }

    private void PostStatus(string s)
    {
        Debug.WriteLine("Статус: " + s);
        Dispatcher.UIThread.Post(() => { Status = s; });
    }

    // Попытка найти исполняемый файл в PATH (для запуска espeak-ng.exe)
    private static string? TryFindExecutableInPath(string exeName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            try
            {
                var candidate = Path.Combine(p, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            _sttThreadRun = false;
            _sttThread?.Join(500);

            _ttsThreadRun = false;
            _ttsThread?.Join(500);

            _capture?.Dispose();
            _playOut?.Dispose();
            _recognizer?.Dispose();
            _voskModel?.Dispose();

            _debugWriter?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Исключение в Dispose: " + ex);
        }
    }

    // ================== Тестовый метод: проверка модели Vosk на локальном WAV (если желаем проверить готовый аудиофайл с подготовленной речью) ==================
    public void TestVoskWithWav(string wavPath)
    {
        try
        {
            if (_voskModel == null)
            {
                Debug.WriteLine("TestVoskWithWav: модель отсутствует.");
                return;
            }
            if (!File.Exists(wavPath))
            {
                Debug.WriteLine("TestVoskWithWav: wav-файл не найден: " + wavPath);
                return;
            }

            Debug.WriteLine("TestVoskWithWav: запуск теста для " + wavPath);
            using (var reader = new WaveFileReader(wavPath))
            {
                int sampleRate = reader.WaveFormat.SampleRate;
                Debug.WriteLine($"WAV частота дискретизации: {sampleRate}, каналы: {reader.WaveFormat.Channels}");
            }

            using (var rec = new VoskRecognizer(_voskModel, TARGET_SR))
            {
                using (var wave = new WaveFileReader(wavPath))
                {
                    byte[] buffer = new byte[4096];
                    int bytes;
                    while ((bytes = wave.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (rec.AcceptWaveform(buffer, bytes))
                        {
                            var r = rec.Result();
                            Debug.WriteLine("Тестовый результат: " + r);
                        }
                        else
                        {
                            var pr = rec.PartialResult();
                            Debug.WriteLine("Тестовый partial: " + pr);
                        }
                    }
                    Debug.WriteLine("Тестовый финал: " + rec.FinalResult());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Исключение в TestVoskWithWav: " + ex);
            PostStatus("Ошибка TestVoskWithWav: " + ex.Message);
        }
    }
}
