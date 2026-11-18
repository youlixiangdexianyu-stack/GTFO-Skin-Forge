using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ForgeCore;

/// <summary>A stable fingerprint over labelled values: the same values added in the same order give the same hex on every machine.
/// Each entry is hashed as a type tag, the UTF-8 byte length and bytes of the label, then the byte length (-1 for null) and bytes of the
/// value (numbers in invariant culture), so no text can run one entry into the next (SHA-256, first 16 bytes as 32 hex characters). Floats are rounded to the precision the caller states, so the same level read twice gives one fingerprint.</summary>
internal sealed class Fingerprint
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public int Count { get; private set; }

    public Fingerprint Add(string label, string? value) => Write('s', label, value);

    public Fingerprint Add(string label, long value) => Write('i', label, value.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string label, ulong value) => Write('u', label, value.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string label, bool value) => Write('b', label, value ? "1" : "0");

    /// <summary>A float rounded to <paramref name="decimals"/> places (0.01 m is 2). NaN and infinities are written by name.</summary>
    public Fingerprint Add(string label, double value, int decimals)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return Write('d', label, value.ToString(CultureInfo.InvariantCulture));
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // negative zero
        return Write('d', label, rounded.ToString("F" + decimals, CultureInfo.InvariantCulture));
    }

    /// <summary>Mixes another finished fingerprint in, so a section hash can be both kept on its own and counted in the whole.</summary>
    public Fingerprint Add(string label, Fingerprint inner) => Write('f', label, inner.Hex());

    /// <summary>Files up to this size are fingerprinted by content in <see cref="OfFolder"/>, larger ones by name and length only.</summary>
    public const long SmallFileBytes = 1 << 20;

    /// <summary>The fingerprint of everything under <paramref name="folder"/>: the relative path (with /) and length of every file in
    /// ordinal order, and the content hash of each file of up to <see cref="SmallFileBytes"/>. Equal exactly when the same files hold the
    /// same text and data; a large media file that changed without changing length is the one thing it does not see.</summary>
    public static string OfFolder(string folder)
    {
        var names = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        var fingerprint = new Fingerprint();
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            var length = new FileInfo(path).Length;
            fingerprint.Add(name, length);
            if (length <= SmallFileBytes) fingerprint.Add(name + "#content", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return fingerprint.Hex();
    }

    private Fingerprint Write(char tag, string label, string? value)
    {
        _hash.AppendData(new[] { (byte)tag });
        Append(label);
        if (value == null) _hash.AppendData(BitConverter.GetBytes(-1));
        else Append(value);
        Count++;
        return this;
    }

    private void Append(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _hash.AppendData(BitConverter.GetBytes(bytes.Length));
        _hash.AppendData(bytes);
    }

    /// <summary>The fingerprint of everything added so far (32 lower-case hex characters). Adding more afterwards continues from the same
    /// state, so a fingerprint read in the middle does not end the run.</summary>
    public string Hex()
    {
        var bytes = _hash.GetCurrentHash();
        var hex = new StringBuilder(32);
        for (var i = 0; i < 16; i++) hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}
