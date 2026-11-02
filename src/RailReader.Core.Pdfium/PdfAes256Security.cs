using System.Security.Cryptography;
using System.Text;

namespace RailReader.Core.Services;

/// <summary>
/// The parts of the PDF standard security handler, revisions 5 and 6 (AES-256, <c>/V 5</c>), that
/// <see cref="PdfReplyLinker"/> needs to add a stream to an encrypted file: recovering the file key
/// from a password (ISO 32000-2 Algorithm 2.A, with the 2.B hash for R6) and encrypting stream data
/// with it (<c>/AESV3</c>). Unlike earlier revisions, R5/R6 use the file key directly for every object,
/// so nothing here depends on an object's number.
/// </summary>
internal static class PdfAes256Security
{
    /// <summary>
    /// The 32-byte file key, or null when <paramref name="password"/> opens the file as neither its
    /// owner nor a user. <paramref name="o"/>/<paramref name="u"/>/<paramref name="oe"/>/<paramref name="ue"/>
    /// are the decoded strings of the encryption dictionary.
    /// </summary>
    public static byte[]? FileKey(int revision, byte[] o, byte[] u, byte[] oe, byte[] ue, string? password)
    {
        if (revision is not (5 or 6) || o.Length < 48 || u.Length < 48 || oe.Length < 32 || ue.Length < 32) return null;
        var u48 = u.AsSpan(0, 48).ToArray();

        // The raw UTF-8 first — what PDFium checks, so what opened the file — then its NFKC form, the
        // SASLprep a conforming writer may have applied.
        foreach (var pw in PasswordCandidates(password))
        {
            // Owner first, as readers do: an owner password also opens the file but yields the key via /OE.
            if (Hash(revision, pw, o.AsSpan(32, 8), u48).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
                return Unwrap(Hash(revision, pw, o.AsSpan(40, 8), u48), oe);
            if (Hash(revision, pw, u.AsSpan(32, 8), []).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
                return Unwrap(Hash(revision, pw, u.AsSpan(40, 8), []), ue);
        }
        return null;
    }

    /// <summary>Stream data encrypted under <c>/AESV3</c>: a random 16-byte IV, then AES-256-CBC with PKCS#7 padding.</summary>
    public static byte[] EncryptStream(byte[] fileKey, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        var iv = RandomNumberGenerator.GetBytes(16);
        var body = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        return [.. iv, .. body];
    }

    /// <summary>Inverse of <see cref="EncryptStream"/>; for tests.</summary>
    internal static byte[] DecryptStream(byte[] fileKey, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
    }

    // SASLprep is approximated by NFKC, which covers every password a person types in practice;
    // the spec caps the UTF-8 form at 127 bytes.
    private static IEnumerable<byte[]> PasswordCandidates(string? password)
    {
        var raw = password ?? "";
        yield return Truncate(Encoding.UTF8.GetBytes(raw));
        var prepped = raw.Normalize(NormalizationForm.FormKC);
        if (prepped != raw) yield return Truncate(Encoding.UTF8.GetBytes(prepped));

        static byte[] Truncate(byte[] bytes) => bytes.Length > 127 ? bytes[..127] : bytes;
    }

    private static byte[] Unwrap(byte[] key, byte[] wrapped)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(wrapped.AsSpan(0, 32), new byte[16], PaddingMode.None);
    }

    /// <summary>R5: one SHA-256. R6: Algorithm 2.B, at least 64 rounds of AES-128 and SHA-2.</summary>
    private static byte[] Hash(int revision, byte[] password, ReadOnlySpan<byte> salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (revision == 5) return k;

        using var aes = Aes.Create();
        byte[] e = [];
        for (int round = 0; round < 64 || e[^1] > round - 32; round++)
        {
            var block = new byte[password.Length + k.Length + userKey.Length];
            password.CopyTo(block, 0);
            k.CopyTo(block, password.Length);
            userKey.CopyTo(block, password.Length + k.Length);
            var k1 = new byte[block.Length * 64];
            for (int r = 0; r < 64; r++) block.CopyTo(k1, r * block.Length);

            aes.Key = k[..16];
            e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);

            // The first 16 bytes of E as a big-endian number, mod 3 — and 256 ≡ 1 (mod 3), so the byte sum will do.
            int mod = 0;
            for (int b = 0; b < 16; b++) mod += e[b];
            k = (mod % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
        }
        return k[..32];
    }
}
