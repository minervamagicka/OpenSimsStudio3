using System.Collections.Concurrent;
using System.Numerics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Rendering;

namespace OpenSimsStudio3.App;

/// <summary>
/// Renders item-browser thumbnails (the part's most detailed mesh, untextured) on one worker thread that owns the
/// Vulkan renderer. Requests carry a generation: a new filter supersedes queued work. Rendered images are EA-derived,
/// so they live in memory only and are never written anywhere.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public const int Size = 280;

    readonly IResourceSource _game;
    readonly BlockingCollection<(int Generation, CasItem Item, Action<Bitmap?> Done)> _queue = new();
    readonly ConcurrentDictionary<ulong, Bitmap?> _cache = new();
    readonly Thread _worker;
    int _generation;

    public ThumbnailService(IResourceSource game)
    {
        _game = game;
        _worker = new Thread(Work) { IsBackground = true, Name = "OSS3 thumbnails" };
        _worker.Start();
    }

    /// <summary>Drops queued requests from earlier calls and queues these in order. Callbacks run on the UI thread.</summary>
    public void Request(IEnumerable<(CasItem Item, Action<Bitmap?> Done)> items)
    {
        var generation = Interlocked.Increment(ref _generation);
        foreach (var (item, done) in items)
        {
            if (_cache.TryGetValue(item.Instance, out var cached)) { done(cached); continue; }
            _queue.Add((generation, item, done));
        }
    }

    void Work()
    {
        OffscreenMeshRenderer? renderer = null;
        try
        {
            renderer = new OffscreenMeshRenderer { ClearColor = new Vector4(0.047f, 0.063f, 0.086f, 1f) };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"OSS3: no Vulkan renderer for thumbnails ({error.Message}); tiles keep their placeholders.");
        }
        var pixels = new byte[Size * Size * 4];
        foreach (var (generation, item, done) in _queue.GetConsumingEnumerable())
        {
            if (generation != Volatile.Read(ref _generation) || renderer == null) continue;
            if (_cache.TryGetValue(item.Instance, out var cached)) { Dispatcher.UIThread.Post(() => done(cached)); continue; }
            Bitmap? bitmap = null;
            try
            {
                var mesh = CasMeshes.Lod0(_game, new ResourceKey(0x034AEECB, item.Group, item.Instance));
                if (mesh != null)
                {
                    Thumbnails.Draw(renderer, mesh, Size, pixels);
                    bitmap = ToBitmap(pixels);
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"OSS3: thumbnail for {item.Key} ({item.Name}) failed: {error.Message}");
            }
            _cache[item.Instance] = bitmap;
            Dispatcher.UIThread.Post(() => done(bitmap));
        }
        renderer?.Dispose();
    }

    static WriteableBitmap ToBitmap(byte[] bgra)
    {
        var bitmap = new WriteableBitmap(new PixelSize(Size, Size), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var target = bitmap.Lock();
        for (var y = 0; y < Size; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgra, y * Size * 4, target.Address + y * target.RowBytes, Size * 4);
        return bitmap;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _queue.CompleteAdding();
    }
}
