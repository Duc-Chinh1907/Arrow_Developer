using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Arrow.Common.Secrets;
using Arrow.DataAccess.IO;

namespace Arrow.DataAccess.Security;

/// <summary>API keys (P09 §4).</summary>
public interface ISecretStore
{
    /// <summary>The key for <paramref name="reference"/>, in the order of P09 §4.3; null when none is found.</summary>
    SecretValue? Get(string reference);

    /// <summary>Stores a key in <c>secrets/</c>.</summary>
    void Set(string reference, SecretValue value);

    /// <summary>Deletes a stored key; false when there was none.</summary>
    bool Remove(string reference);

    /// <summary>Stored keys and keys given by <c>ARROW_SECRET_*</c> variables, with their last characters only.</summary>
    IReadOnlyList<SecretInfo> List();
}

/// <summary>
/// File implementation of <see cref="ISecretStore"/>. Windows: <c>secrets/&lt;ref&gt;.bin</c> encrypted with DPAPI for the
/// current user. Linux and macOS: <c>secrets/&lt;ref&gt;.key</c> with mode 600 in a folder with mode 700.
/// Every key read or written is registered with <see cref="Redactor"/>.
/// </summary>
public sealed partial class SecretStore : ISecretStore
{
    /// <summary>Prefix of the environment variables that override stored keys.</summary>
    public const string EnvironmentPrefix = "ARROW_SECRET_";

    // Fixed entropy of ARROW (P09 §4.1): another program running as the same user cannot decrypt the files by
    // calling DPAPI without it.
    private static readonly byte[] Entropy = "ARROW Developer CLI secret v1"u8.ToArray();

    private static readonly Dictionary<string, string> ProviderVariables = new(StringComparer.Ordinal)
    {
        ["openai"] = "OPENAI_API_KEY",
        ["anthropic"] = "ANTHROPIC_API_KEY",
    };

    private readonly string _dir;
    private readonly Func<string, string?> _getEnvironment;
    private readonly Func<IEnumerable<string>> _environmentNames;
    private readonly bool _windows;

    /// <param name="secretsDir">Usually <c>ArrowHome.SecretsDir</c>.</param>
    /// <param name="getEnvironment">Reads an environment variable; tests pass their own.</param>
    /// <param name="environmentNames">Lists environment variable names; tests pass their own.</param>
    public SecretStore(string secretsDir, Func<string, string?>? getEnvironment = null, Func<IEnumerable<string>>? environmentNames = null)
    {
        _dir = Path.GetFullPath(secretsDir);
        _getEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
        _environmentNames = environmentNames ?? (() => Environment.GetEnvironmentVariables().Keys.Cast<string>());
        _windows = OperatingSystem.IsWindows();
    }

    /// <summary>Name of the environment variable for <paramref name="reference"/>, e.g. <c>ARROW_SECRET_TEAMO</c>.</summary>
    public static string EnvironmentVariable(string reference) =>
        EnvironmentPrefix + reference.ToUpperInvariant().Replace('-', '_');

    public SecretValue? Get(string reference)
    {
        CheckReference(reference);
        var value = FromEnvironment(reference)?.Value ?? ReadFile(reference);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        Redactor.Register(value);
        return new SecretValue(value);
    }

    public void Set(string reference, SecretValue value)
    {
        CheckReference(reference);
        var plain = value.Reveal();
        Redactor.Register(plain);
        EnsureDirectory();
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
        {
            AtomicFile.WriteAllBytes(FilePath(reference), Protect(bytes));
            return;
        }

        var path = FilePath(reference);
        AtomicFile.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public bool Remove(string reference)
    {
        CheckReference(reference);
        var path = FilePath(reference);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public IReadOnlyList<SecretInfo> List()
    {
        var result = new Dictionary<string, SecretInfo>(StringComparer.Ordinal);
        if (Directory.Exists(_dir))
        {
            foreach (var file in Directory.GetFiles(_dir, _windows ? "*.bin" : "*.key"))
            {
                var reference = Path.GetFileNameWithoutExtension(file);
                if (ReferencePattern().IsMatch(reference) && ReadFile(reference) is { } stored)
                {
                    result[reference] = new SecretInfo(reference, SecretValue.MaskForDisplay(stored), SecretSource.Store);
                }
            }
        }

        foreach (var name in _environmentNames().Where(n => n.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var reference = name[EnvironmentPrefix.Length..].ToLowerInvariant().Replace('_', '-');
            if (ReferencePattern().IsMatch(reference) && _getEnvironment(name) is { Length: > 0 } value)
            {
                result[reference] = new SecretInfo(reference, SecretValue.MaskForDisplay(value), SecretSource.ArrowEnvironment);
            }
        }

        return result.Values.OrderBy(s => s.Reference, StringComparer.Ordinal).ToList();
    }

    /// <summary>The key from the environment, in the order of P09 §4.3 steps 1–2.</summary>
    private (string Value, SecretSource Source)? FromEnvironment(string reference)
    {
        if (_getEnvironment(EnvironmentVariable(reference)) is { Length: > 0 } own)
        {
            return (own, SecretSource.ArrowEnvironment);
        }

        return ProviderVariables.TryGetValue(reference, out var standard) && _getEnvironment(standard) is { Length: > 0 } provider
            ? (provider, SecretSource.ProviderEnvironment)
            : null;
    }

    private string? ReadFile(string reference)
    {
        var path = FilePath(reference);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(_windows ? Unprotect(bytes) : bytes).Trim();
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string FilePath(string reference) => Path.Combine(_dir, reference + (_windows ? ".bin" : ".key"));

    private static byte[] Protect(byte[] plain) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();

    private static byte[] Unprotect(byte[] cipher) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();

    /// <summary>
    /// A reference is a file name of lower-case letters, digits and <c>-</c>, so it cannot leave <c>secrets/</c>, and it maps
    /// one-to-one to its environment variable (<c>-</c> ↔ <c>_</c>).
    /// </summary>
    private static void CheckReference(string reference)
    {
        if (!ReferencePattern().IsMatch(reference))
        {
            throw new ArgumentException($"Invalid secret reference: {reference}. Use a-z, 0-9 and '-'.", nameof(reference));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ReferencePattern();
}
