using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ForgeCore;

/// <summary>SHA-256 as 64 lowercase hex characters, the one spelling of a content hash for pins, file names and saved keys. Text is hashed as
/// its UTF-8 bytes. A hash that is written or compared in capitals keeps its own spelling (<c>Fingerprint</c> writes none lowercase). No game or Unity type.</summary>
public static class Hash
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
