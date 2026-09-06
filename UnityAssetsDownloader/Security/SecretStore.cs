using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Хранение секретов средствами операционной системы.
///
/// Windows: сессия шифруется через DPAPI под учётную запись текущего пользователя,
/// логин и пароль кладутся в Диспетчер учётных данных Windows
/// (виден в `control /name Microsoft.CredentialManager`).
///
/// Linux и macOS: DPAPI нет, файл сохраняется как есть с правами 600.
/// </summary>
internal static class SecretStore
{
    private const string CredentialTargetPrefix = "UnityAssetsDownloader";

    /// <summary>Доступно ли шифрование средствами ОС.</summary>
    public static bool EncryptionAvailable => OperatingSystem.IsWindows();

    /// <summary>Доступен ли Диспетчер учётных данных для хранения логина и пароля.</summary>
    public static bool CredentialManagerAvailable => OperatingSystem.IsWindows();

    public static string BuildCredentialTarget(string profileName) => $"{CredentialTargetPrefix}:{profileName}";

    // ---------------------------------------------------------------- сессия

    /// <summary>
    /// Записывает текст в файл. На Windows содержимое шифруется DPAPI.
    /// Операция идемпотентна: повторная запись просто заменяет файл.
    /// </summary>
    public static void WriteProtectedText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plain = Encoding.UTF8.GetBytes(content);

        if (OperatingSystem.IsWindows() && TryProtect(plain, out var encrypted))
        {
            File.WriteAllBytes(path, encrypted);
            return;
        }

        File.WriteAllBytes(path, plain);
        RestrictToCurrentUser(path);
    }

    /// <summary>
    /// Читает файл, записанный через <see cref="WriteProtectedText"/>.
    /// Понимает и незашифрованные файлы — они остались от старых версий программы.
    /// </summary>
    public static string? ReadProtectedText(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var raw = File.ReadAllBytes(path);
        if (raw.Length == 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && TryUnprotect(raw, out var decrypted))
        {
            return Encoding.UTF8.GetString(decrypted);
        }

        // Файл от старой версии или с другой ОС — читаем как обычный текст.
        var text = Encoding.UTF8.GetString(raw);
        return LooksLikeJson(text) ? text : null;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>На Unix снимает права у всех, кроме владельца. На Windows ничего не делает.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Файловая система может не поддерживать права доступа — не критично.
        }
    }

    // ------------------------------------------------------------- DPAPI

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static bool TryProtect(byte[] plain, out byte[] result)
    {
        return TryTransform(plain, encrypt: true, out result);
    }

    private static bool TryUnprotect(byte[] encrypted, out byte[] result)
    {
        return TryTransform(encrypted, encrypt: false, out result);
    }

    private static bool TryTransform(byte[] input, bool encrypt, out byte[] result)
    {
        result = [];

        var inputPtr = IntPtr.Zero;
        var output = new DataBlob();

        try
        {
            inputPtr = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputPtr, input.Length);

            var blob = new DataBlob { Size = input.Length, Data = inputPtr };

            var ok = encrypt
                ? CryptProtectData(ref blob, "UnityAssetsDownloader session", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out output);

            if (!ok || output.Data == IntPtr.Zero)
            {
                return false;
            }

            result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }

            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    // ------------------------------- Диспетчер учётных данных Windows

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>
    /// Сохраняет логин и пароль в Диспетчер учётных данных Windows.
    /// Повторный вызов перезаписывает запись — дублей не появляется.
    /// </summary>
    public static bool TrySaveCredentials(string target, string userName, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var targetPtr = IntPtr.Zero;
        var userPtr = IntPtr.Zero;
        var blobPtr = IntPtr.Zero;

        try
        {
            var blob = Encoding.Unicode.GetBytes(password);

            // Windows не принимает секрет длиннее 2560 байт.
            if (blob.Length > 2560)
            {
                return false;
            }

            targetPtr = Marshal.StringToCoTaskMemUni(target);
            userPtr = Marshal.StringToCoTaskMemUni(userName);
            blobPtr = Marshal.AllocCoTaskMem(blob.Length);
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr
            };

            return CredWrite(ref credential, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    /// <summary>Читает логин и пароль из Диспетчера учётных данных Windows.</summary>
    public static bool TryReadCredentials(string target, out string userName, out string password)
    {
        userName = string.Empty;
        password = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var credentialPtr = IntPtr.Zero;

        try
        {
            if (!CredRead(target, CredTypeGeneric, 0, out credentialPtr) || credentialPtr == IntPtr.Zero)
            {
                return false;
            }

            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);

            if (credential.UserName != IntPtr.Zero)
            {
                userName = Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
            }

            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
            {
                var blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, credential.CredentialBlobSize);
                password = Encoding.Unicode.GetString(blob);
            }

            return !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (credentialPtr != IntPtr.Zero)
            {
                CredFree(credentialPtr);
            }
        }
    }

    /// <summary>Удаляет запись. Если её нет — считается успехом, вызов идемпотентен.</summary>
    public static bool TryDeleteCredentials(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return CredDelete(target, CredTypeGeneric, 0) || !TryReadCredentials(target, out _, out _);
        }
        catch
        {
            return false;
        }
    }
}
