using System.Text.Json;

/// <summary>Один профиль — один аккаунт Unity со своей сессией.</summary>
internal sealed class ProfileInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool PasswordSaved { get; set; }
}

/// <summary>Список профилей на этом компьютере и профиль по умолчанию.</summary>
internal sealed class ProfileRegistry
{
    public string? DefaultProfile { get; set; }
    public List<ProfileInfo> Profiles { get; set; } = [];
}

/// <summary>
/// Управляет профилями в каталоге данных.
/// Все операции идемпотентны: повторный вызов не создаёт дублей и не ломает файлы.
/// </summary>
internal sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dataDirectory;

    public ProfileStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public string RegistryPath => Path.Combine(_dataDirectory, "profiles.json");

    public string ProfilesRoot => Path.Combine(_dataDirectory, "profiles");

    public string GetProfileDirectory(string name) => Path.Combine(ProfilesRoot, Sanitize(name));

    public string GetSessionPath(string name) => Path.Combine(GetProfileDirectory(name), "session.dat");

    /// <summary>
    /// Приводит имя профиля к безопасному имени папки.
    /// Одно и то же имя всегда даёт одну и ту же папку.
    /// </summary>
    public static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "default";
        }

        // Список специально жёстче, чем требует Linux: имя профиля должно получаться
        // одинаковым на всех компьютерах, а Windows запрещает больше символов.
        const string invalid = "<>:\"/\\|?*";
        var safe = new string(trimmed
            .Select(c => invalid.Contains(c) || c == ' ' || char.IsControl(c) ? '_' : c)
            .ToArray())
            .Trim('_', '.');

        return safe.Length == 0 ? "default" : safe;
    }

    public ProfileRegistry Load()
    {
        try
        {
            if (File.Exists(RegistryPath))
            {
                var raw = File.ReadAllText(RegistryPath);
                var registry = JsonSerializer.Deserialize<ProfileRegistry>(raw, ReadOptions);
                if (registry is not null)
                {
                    return registry;
                }
            }
        }
        catch
        {
            // Битый файл списка профилей не должен мешать работе — начинаем с пустого.
        }

        return new ProfileRegistry();
    }

    public void Save(ProfileRegistry registry)
    {
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(RegistryPath, JsonSerializer.Serialize(registry, JsonOptions));
    }

    /// <summary>
    /// Регистрирует профиль, если его ещё нет, и отмечает время последнего использования.
    /// Первый созданный профиль автоматически становится профилем по умолчанию.
    /// </summary>
    public ProfileInfo Touch(string name, string? email = null, bool? passwordSaved = null)
    {
        var registry = Load();
        var profile = registry.Profiles
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            profile = new ProfileInfo { Name = name };
            registry.Profiles.Add(profile);
        }

        profile.LastUsedUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(email))
        {
            profile.Email = email;
        }

        if (passwordSaved.HasValue)
        {
            profile.PasswordSaved = passwordSaved.Value;
        }

        if (string.IsNullOrWhiteSpace(registry.DefaultProfile))
        {
            registry.DefaultProfile = name;
        }

        Directory.CreateDirectory(GetProfileDirectory(name));
        Save(registry);
        return profile;
    }

    public void SetDefault(string name)
    {
        var registry = Load();
        if (!registry.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            registry.Profiles.Add(new ProfileInfo { Name = name });
        }

        registry.DefaultProfile = name;
        Save(registry);
    }

    /// <summary>
    /// Определяет, под каким профилем работать.
    /// Приоритет: параметр командной строки, затем config.json, затем профиль по
    /// умолчанию из profiles.json, затем имя пользователя операционной системы.
    /// Последний вариант важен для школьных классов: у каждого ученика,
    /// который входит в Windows под собой, свой профиль появляется сам собой.
    /// </summary>
    public string ResolveProfileName(string? fromCli, string? fromConfig)
    {
        if (!string.IsNullOrWhiteSpace(fromCli))
        {
            return fromCli.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig.Trim();
        }

        var registry = Load();
        if (!string.IsNullOrWhiteSpace(registry.DefaultProfile))
        {
            return registry.DefaultProfile.Trim();
        }

        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName) ? "default" : userName.Trim();
    }

    /// <summary>
    /// Переносит сессию из старой раскладки (data/unity_session_state.json)
    /// в папку профиля. Выполняется один раз: если в профиле уже есть сессия,
    /// ничего не трогает.
    /// </summary>
    public bool TryMigrateLegacySession(string profileName, out string message)
    {
        message = string.Empty;

        var legacyPath = Path.Combine(_dataDirectory, "unity_session_state.json");
        var targetPath = GetSessionPath(profileName);

        if (!File.Exists(legacyPath) || File.Exists(targetPath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(legacyPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            SecretStore.WriteProtectedText(targetPath, raw);
            File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
            var protection = SecretStore.EncryptionAvailable
                ? "и зашифрована средствами Windows"
                : "и закрыта от других пользователей компьютера";
            message = $"Старая сессия перенесена в профиль '{profileName}' {protection}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Не удалось перенести старую сессию: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Переименовывает профиль, когда стало известно имя аккаунта Unity.
    /// Папка переезжает целиком: и сессия, и папка браузера.
    ///
    /// Операция необязательная. Если что-то помешало — остаёмся на старом имени
    /// и попробуем в следующий раз. Ничего не теряется.
    /// </summary>
    public bool TryRename(string oldName, string newName, out string message)
    {
        message = string.Empty;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var from = GetProfileDirectory(oldName);
        var to = GetProfileDirectory(newName);

        try
        {
            if (Directory.Exists(to))
            {
                // Такой профиль уже есть — значит этим аккаунтом уже пользовались.
                // Ничего не двигаем, просто переключаемся на него со следующего раза.
                SetDefault(newName);
                message = $"Профиль для этого аккаунта уже есть: '{newName}'. Следующий запуск пойдёт под ним.";
                return false;
            }

            if (Directory.Exists(from))
            {
                // Windows не отдаёт папку, пока Chrome не закрылся до конца.
                // На это уходит секунда-другая, поэтому пробуем несколько раз.
                Exception? last = null;
                var moved = false;

                for (var attempt = 1; attempt <= 6 && !moved; attempt++)
                {
                    try
                    {
                        Directory.Move(from, to);
                        moved = true;
                    }
                    catch (IOException ex)
                    {
                        last = ex;
                        Thread.Sleep(1500);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        last = ex;
                        Thread.Sleep(1500);
                    }
                }

                if (!moved)
                {
                    throw last ?? new IOException("Папку профиля переместить не удалось.");
                }
            }
            else
            {
                Directory.CreateDirectory(to);
            }

            var registry = Load();
            var profile = registry.Profiles
                .FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));

            if (profile is not null)
            {
                profile.Name = newName;
            }
            else
            {
                registry.Profiles.Add(new ProfileInfo { Name = newName, LastUsedUtc = DateTime.UtcNow });
            }

            if (string.Equals(registry.DefaultProfile, oldName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(registry.DefaultProfile))
            {
                registry.DefaultProfile = newName;
            }

            Save(registry);
            message = $"Профиль переименован: '{oldName}' -> '{newName}'.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Профиль переименовать не удалось ({ex.Message}). Останемся на '{oldName}', попробуем позже.";
            return false;
        }
    }

    public string Describe()
    {
        var registry = Load();
        if (registry.Profiles.Count == 0)
        {
            return "Профилей пока нет.";
        }

        var lines = registry.Profiles
            .OrderByDescending(p => p.LastUsedUtc ?? DateTime.MinValue)
            .Select(p =>
            {
                var isDefault = string.Equals(p.Name, registry.DefaultProfile, StringComparison.OrdinalIgnoreCase);
                var marker = isDefault ? " (по умолчанию)" : string.Empty;
                var email = string.IsNullOrWhiteSpace(p.Email) ? "email неизвестен" : p.Email;
                var lastUsed = p.LastUsedUtc.HasValue
                    ? p.LastUsedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "не запускался";
                var password = p.PasswordSaved ? "пароль сохранён" : "пароль не сохранён";
                return $"  {p.Name}{marker} | {email} | последний запуск: {lastUsed} | {password}";
            });

        return string.Join(Environment.NewLine, lines);
    }
}
