using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace ResolveAI.App.Services;

/// <summary>
/// The live-mode access code, kept only in this browser, encrypted so it can't be read from storage. Storage can be
/// unavailable (private mode) or unreadable (new server keys), so failures just mean the code has to be entered again.
/// </summary>
public sealed class AccessCodeStore(ProtectedLocalStorage storage)
{
    private const string Key = "resolveai.accessCode";

    public string Current { get; private set; } = "";
    public bool Loaded { get; private set; }

    public async Task Load()
    {
        if (Loaded) return;
        try
        {
            var result = await storage.GetAsync<string>(Key);
            Current = result.Success ? result.Value ?? "" : "";
        }
        catch (Exception e) when (e is JSException or CryptographicException or InvalidOperationException or JSDisconnectedException)
        {
            Current = "";
        }
        Loaded = true;
    }

    public async Task Set(string code)
    {
        Current = code;
        Loaded = true;
        try
        {
            if (code.Length > 0) await storage.SetAsync(Key, code);
            else await storage.DeleteAsync(Key);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or JSDisconnectedException)
        {
            // Not persisted; the code just has to be entered again next time.
        }
    }
}
