// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowFileCrypto.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentFlow.Contracts;

/// <summary>
/// Encrypts workflow JSON for the custom .rw storage format. The JSON graph remains the
/// in-memory runtime format; only the persisted file payload is encrypted.
/// </summary>
public static class WorkflowFileCrypto
{
    public const string RwExtension = ".rw";
    public const string BackupExtension = ".bak";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RW01");
    private static readonly byte[] Key = SHA256.HashData(
        Encoding.UTF8.GetBytes("AgentFlow RW Workflow Encryption v1"));
    private static readonly ConcurrentDictionary<string, object> PathGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Return true when <paramref name="path"/> uses the encrypted workflow extension.</summary>
    public static bool HasRwExtension(string path) =>
        Path.GetExtension(path).Equals(RwExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Return the canonical .rw persistence path for a workflow file.</summary>
    public static string NormalizeWorkflowPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return HasRwExtension(path) ? path : Path.ChangeExtension(path, RwExtension);
    }

    /// <summary>Return true when the payload starts with the .rw file signature.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> payload) =>
        payload.Length >= Magic.Length + NonceSize + TagSize &&
        payload[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Encrypt a workflow JSON string into .rw bytes.</summary>
    public static byte[] Encrypt(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        var plaintext = Encoding.UTF8.GetBytes(json);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[Magic.Length + NonceSize + TagSize + ciphertext.Length];
        Magic.CopyTo(payload, 0);
        nonce.CopyTo(payload, Magic.Length);
        tag.CopyTo(payload, Magic.Length + NonceSize);
        ciphertext.CopyTo(payload, Magic.Length + NonceSize + TagSize);
        return payload;
    }

    /// <summary>Decrypt .rw bytes into the original workflow JSON string.</summary>
    public static string Decrypt(ReadOnlySpan<byte> payload)
    {
        if (!IsEncrypted(payload))
            throw new InvalidDataException("The file is not a valid AgentFlow .rw workflow file.");

        var nonce = payload.Slice(Magic.Length, NonceSize);
        var tag = payload.Slice(Magic.Length + NonceSize, TagSize);
        var ciphertext = payload[(Magic.Length + NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(Key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The .rw workflow file is corrupted or was tampered with.", ex);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Read a workflow file as JSON. Encrypted .rw files are decrypted; legacy .json files
    /// remain readable for compatibility and developer conversion. When the primary file is
    /// missing, the last atomically-created backup is used as a recovery point.
    /// </summary>
    public static string ReadWorkflowJsonFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var primaryPath = path;
        if (!File.Exists(primaryPath))
        {
            var backupPath = GetBackupPath(primaryPath);
            if (File.Exists(backupPath))
                primaryPath = backupPath;
            else
                throw new FileNotFoundException("Workflow file not found.", path);
        }

        var payload = File.ReadAllBytes(primaryPath);
        return HasRwExtension(primaryPath) || IsEncrypted(payload)
            ? Decrypt(payload)
            : Encoding.UTF8.GetString(payload);
    }

    /// <summary>
    /// Write workflow JSON to disk as .rw, or as plain JSON when explicitly requested.
    /// Existing destinations are replaced atomically and the previous file is retained as
    /// a sibling .bak recovery copy. Serialization uses a unique temporary file and cleans
    /// it up on every failure path.
    /// </summary>
    public static void WriteWorkflowFile(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(json);

        var bytes = HasRwExtension(path) ? Encrypt(json) : Encoding.UTF8.GetBytes(json);
        WriteAtomicBytes(path, bytes);
    }

    private static string GetBackupPath(string path) => path + BackupExtension;

    private static void WriteAtomicBytes(string path, byte[] payload)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = PathGates.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(
                directory ?? Directory.GetCurrentDirectory(),
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            var backupPath = GetBackupPath(fullPath);

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { /* Best-effort cleanup; the unique temp name is safe to leave behind. */ }
                }
            }
        }
    }
}
