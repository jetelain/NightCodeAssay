using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace NightCodeAssay.Core.Security;

/// <summary>
/// Platform abstraction over at-rest key protection for <c>default-key.dat</c>.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Wraps <paramref name="plaintext"/> for at-rest storage.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Unwraps <paramref name="protectedData"/> produced by <see cref="Protect"/>.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Identity (pass-through) protector used on non-Windows platforms and in unit tests.
/// </summary>
public sealed class PlainKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedData) => protectedData;
}

/// <summary>
/// Windows DPAPI protector using <see cref="DataProtectionScope.CurrentUser"/> so
/// <c>default-key.dat</c> can be unwrapped by companion processes under the same OS user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
}
