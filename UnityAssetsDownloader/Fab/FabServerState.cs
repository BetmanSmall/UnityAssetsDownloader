using System.Text.Json;

/// <summary>
/// Память Fab-этапа сервера (fab/server_state.json): когда в последний раз смотрели раздачу и когда
/// в последний раз звали человека.
///
/// Зачем: сервер заходит на Fab редко и по делу — Cloudflare запоминает частые заходы с одного
/// адреса. И человека зовём не чаще раза в сутки: окно браузера на сервере занимает память, а
/// сообщение, которое приходит каждые 6 часов, быстро перестают читать.
/// </summary>
internal sealed class FabServerState
{
    private sealed class Saved
    {
        public DateTime? LimitedTimeFreeCheckedUtc { get; set; }
        public DateTime? HumanAskedUtc { get; set; }
        public string? HumanReason { get; set; }
        public DateTime? GiveawayEndUtc { get; set; }
        public string? GiveawayUntilText { get; set; }
        public string? GiveawayRemindedFor { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly DateTime? _askedBefore;
    private bool _askedThisRun;
    private bool _changed;

    private FabServerState(string path, Saved saved)
    {
        _path = path;
        LimitedTimeFreeCheckedUtc = saved.LimitedTimeFreeCheckedUtc;
        HumanAskedUtc = saved.HumanAskedUtc;
        HumanReason = saved.HumanReason;
        GiveawayEndUtc = saved.GiveawayEndUtc;
        GiveawayUntilText = saved.GiveawayUntilText;
        GiveawayRemindedFor = saved.GiveawayRemindedFor;
        _askedBefore = saved.HumanAskedUtc;
    }

    /// <summary>Когда в последний раз читали страницу раздачи Limited-Time Free.</summary>
    public DateTime? LimitedTimeFreeCheckedUtc { get; private set; }

    /// <summary>Когда в последний раз звали человека и ответа ещё не было (null — не ждём).</summary>
    public DateTime? HumanAskedUtc { get; private set; }

    public string? HumanReason { get; private set; }

    /// <summary>Когда кончается нынешняя раздача Limited-Time Free (по её надписи «Until … ET»), если удалось разобрать.</summary>
    public DateTime? GiveawayEndUtc { get; private set; }

    public string? GiveawayUntilText { get; private set; }

    /// <summary>Для какой раздачи (по тексту «Until …») уже напомнили о конце.</summary>
    public string? GiveawayRemindedFor { get; private set; }

    public static FabServerState Load(string fabDirectory)
    {
        var path = Path.Combine(fabDirectory, "server_state.json");
        Saved saved;
        try
        {
            saved = File.Exists(path)
                ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) ?? new Saved()
                : new Saved();
        }
        catch
        {
            // Битый файл — не повод падать: начнём с чистого листа.
            saved = new Saved();
        }

        return new FabServerState(path, saved);
    }

    /// <summary>Пора ли снова открыть раздачу: ещё не смотрели или прошло не меньше every.</summary>
    public bool LimitedTimeFreeDue(DateTime nowUtc, TimeSpan every) =>
        LimitedTimeFreeCheckedUtc is not { } last || nowUtc - last >= every;

    public void MarkLimitedTimeFreeChecked(DateTime nowUtc)
    {
        LimitedTimeFreeCheckedUtc = nowUtc;
        _changed = true;
    }

    /// <summary>Запоминает, когда кончается раздача. Новая раздача (другой текст) сбрасывает отметку «напомнили».</summary>
    public void NoteGiveaway(string? untilText, DateTime? endUtc)
    {
        if (string.IsNullOrWhiteSpace(untilText) || (GiveawayUntilText == untilText && GiveawayEndUtc == endUtc))
        {
            return;
        }

        GiveawayUntilText = untilText;
        GiveawayEndUtc = endUtc;
        _changed = true;
    }

    /// <summary>Пора напомнить: до конца раздачи осталось не больше lead, она ещё идёт, и об этой раздаче ещё не напоминали.</summary>
    public bool GiveawayReminderDue(DateTime nowUtc, TimeSpan lead) =>
        GiveawayEndUtc is { } end && nowUtc < end && end - nowUtc <= lead && GiveawayRemindedFor != GiveawayUntilText;

    public void MarkGiveawayReminded()
    {
        GiveawayRemindedFor = GiveawayUntilText;
        _changed = true;
    }

    /// <summary>
    /// Можно ли звать человека. Да — если в этом прогоне уже звали (разговор идёт) или в прошлый
    /// раз звали не меньше quiet назад (или никогда). Иначе человек уже в курсе и пока не пришёл.
    /// </summary>
    public bool CanAskHuman(DateTime nowUtc, TimeSpan quiet) =>
        _askedThisRun || _askedBefore is not { } before || nowUtc - before >= quiet;

    /// <summary>Отмечает, что человека позвали сейчас.</summary>
    public void MarkAsked(DateTime nowUtc, string reason)
    {
        _askedThisRun = true;
        HumanAskedUtc = nowUtc;
        HumanReason = reason;
        _changed = true;
    }

    /// <summary>Человек откликнулся, и всё, что требовало его, сделано: снова можно звать, когда понадобится.</summary>
    public void ClearAsked()
    {
        if (HumanAskedUtc is null && HumanReason is null)
        {
            return;
        }

        HumanAskedUtc = null;
        HumanReason = null;
        _changed = true;
    }

    public void Save()
    {
        if (!_changed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Saved
            {
                LimitedTimeFreeCheckedUtc = LimitedTimeFreeCheckedUtc,
                HumanAskedUtc = HumanAskedUtc,
                HumanReason = HumanReason,
                GiveawayEndUtc = GiveawayEndUtc,
                GiveawayUntilText = GiveawayUntilText,
                GiveawayRemindedFor = GiveawayRemindedFor
            }, Json));
            _changed = false;
        }
        catch
        {
            // Не сохранилось — в следующий раз просто спросим заново.
        }
    }
}
