/// <summary>Аргументы разовых прогонов, которые служба запускает сама (окно Fab по просьбе человека).</summary>
internal static class ServiceArgs
{
    /// <summary>Аргументы службы без --watch и её расписания, плюс extra.</summary>
    public static string[] OneShot(IReadOnlyList<string> serviceArgs, params string[] extra)
    {
        var result = new List<string>();
        for (var i = 0; i < serviceArgs.Count; i++)
        {
            if (serviceArgs[i].Equals("--watch-interval", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (!serviceArgs[i].Equals("--watch", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(serviceArgs[i]);
            }
        }

        result.AddRange(extra);
        return result.ToArray();
    }

    /// <summary>
    /// Сеанс Fab по просьбе человека: серверный этап Fab, человек у экрана (--fab-on-call); phone — он с телефона: ссылку
    /// без туннеля прислать сразу, как откроется окно (--fab-phone).
    /// </summary>
    public static string[] FabOnCall(IReadOnlyList<string> serviceArgs, bool phone = false) => phone
        ? OneShot(serviceArgs, "--fab-server", "--fab-on-call", "--fab-phone")
        : OneShot(serviceArgs, "--fab-server", "--fab-on-call");
}
