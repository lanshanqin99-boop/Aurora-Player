
namespace Aurora;

/// <summary>
/// Reads cover art, analyses it and caches the resulting palette. This is the only module
/// the UI talks to; it knows nothing about playback and playback knows nothing about it, so
/// the palette pipeline can be changed without touching the service that owns the queue.
///
/// Two guarantees shape the design:
/// <list type="bullet">
/// <item><b>Nothing runs on the UI thread.</b> Tag reading, decoding and the histogram all
/// happen inside a background task, so a slow disk or a large cover cannot stall a
/// frame.</item>
/// <item><b>Every failure is black.</b> No cover, damaged cover, unknown format, a file
/// that vanished — all of them produce <see cref="AmbientPalette.Black"/> rather than an
/// exception, because a missing colour must never affect playback.</item>
/// </list>
/// </summary>
internal sealed class CoverPaletteService
{
    /// <summary>Distinct files kept in memory. A whole album plus its neighbours fits.</summary>
    private const int MaxEntries = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    /// <summary>
    /// Returns the palette for a file, reading and analysing it on a background thread the
    /// first time. The cache key is the path plus the file's size and last-write time, so a
    /// re-tagged file is re-analysed while an unchanged one is served from memory.
    /// </summary>
    public async Task<AmbientPalette> GetPaletteAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return AmbientPalette.Black;
        }

        FileStamp stamp = FileStamp.Read(filePath);
        string key = $"{filePath}|{stamp.Length}|{stamp.Ticks}";

        if (TryGetCached(key, out AmbientPalette cached))
        {
            return cached;
        }

        AmbientPalette palette = await Task.Run(() => Analyse(filePath)).ConfigureAwait(true);

        Store(key, palette);
        return palette;
    }

    /// <summary>
    /// The off-thread pipeline. Synchronous by design: it is only ever invoked from
    /// <see cref="Task.Run(Action)"/>, which keeps the async surface in one place.
    /// </summary>
    private static AmbientPalette Analyse(string filePath)
    {
        try
        {
            CoverImage? cover = CoverArtReader.ReadEmbeddedCover(filePath);
            if (cover is null)
            {
                return AmbientPalette.Black;
            }

            PixelSample? sample = CoverPixelSampler.SampleAsync(cover).GetAwaiter().GetResult();
            if (sample is null)
            {
                return AmbientPalette.Black;
            }

            return PaletteExtractor.Extract(sample);
        }
        catch (Exception)
        {
            // Whatever went wrong, the player keeps working with a black background.
            return AmbientPalette.Black;
        }
    }

    /// <summary>
    /// Returns the palette for a picture that is a file in its own right, rather than a cover
    /// embedded in an audio file. The default background is such a picture, and it is coloured by
    /// exactly the same route a cover takes: the same decoder, the same 48px sample, the same
    /// extractor, the same cache and the same "every failure is black" rule. Only where the bytes
    /// come from differs.
    /// </summary>
    public async Task<AmbientPalette> GetImagePaletteAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return AmbientPalette.Black;
        }

        FileStamp stamp = FileStamp.Read(imagePath);
        string key = $"image|{imagePath}|{stamp.Length}|{stamp.Ticks}";

        if (TryGetCached(key, out AmbientPalette cached))
        {
            return cached;
        }

        AmbientPalette palette = await Task.Run(() => AnalyseImage(imagePath)).ConfigureAwait(true);

        Store(key, palette);
        return palette;
    }

    /// <summary>
    /// The off-thread pipeline for a picture file. The bytes are read here and handed on as a
    /// <see cref="CoverImage"/> purely because that is what the sampler takes; nothing about the
    /// sampling or the statistics changes.
    /// </summary>
    private static AmbientPalette AnalyseImage(string imagePath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(imagePath);
            if (data.Length == 0)
            {
                return AmbientPalette.Black;
            }

            var image = new CoverImage(data, MimeTypeFor(imagePath));

            PixelSample? sample = CoverPixelSampler.SampleAsync(image).GetAwaiter().GetResult();
            if (sample is null)
            {
                return AmbientPalette.Black;
            }

            return PaletteExtractor.Extract(sample);
        }
        catch (Exception)
        {
            // Whatever went wrong, the default surface keeps working with no colour of its own.
            return AmbientPalette.Black;
        }
    }

    /// <summary>Only used to label the sample; the decoder reads the real format from the bytes.</summary>
    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private bool TryGetCached(string key, out AmbientPalette palette)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out CacheEntry? entry) && entry is not null)
            {
                // Refresh recency.
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                palette = entry.Palette;
                return true;
            }
        }

        palette = AmbientPalette.Black;
        return false;
    }

    private void Store(string key, AmbientPalette palette)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out CacheEntry? existing) && existing is not null)
            {
                existing.Palette = palette;
                _order.Remove(existing.Node);
                _order.AddFirst(existing.Node);
                return;
            }

            LinkedListNode<string> node = _order.AddFirst(key);
            _cache[key] = new CacheEntry(palette, node);

            // Oldest first, so the cache stays bounded without a scan.
            while (_cache.Count > MaxEntries && _order.Last is LinkedListNode<string> last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value);
            }
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(AmbientPalette palette, LinkedListNode<string> node)
        {
            Palette = palette;
            Node = node;
        }

        public AmbientPalette Palette { get; set; }

        public LinkedListNode<string> Node { get; }
    }

    /// <summary>
    /// File identity used for invalidation. Reading it never throws and never fails hard:
    /// an unreadable file reports a stamp of zero, which still caches the black result so a
    /// broken track is not re-probed on every track change.
    /// </summary>
    private readonly record struct FileStamp(long Length, long Ticks)
    {
        public static FileStamp Read(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
