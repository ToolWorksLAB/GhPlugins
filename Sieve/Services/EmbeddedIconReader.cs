using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;

namespace Sieve.Services
{
    /// <summary>
    /// Reads high-confidence named PNG resources without loading or executing plugin code.
    /// </summary>
    internal static class EmbeddedIconReader
    {
        private const int MaximumPluginBytes = 128 * 1024 * 1024;
        private const int MaximumIconBytes = 256 * 1024;
        private const int MinimumConfidence = 75;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static string ReadPngDataUrl(string path, string pluginName)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length <= PngSignature.Length || file.Length > MaximumPluginBytes)
                    return string.Empty;

                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader == null)
                    return string.Empty;

                var metadata = peReader.GetMetadataReader();
                var resources = peReader.PEHeaders.CorHeader.ResourcesDirectory;
                if (resources.Size <= 0)
                    return string.Empty;

                var candidates = new List<IconCandidate>();
                foreach (var handle in metadata.ManifestResources)
                {
                    var resource = metadata.GetManifestResource(handle);
                    if (!resource.Implementation.IsNil)
                        continue;

                    var resourceName = metadata.GetString(resource.Name);
                    var bytes = ReadEmbeddedResource(peReader, resources.RelativeVirtualAddress, resource.Offset);
                    if (bytes.Length == 0)
                        continue;

                    FindPngCandidates(bytes, resourceName, pluginName, candidates);
                    if (resourceName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                        ReadResourceEntries(bytes, pluginName, candidates);
                }

                var best = candidates
                    .Where(candidate => candidate.Score >= MinimumConfidence)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => Math.Abs(candidate.Width - 24) + Math.Abs(candidate.Height - 24))
                    .ThenBy(candidate => candidate.Bytes.Length)
                    .FirstOrDefault();
                return best == null
                    ? string.Empty
                    : "data:image/png;base64," + Convert.ToBase64String(best.Bytes);
            }
            catch
            {
                // Icons are cosmetic. A plugin remains available if metadata is malformed or inaccessible.
                return string.Empty;
            }
        }

        private static byte[] ReadEmbeddedResource(PEReader peReader, int resourcesRva, long resourceOffset)
        {
            try
            {
                var content = peReader.GetSectionData(resourcesRva + checked((int)resourceOffset)).GetContent();
                if (content.Length < 4)
                    return Array.Empty<byte>();

                var length = content[0] | (content[1] << 8) | (content[2] << 16) | (content[3] << 24);
                if (length <= 0 || length > content.Length - 4)
                    return Array.Empty<byte>();

                return content.Skip(4).Take(length).ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static void ReadResourceEntries(byte[] resourceBytes, string pluginName, List<IconCandidate> candidates)
        {
            try
            {
                using var stream = new MemoryStream(resourceBytes, writable: false);
                using var reader = new ResourceReader(stream);
                var entries = reader.GetEnumerator();
                while (entries.MoveNext())
                {
                    var entryName = entries.Key as string;
                    if (string.IsNullOrWhiteSpace(entryName))
                        continue;

                    try
                    {
                        reader.GetResourceData(entryName, out _, out var data);
                        FindPngCandidates(data, entryName, pluginName, candidates);
                    }
                    catch
                    {
                        // One malformed resource must not discard the remaining named resources.
                    }
                }
            }
            catch
            {
                // Not every manifest resource ending in .resources is a readable ResourceReader stream.
            }
        }

        private static void FindPngCandidates(byte[] bytes, string resourceName, string pluginName, List<IconCandidate> candidates)
        {
            for (var offset = 0; offset <= bytes.Length - PngSignature.Length; offset++)
            {
                if (!MatchesPngSignature(bytes, offset) ||
                    !TryGetPngInfo(bytes, offset, out var length, out var width, out var height))
                    continue;
                if (length > MaximumIconBytes || width <= 0 || height <= 0 || width > 512 || height > 512)
                    continue;

                var png = new byte[length];
                Buffer.BlockCopy(bytes, offset, png, 0, length);
                candidates.Add(new IconCandidate
                {
                    Bytes = png,
                    Width = width,
                    Height = height,
                    Score = ScoreResource(resourceName, pluginName, width, height)
                });
                offset += Math.Max(PngSignature.Length - 1, length - 1);
            }
        }

        private static int ScoreResource(string resourceName, string pluginName, int width, int height)
        {
            var name = (resourceName ?? string.Empty).ToLowerInvariant();
            var normalizedResource = Normalize(resourceName);
            var normalizedPlugin = Normalize(pluginName);
            var score = 0;

            if (normalizedPlugin.Length >= 4 && normalizedResource.Contains(normalizedPlugin, StringComparison.Ordinal))
                score += 60;
            if (name.Contains("logo", StringComparison.Ordinal))
                score += 70;
            if (name.Contains("assembly", StringComparison.Ordinal))
                score += 40;
            if (name.Contains("plugin", StringComparison.Ordinal))
                score += 35;
            if (name.Contains("icon", StringComparison.Ordinal))
                score += 25;
            if (name.EndsWith(".icon", StringComparison.Ordinal) || name.EndsWith("_icon", StringComparison.Ordinal))
                score += 20;

            if (width == height)
                score += 10;
            if (width == 24 || width == 32 || width == 48 || width == 64 || width == 128)
                score += 10;

            foreach (var penalty in new[] { "component", "button", "cursor", "preview", "splash", "banner", "toolbar" })
            {
                if (name.Contains(penalty, StringComparison.Ordinal))
                    score -= 50;
            }

            if (width < 20 || height < 20)
                score -= 20;
            if (Math.Max(width, height) > Math.Min(width, height) * 2)
                score -= 25;
            return score;
        }

        private static string Normalize(string value)
        {
            return new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static bool MatchesPngSignature(byte[] bytes, int offset)
        {
            for (var index = 0; index < PngSignature.Length; index++)
            {
                if (bytes[offset + index] != PngSignature[index])
                    return false;
            }
            return true;
        }

        private static bool TryGetPngInfo(byte[] bytes, int start, out int length, out int width, out int height)
        {
            length = 0;
            width = 0;
            height = 0;
            if (start + 24 > bytes.Length || bytes[start + 12] != (byte)'I' || bytes[start + 13] != (byte)'H' ||
                bytes[start + 14] != (byte)'D' || bytes[start + 15] != (byte)'R')
                return false;

            width = ReadBigEndianInt32(bytes, start + 16);
            height = ReadBigEndianInt32(bytes, start + 20);
            var cursor = start + PngSignature.Length;
            for (var chunks = 0; chunks < 128 && cursor + 12 <= bytes.Length; chunks++)
            {
                var dataLength = ReadBigEndianInt32(bytes, cursor);
                if (dataLength < 0 || dataLength > MaximumIconBytes || cursor + 12L + dataLength > bytes.Length)
                    return false;

                var isEnd = bytes[cursor + 4] == (byte)'I' && bytes[cursor + 5] == (byte)'E' &&
                    bytes[cursor + 6] == (byte)'N' && bytes[cursor + 7] == (byte)'D';
                cursor += 12 + dataLength;
                if (isEnd)
                {
                    length = cursor - start;
                    return true;
                }
            }
            return false;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private sealed class IconCandidate
        {
            public byte[] Bytes { get; set; } = Array.Empty<byte>();
            public int Width { get; set; }
            public int Height { get; set; }
            public int Score { get; set; }
        }
    }
}
