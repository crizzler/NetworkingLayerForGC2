using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
#if UNITY_6000_6_OR_NEWER
using UnityEditor.AssetPackage;
#endif

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// A single asset entry read back out of an exported <c>.unitypackage</c> archive.
    /// </summary>
    public sealed class NetworkUnityPackageEntry
    {
        public string Guid { get; internal set; }
        public string Pathname { get; internal set; }

        /// <summary>Serialized asset bytes; <c>null</c> when the entry describes a folder.</summary>
        public byte[] Asset { get; internal set; }

        /// <summary>Exact <c>.meta</c> bytes read from the archive; <c>null</c> when absent.</summary>
        public byte[] AssetMeta { get; internal set; }

        public bool IsFolder => Asset == null;

        /// <summary>GUID recorded inside the <c>.meta</c> payload, when one is present.</summary>
        public string MetaGuid
        {
            get
            {
                if (AssetMeta == null) return null;

                string text = Encoding.UTF8.GetString(AssetMeta);
                const string marker = "guid: ";
                int index = text.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) return null;

                index += marker.Length;
                int end = index;
                while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
                return end > index ? text.Substring(index, end - index) : null;
            }
        }
    }

    /// <summary>
    /// Outcome of a single export, including the post-export signature audit.
    /// </summary>
    public readonly struct NetworkPackageExportResult
    {
        public NetworkPackageExportResult(
            string packagePath,
            int assetCount,
            long bytes,
            string sha256,
            bool signed,
            string signatureStatus)
        {
            PackagePath = packagePath;
            AssetCount = assetCount;
            Bytes = bytes;
            Sha256 = sha256;
            Signed = signed;
            SignatureStatus = signatureStatus;
        }

        public string PackagePath { get; }
        public int AssetCount { get; }
        public long Bytes { get; }
        public string Sha256 { get; }
        public bool Signed { get; }
        public string SignatureStatus { get; }
    }

    /// <summary>
    /// Unity 6000.6 replaced <c>AssetDatabase.ExportPackage</c> with
    /// <c>UnityEditor.AssetPackage.Package.Export</c>, which additionally accepts the owner
    /// organization id. Passing that id makes the Editor request a package-registry attestation
    /// (<c>&lt;guid&gt;.attestation.p7m</c>) and embed it in the archive; without it, or without a
    /// signed-in Editor session, 6000.6+ imports report the package as unsigned.
    ///
    /// This helper centralizes the owner identity and the archive audit so every installer the
    /// Networking Layer ships is produced the same way:
    /// the transport release packages (Publisher tooling), the per-feature demo installers rebuilt
    /// by the editor utilities, and ad-hoc maintainer exports.
    ///
    /// The attestation is only issued for an organization owned by the signed-in Unity account, so
    /// this must run inside an interactive, authenticated Editor. Headless batch runs fail with
    /// "Package signature failed: Access token is not set." and are therefore only acceptable when
    /// <c>GC2_ALLOW_UNSIGNED=1</c> is set deliberately.
    /// </summary>
    public static class NetworkPackageSigning
    {
        /// <summary>Arawn Software Publishing: the organization that signs every shipped package.</summary>
        public const string OwnerOrgId = "9071755743287";

        public const string OwnerOrgName = "ARAWN SOFTWARE PUBLISHING";

        /// <summary>Archive entry suffix Unity uses for the embedded registry attestation.</summary>
        public const string AttestationEntrySuffix = ".attestation.p7m";

        /// <summary>Set to <c>1</c> to turn an unsigned export into a warning instead of a failure.</summary>
        public const string AllowUnsignedEnvironmentVariable = "GC2_ALLOW_UNSIGNED";

        /// <summary>
        /// True when the running Editor can attach a registry attestation. Unity 6000.5 and
        /// earlier cannot, regardless of the signed-in state.
        /// </summary>
        public static bool CanAttachAttestation
        {
            get
            {
#if UNITY_6000_6_OR_NEWER
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>True when <c>GC2_ALLOW_UNSIGNED=1</c> is set for this Editor process.</summary>
        public static bool UnsignedExportsAllowed
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(AllowUnsignedEnvironmentVariable);
                return !string.IsNullOrEmpty(value) && value.Trim() == "1";
            }
        }

        /// <summary>
        /// Exports <paramref name="assetPaths"/> to <paramref name="outputAbsolutePath"/> and then
        /// re-reads the archive to report whether the Arawn attestation was embedded.
        /// </summary>
        /// <param name="context">Human-readable producer name used in log and error messages.</param>
        public static NetworkPackageExportResult Export(
            IReadOnlyList<string> assetPaths,
            string outputAbsolutePath,
            ExportPackageOptions flags,
            string context)
        {
            if (assetPaths == null || assetPaths.Count == 0)
                throw new ArgumentException("At least one asset path is required.", nameof(assetPaths));
            if (string.IsNullOrWhiteSpace(outputAbsolutePath))
                throw new ArgumentException("An output path is required.", nameof(outputAbsolutePath));

            string[] paths = new string[assetPaths.Count];
            for (int i = 0; i < assetPaths.Count; i++) paths[i] = assetPaths[i];

            string fullOutput = Path.GetFullPath(outputAbsolutePath);
            string directory = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(fullOutput)) File.Delete(fullOutput);

#if UNITY_6000_6_OR_NEWER
            // Passing the owner organization id is what triggers the registry attestation.
            Package.Export(new ExportPackageParameters(
                paths,
                fullOutput,
                OwnerOrgId,
                flags));
#else
            AssetDatabase.ExportPackage(paths, fullOutput, flags);
#endif

            if (!File.Exists(fullOutput))
            {
                throw new InvalidOperationException(
                    $"{context}: Unity did not write '{fullOutput}'.");
            }

            ReadUnityPackage(fullOutput, out List<string> pathnames, out byte[] attestationBytes);

            bool signed = AttestationIdentifiesOwner(attestationBytes);
            string status;
            if (signed)
            {
                status = $"signed by {OwnerOrgName}";
            }
            else if (attestationBytes != null && attestationBytes.Length > 0)
            {
                status = $"attestation present but it does not identify {OwnerOrgName}";
            }
            else if (CanAttachAttestation)
            {
                status =
                    "UNSIGNED: Unity attached no registry attestation. Sign in to the Unity Editor " +
                    "with an account that owns " + OwnerOrgName + " and export again.";
            }
            else
            {
                status =
                    $"UNSIGNED: Unity {UnityEngine.Application.unityVersion} cannot attach registry " +
                    "attestations; export from Unity 6000.6 or newer.";
            }

            return new NetworkPackageExportResult(
                fullOutput,
                pathnames.Count,
                new FileInfo(fullOutput).Length,
                ComputeSha256(fullOutput),
                signed,
                status);
        }

        /// <summary>
        /// Exports and throws unless the archive carries the Arawn attestation, unless
        /// <c>GC2_ALLOW_UNSIGNED=1</c> is set (in which case the unsigned result is logged loudly).
        /// </summary>
        public static NetworkPackageExportResult ExportSigned(
            IReadOnlyList<string> assetPaths,
            string outputAbsolutePath,
            ExportPackageOptions flags,
            string context)
        {
            NetworkPackageExportResult result = Export(assetPaths, outputAbsolutePath, flags, context);

            if (result.Signed)
            {
                UnityEngine.Debug.Log(
                    $"[GC2-PACKAGE-SIGN] {context}: {result.SignatureStatus} " +
                    $"({result.AssetCount} paths, {result.Bytes} bytes, sha256 {result.Sha256}).");
                return result;
            }

            const string message =
                "{0}: package is not signed by {1}. {2} " +
                "Set GC2_ALLOW_UNSIGNED=1 to accept an unsigned build deliberately.";

            if (!UnsignedExportsAllowed)
            {
                throw new InvalidOperationException(
                    string.Format(message, context, OwnerOrgName, result.SignatureStatus));
            }

            UnityEngine.Debug.LogWarning(
                "[GC2-PACKAGE-SIGN] " + string.Format(message, context, OwnerOrgName, result.SignatureStatus));
            return result;
        }

        /// <summary>Reads the asset pathnames and any embedded attestation from an archive.</summary>
        public static void ReadUnityPackage(
            string packageAbsolutePath,
            out List<string> pathnames,
            out byte[] attestationBytes)
        {
            pathnames = new List<string>();
            attestationBytes = null;

            using var file = File.OpenRead(packageAbsolutePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);

            var header = new byte[512];
            string pendingLongName = null;

            while (TryReadHeader(gzip, header))
            {
                if (IsZeroBlock(header)) break;

                string name = pendingLongName ?? ReadNullTerminatedAscii(header, 0, 100);
                pendingLongName = null;

                long size = ParseOctal(header, 124, 12);
                char typeFlag = (char)header[156];

                if (typeFlag == 'L')
                {
                    // GNU long-name entry: carries the next member's name.
                    pendingLongName = DecodeTarString(ReadEntryBytes(gzip, size));
                }
                else if (name.EndsWith("/pathname", StringComparison.Ordinal))
                {
                    byte[] data = ReadEntryBytes(gzip, size);
                    string path = Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' ');
                    if (path.Length > 0) pathnames.Add(path);
                }
                else if (name.EndsWith(AttestationEntrySuffix, StringComparison.Ordinal))
                {
                    attestationBytes = ReadEntryBytes(gzip, size);
                }
                else
                {
                    SkipEntryBytes(gzip, size);
                }

                SkipPadding(gzip, size);
            }
        }

        /// <summary>Reads every asset entry of an archive, keyed by its package GUID folder.</summary>
        public static List<NetworkUnityPackageEntry> ReadEntries(string packageAbsolutePath)
        {
            var entries = new List<NetworkUnityPackageEntry>();
            var byGuid = new Dictionary<string, NetworkUnityPackageEntry>(StringComparer.Ordinal);

            using var file = File.OpenRead(packageAbsolutePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);

            var header = new byte[512];
            string pendingLongName = null;

            while (TryReadHeader(gzip, header))
            {
                if (IsZeroBlock(header)) break;

                string name = pendingLongName ?? ReadNullTerminatedAscii(header, 0, 100);
                pendingLongName = null;

                long size = ParseOctal(header, 124, 12);
                char typeFlag = (char)header[156];

                if (typeFlag == 'L')
                {
                    pendingLongName = DecodeTarString(ReadEntryBytes(gzip, size));
                    SkipPadding(gzip, size);
                    continue;
                }

                string normalized = name.StartsWith("./", StringComparison.Ordinal) ? name.Substring(2) : name;
                string[] parts = normalized.Split('/');
                if (parts.Length != 2 ||
                    !(parts[1] == "asset" || parts[1] == "asset.meta" || parts[1] == "pathname"))
                {
                    SkipEntryBytes(gzip, size);
                    SkipPadding(gzip, size);
                    continue;
                }

                byte[] payload = ReadEntryBytes(gzip, size);
                SkipPadding(gzip, size);

                if (!byGuid.TryGetValue(parts[0], out NetworkUnityPackageEntry entry))
                {
                    entry = new NetworkUnityPackageEntry { Guid = parts[0] };
                    byGuid[parts[0]] = entry;
                    entries.Add(entry);
                }

                switch (parts[1])
                {
                    case "asset":
                        entry.Asset = payload;
                        break;
                    case "asset.meta":
                        entry.AssetMeta = payload;
                        break;
                    default:
                        entry.Pathname = Encoding.UTF8.GetString(payload).Trim('\0', '\r', '\n', ' ');
                        break;
                }
            }

            entries.RemoveAll(entry => string.IsNullOrEmpty(entry.Pathname));
            return entries;
        }

        /// <summary>True when the attestation payload names both the Arawn org id and org name.</summary>
        public static bool AttestationIdentifiesOwner(byte[] attestationBytes)
        {
            return ContainsAscii(attestationBytes, "\"ownerOrgId\":\"" + OwnerOrgId + "\"") &&
                   ContainsAscii(attestationBytes, "\"ownerOrgName\":\"" + OwnerOrgName + "\"");
        }

        /// <summary>
        /// Reports whether an existing archive carries the Arawn attestation, without re-exporting.
        /// Used to audit installers embedded in another package before that package is shipped.
        /// </summary>
        public static bool IsPackageSigned(string packageAbsolutePath, out string status)
        {
            ReadUnityPackage(packageAbsolutePath, out List<string> pathnames, out byte[] attestationBytes);

            if (AttestationIdentifiesOwner(attestationBytes))
            {
                status = $"signed by {OwnerOrgName} ({pathnames.Count} paths)";
                return true;
            }

            if (attestationBytes != null && attestationBytes.Length > 0)
            {
                status = "attestation present but it does not identify " + OwnerOrgName;
                return false;
            }

            status = CanAttachAttestation
                ? "no registry attestation; timestamp-unsigned"
                : "no registry attestation; Unity " + UnityEngine.Application.unityVersion + " cannot attest";
            return false;
        }

        public static string ComputeSha256(string packageAbsolutePath)
        {
            using FileStream stream = File.OpenRead(packageAbsolutePath);
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(stream);

            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        private static bool ContainsAscii(byte[] data, string value)
        {
            if (data == null || data.Length == 0 || value.Length == 0) return false;

            byte[] needle = Encoding.ASCII.GetBytes(value);
            int lastStart = data.Length - needle.Length;

            for (int index = 0; index <= lastStart; index++)
            {
                bool match = true;

                for (int offset = 0; offset < needle.Length; offset++)
                {
                    if (data[index + offset] != needle[offset])
                    {
                        match = false;
                        break;
                    }
                }

                if (match) return true;
            }

            return false;
        }

        private static bool TryReadHeader(Stream stream, byte[] header)
        {
            int offset = 0;

            while (offset < header.Length)
            {
                int read = stream.Read(header, offset, header.Length - offset);
                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new InvalidDataException("Truncated Unity package header.");
                }

                offset += read;
            }

            return true;
        }

        private static bool IsZeroBlock(byte[] block)
        {
            for (int index = 0; index < block.Length; index++)
            {
                if (block[index] != 0) return false;
            }

            return true;
        }

        private static string ReadNullTerminatedAscii(byte[] block, int offset, int length)
        {
            int end = offset;
            int limit = offset + length;

            while (end < limit && block[end] != 0) end++;

            return Encoding.ASCII.GetString(block, offset, end - offset).Trim();
        }

        private static long ParseOctal(byte[] block, int offset, int length)
        {
            long value = 0;

            foreach (char character in ReadNullTerminatedAscii(block, offset, length))
            {
                if (character < '0' || character > '7') continue;
                value = (value * 8) + (character - '0');
            }

            return value;
        }

        private static byte[] ReadEntryBytes(Stream stream, long size)
        {
            if (size <= 0) return Array.Empty<byte>();

            var data = new byte[size];
            if (!TryReadExactly(stream, data, data.Length))
            {
                throw new InvalidDataException("Unexpected end of Unity package while reading an entry.");
            }

            return data;
        }

        private static void SkipEntryBytes(Stream stream, long size)
        {
            long remaining = size;
            var buffer = new byte[8192];

            while (remaining > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, chunk);
                if (read <= 0)
                {
                    throw new InvalidDataException("Unexpected end of Unity package while skipping an entry.");
                }

                remaining -= read;
            }
        }

        private static void SkipPadding(Stream stream, long size)
        {
            int padding = (int)((512 - (size % 512)) % 512);
            SkipEntryBytes(stream, padding);
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;

            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }

            return true;
        }

        private static string DecodeTarString(byte[] data)
        {
            int end = Array.IndexOf(data, (byte)0);
            if (end < 0) end = data.Length;
            return Encoding.UTF8.GetString(data, 0, end).Trim();
        }
    }
}
