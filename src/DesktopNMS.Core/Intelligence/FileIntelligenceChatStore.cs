using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>Atomic, per-chat files. The platform supplies encryption; scopes
/// separate LibreNMS servers and ChatGPT accounts without exposing their names.</summary>
public sealed class FileIntelligenceChatStore(string root, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect) : IIntelligenceChatStore
{
    private readonly object _sync = new();

    public SavedChatCollection Load(string scope)
    {
        lock (_sync)
        {
            var directory = DirectoryFor(scope);
            if (!Directory.Exists(directory)) return new(null, Array.Empty<SavedIntelligenceChat>());
            // Each file on its own (#322 in DashyNMS/desktop): one that won't
            // decrypt or parse is skipped, not the end of every chat.
            var chats = new List<SavedIntelligenceChat>();
            var unreadable = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.chat").ToList())
            {
                try
                {
                    chats.Add(ReadChat(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked or out of reach for now - try again next time.
                    unreadable.Add(Path.GetFileName(file));
                }
                catch (Exception)
                {
                    // Damaged for good: moved aside, so it isn't retried on every start.
                    unreadable.Add(Path.GetFileName(file));
                    SetAside(file);
                }
            }

            Guid? active = null;
            var statePath = Path.Combine(directory, "active.dat");
            if (File.Exists(statePath))
            {
                active = ReadActive(statePath);
            }

            return new(active, chats.OrderByDescending(chat => chat.IsPinned).ThenByDescending(chat => chat.UpdatedAt).ToList(), unreadable);
        }
    }

    public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true)
    {
        lock (_sync)
        {
            var directory = DirectoryFor(scope);
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, chat.Id.ToString("N") + ".chat"), JsonSerializer.SerializeToUtf8Bytes(chat));
            if (makeActive) Write(Path.Combine(directory, "active.dat"), JsonSerializer.SerializeToUtf8Bytes(chat.Id));
        }
    }

    public void Delete(string scope, Guid id)
    {
        lock (_sync)
        {
            var directory = DirectoryFor(scope);
            File.Delete(Path.Combine(directory, id.ToString("N") + ".chat"));

            // A deleted chat is no longer the one to reopen.
            var statePath = Path.Combine(directory, "active.dat");
            if (File.Exists(statePath) && ReadActive(statePath) == id) File.Delete(statePath);
        }
    }

    private string DirectoryFor(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))));
    }

    private SavedIntelligenceChat ReadChat(string file)
    {
        var data = Read(file);
        try
        {
            var chat = JsonSerializer.Deserialize<SavedIntelligenceChat>(data) ?? throw new JsonException("Empty saved chat.");
            if (Path.GetFileNameWithoutExtension(file) != chat.Id.ToString("N")) throw new JsonException("Saved chat identity mismatch.");
            return chat;
        }
        finally { Array.Clear(data); }
    }

    /// <summary>Renames a damaged chat to <c>.chat.bad</c>, kept in case it can be recovered.</summary>
    private static void SetAside(string file)
    {
        try { File.Move(file, file + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The chat that was open, or null when it can't be read - that only loses which chat was open; the next save writes it again.</summary>
    private Guid? ReadActive(string statePath)
    {
        try
        {
            var data = Read(statePath);
            try { return JsonSerializer.Deserialize<Guid>(data); }
            finally { Array.Clear(data); }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private byte[] Read(string path) => unprotect(File.ReadAllBytes(path));

    private void Write(string path, byte[] plain)
    {
        try
        {
            var encrypted = protect(plain);
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { Array.Clear(plain); }
    }
}
