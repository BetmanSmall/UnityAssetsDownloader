/// <summary>
/// Что служба делает сейчас — для /status и для просьбы «открыть окно Fab» (этап B плана «Окно Fab», 13.10). Пишет цикл
/// службы, читает приёмник бота из своего потока, поэтому всё под замком. Сон службы прерывается просьбой (Wake): человек
/// нажал «Открыть окно» и ждёт, а до следующего прогона может быть 12 часов.
/// </summary>
internal sealed class ServiceStatus
{
    private readonly object _sync = new();
    private TaskCompletionSource _wake = NewWake();

    public string Activity { get; private set; } = "запускается";
    public DateTime Since { get; private set; } = DateTime.Now;

    /// <summary>Занята прогоном (обычным, ночным, окном Fab); false — спит до следующего.</summary>
    public bool Busy { get; private set; } = true;

    public DateTime? NextRegular { get; private set; }
    public DateTime? WakeAt { get; private set; }

    /// <summary>Человек попросил окно Fab, а сеанс ещё не начался.</summary>
    public bool FabRequested { get; private set; }

    /// <summary>Сеанс Fab по просьбе идёт.</summary>
    public bool FabRunning { get; private set; }

    /// <summary>В последней принятой просьбе человек просил ссылку для телефона (/phone, «📱 С телефона»).</summary>
    public bool LastRequestPhone { get; private set; }

    private bool _phoneRequested;

    public void SetBusy(string activity)
    {
        lock (_sync)
        {
            Activity = activity;
            Since = DateTime.Now;
            Busy = true;
        }
    }

    public void SetSleeping(DateTime wakeAt, DateTime nextRegular)
    {
        lock (_sync)
        {
            Activity = "спит";
            Since = DateTime.Now;
            Busy = false;
            WakeAt = wakeAt;
            NextRegular = nextRegular;
        }
    }

    /// <summary>
    /// Человек просит окно Fab (/fab или кнопка). Возвращает ответ ему; спящую службу будит. Повторные нажатия, пока
    /// просьба в очереди или сеанс идёт, ничего не добавляют.
    /// </summary>
    public string RequestFab(bool phone = false)
    {
        lock (_sync)
        {
            if (FabRunning)
            {
                return phone
                    ? "Окно Fab уже открывается. Понадобится галочка — нажмите «📱 С телефона» под сообщением с окном."
                    : "Окно Fab уже открывается или открыто — ссылка придёт (или уже пришла) отдельным сообщением.";
            }

            _phoneRequested |= phone;

            if (FabRequested)
            {
                return Busy
                    ? $"Уже в очереди: окно Fab откроется сразу после того, что идёт сейчас ({Activity}, с {Since:HH:mm})."
                    : "Уже открываю окно Fab — сообщение придёт через минуту.";
            }

            FabRequested = true;
            if (Busy)
            {
                return $"Сейчас идёт {Activity} (с {Since:HH:mm}) — окно Fab открою сразу после него и напишу.";
            }

            _wake.TrySetResult();
        }

        return "Открываю Fab. Понадобится галочка или вход — пришлю ссылку на окно и кнопку «Готово» (обычно меньше минуты).";
    }

    /// <summary>Есть просьба — сеанс начинается (true), просьба снимается.</summary>
    public bool TakeFabRequest()
    {
        lock (_sync)
        {
            if (!FabRequested)
            {
                return false;
            }

            FabRequested = false;
            LastRequestPhone = _phoneRequested;
            _phoneRequested = false;
            FabRunning = true;
            Activity = "окно Fab по вашей просьбе";
            Since = DateTime.Now;
            Busy = true;
            return true;
        }
    }

    public void FabFinished()
    {
        lock (_sync)
        {
            FabRunning = false;
        }
    }

    /// <summary>Спит pause или до просьбы человека (Wake). false — службу останавливают.</summary>
    public async Task<bool> SleepAsync(TimeSpan pause, CancellationToken stop)
    {
        Task wake;
        lock (_sync)
        {
            if (FabRequested)
            {
                return !stop.IsCancellationRequested;
            }

            _wake = NewWake();
            wake = _wake.Task;
        }

        try
        {
            await Task.WhenAny(Task.Delay(pause, stop), wake);
        }
        catch (OperationCanceledException)
        {
            // остановка
        }

        return !stop.IsCancellationRequested;
    }

    /// <summary>Строка «что делаю» для /status.</summary>
    public string Describe()
    {
        lock (_sync)
        {
            var now = Busy
                ? $"идёт {Activity} (с {Since:HH:mm})"
                : WakeAt is { } w ? $"спит до {w:dd.MM HH:mm}" : "спит";
            var next = NextRegular is { } n ? $"; следующий обычный прогон — {n:dd.MM HH:mm}" : string.Empty;
            var fab = FabRequested ? "; окно Fab в очереди — откроется после текущего" : string.Empty;
            return now + next + fab;
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
