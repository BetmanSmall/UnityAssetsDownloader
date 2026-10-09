/// <summary>
/// Один запуск Fab за раз. Служба (этап после прогона Unity) и команда из `docker compose exec` работают в
/// одном контейнере с одной папкой браузера и одними файлами памяти: два запуска одновременно перехватывали бы
/// чужое окно Chrome, закрывали его и затирали друг другу файлы. Замок — файл `fab/run.lock`, удерживаемый
/// процессом; если процесс убит, система снимает замок сама.
/// </summary>
internal sealed class FabRunLock : IDisposable
{
    private readonly FileStream? _stream;

    private FabRunLock(FileStream? stream) => _stream = stream;

    /// <summary>null — замок занят другим запуском. Замок нельзя завести по другой причине (права) — запуск не блокируем.</summary>
    public static FabRunLock? TryAcquire(string fabDirectory)
    {
        try
        {
            Directory.CreateDirectory(fabDirectory);
            var stream = new FileStream(Path.Combine(fabDirectory, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new FabRunLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return new FabRunLock(null);
        }
    }

    public void Dispose() => _stream?.Dispose();
}
