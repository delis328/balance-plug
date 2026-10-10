using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BalancePlug.Core;

namespace BalancePlug;

public sealed class LocalStore
{
    public string DirectoryPath { get; }
    public string? MigrationNotice { get; private set; }
    public bool MigrationPending { get; private set; }
    public string MascotPath => Path.Combine(DirectoryPath, "mascot.png");
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private string SessionPath => Path.Combine(DirectoryPath, "session.dat");
    public LocalStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalancePlug");

    public AppSettings LoadSettings(out bool corrupt, bool deferMigration = false)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var bytes = File.ReadAllBytes(SettingsPath);
            using var document = JsonDocument.Parse(bytes);
            var settings = JsonSerializer.Deserialize<AppSettings>(bytes);
            if (settings is null) throw new JsonException();
            if (!document.RootElement.TryGetProperty("BubbleScale", out _)) settings.BubbleScale = AppSettings.LegacyScale(settings.Size);
            if (!document.RootElement.TryGetProperty("MascotScale", out _)) settings.MascotScale = AppSettings.LegacyScale(settings.Size);
            settings.Normalize();
            MigrationPending = settings.Size is not null || settings.CustomMascot.HasValue || !document.RootElement.TryGetProperty("Mascots", out _);
            if (!deferMigration) CompleteMigration(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { corrupt = true; return new AppSettings(); }
    }

    public void CompleteMigration(AppSettings settings)
    {
        if (!MigrationPending) return;
        var legacy = settings.CustomMascot == true;
        settings.Size = null; settings.CustomMascot = null; MigrationPending = false;
        try
        {
            if (File.Exists(MascotPath)) ImportMascot(settings, File.ReadAllBytes(MascotPath), "已有自定义形象", legacy);
            else { if (legacy) MigrationNotice = "旧形象缺失，已回退默认"; SaveSettings(settings); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BalanceException or NotSupportedException or System.IO.FileFormatException)
        { MigrationNotice = "旧形象无法迁移，原文件已保留；请重新导入"; }
    }

    public AccountSession? LoadSession(out bool corrupt)
    {
        corrupt = false;
        byte[]? plain = null;
        try
        {
            if (!File.Exists(SessionPath)) return null;
            plain = Protect(File.ReadAllBytes(SessionPath), decrypt: true);
            return JsonSerializer.Deserialize<AccountSession>(plain) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        { corrupt = true; return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void SaveSettings(AppSettings settings) => AtomicWrite(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings));
    public void SaveSession(AccountSession? session)
    {
        if (session is null) { if (File.Exists(SessionPath)) File.Delete(SessionPath); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try { AtomicWrite(SessionPath, Protect(plain, decrypt: false)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string GetMascotPath(string id)
    {
        if (!AppSettings.ValidMascotId(id)) throw new BalanceException("形象标识无效");
        return Path.Combine(DirectoryPath, "mascots", id + ".png");
    }

    public MascotItem ImportMascot(AppSettings settings, byte[] bytes, string name, bool select = true, string? displayName = null)
    {
        MascotImages.Decode(bytes, 256); // Validate before mutating the library or selection.
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = settings.Mascots.FirstOrDefault(m => m.Id == id);
        var stem = displayName is null ? Path.GetFileNameWithoutExtension(name) : ValidateMascotName(displayName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "本地形象";
        var uniqueName = UniqueMascotName(settings, stem, id);
        var item = existing is null ? new MascotItem(id, uniqueName) : displayName is null ? existing : existing with { Name = uniqueName };
        var path = GetMascotPath(id); var existed = File.Exists(path);
        AtomicWrite(path, bytes);
        var previous = settings.ActiveMascotId;
        var existingIndex = existing is null ? -1 : settings.Mascots.IndexOf(existing);
        if (existing is null) settings.Mascots.Add(item); else settings.Mascots[existingIndex] = item;
        if (select) settings.ActiveMascotId = id;
        try { SaveSettings(settings); }
        catch
        {
            settings.ActiveMascotId = previous;
            if (existing is null) settings.Mascots.Remove(item); else settings.Mascots[existingIndex] = existing;
            if (!existed) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
        return item;
    }

    public static string ValidateMascotName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 40 || name.Any(char.IsControl))
            throw new BalanceException("名称须为 1—40 个字符，且不能包含换行或控制字符");
        return name;
    }

    private static string UniqueMascotName(AppSettings settings, string stem, string id)
    {
        var value = stem; var suffix = 2;
        while (settings.Mascots.Any(m => m.Id != id && string.Equals(m.Name, value, StringComparison.OrdinalIgnoreCase)))
            value = stem + "（" + suffix++ + "）";
        return value;
    }

    public MascotItem RenameMascot(AppSettings settings, string id, string name)
    {
        var index = settings.Mascots.FindIndex(m => m.Id == id);
        if (index < 0) throw new BalanceException("该形象已不存在");
        var previous = settings.Mascots[index];
        var renamed = previous with { Name = UniqueMascotName(settings, ValidateMascotName(name), id) };
        settings.Mascots[index] = renamed;
        try { SaveSettings(settings); }
        catch { settings.Mascots[index] = previous; throw; }
        return renamed;
    }

    public void SaveMascot(byte[] bytes) => AtomicWrite(MascotPath, bytes); // Legacy fixture/import source.

    private void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, "BalancePlug session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 无法加密或解密此会话");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            // Do not leave decrypted tokens in unmanaged input/output buffers.
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}

