using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CamoConnector.Detector;

internal sealed class PersistentOcr : IDisposable
{
    private const string TessLibrary = "libtesseract-5.dll";
    private const string PixLibrary = "libleptonica-6.dll";
    private readonly ConcurrentBag<IntPtr> engines = new();
    private readonly SemaphoreSlim slots = new(Math.Clamp(Environment.ProcessorCount, 1, 5));
    private readonly string models;

    private PersistentOcr(string models) => this.models = models;

    public static PersistentOcr? Open(string runtime, string models)
    {
        const uint bundledAndDefaultDirectories = 0x1100;
        var tess = LoadLibraryEx(Path.Combine(runtime, TessLibrary), IntPtr.Zero, bundledAndDefaultDirectories);
        if (tess == IntPtr.Zero) return null;
        var pix = LoadLibraryEx(Path.Combine(runtime, PixLibrary), IntPtr.Zero, bundledAndDefaultDirectories);
        if (pix == IntPtr.Zero) return null;
        NativeLibrary.SetDllImportResolver(typeof(PersistentOcr).Assembly, (name, _, _) => name switch
        {
            TessLibrary => tess,
            PixLibrary => pix,
            _ => IntPtr.Zero
        });
        var pool = new PersistentOcr(models);
        try
        {
            pool.engines.Add(pool.CreateEngine());
            return pool;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or IOException)
        {
            pool.Dispose();
            Console.Error.WriteLine("Persistent OCR unavailable: " + error.Message);
            return null;
        }
    }

    public async Task<string> ReadAsync(ArraySegment<byte> image, int pageMode, CancellationToken cancel)
    {
        await slots.WaitAsync(cancel);
        try { return await Task.Run(() => Read(image, pageMode), cancel); }
        finally { slots.Release(); }
    }

    private string Read(ArraySegment<byte> image, int pageMode)
    {
        if (!engines.TryTake(out var engine)) engine = CreateEngine();
        try
        {
            var memory = GCHandle.Alloc(image.Array!, GCHandleType.Pinned);
            IntPtr pix;
            try { pix = pixReadMem(memory.AddrOfPinnedObject() + image.Offset, (nuint)image.Count); }
            finally { memory.Free(); }
            if (pix == IntPtr.Zero) throw new IOException("OCR could not decode the cropped frame.");
            try
            {
                TessBaseAPISetPageSegMode(engine, pageMode);
                TessBaseAPISetImage2(engine, pix);
                var text = TessBaseAPIGetUTF8Text(engine);
                if (text == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUTF8(text) ?? ""; }
                finally { TessDeleteText(text); }
            }
            finally
            {
                TessBaseAPIClear(engine);
                pixDestroy(ref pix);
            }
        }
        finally { engines.Add(engine); }
    }

    private IntPtr CreateEngine()
    {
        var engine = TessBaseAPICreate();
        if (engine == IntPtr.Zero) throw new IOException("Could not allocate a Tesseract engine.");
        if (TessBaseAPIInit3(engine, models, "eng") == 0) return engine;
        TessBaseAPIDelete(engine);
        throw new IOException("Tesseract could not load the English OCR model.");
    }

    public void Dispose()
    {
        while (engines.TryTake(out var engine)) TessBaseAPIDelete(engine);
        slots.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TessBaseAPICreate();
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TessBaseAPIInit3(IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string language);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPISetPageSegMode(IntPtr engine, int mode);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPISetImage2(IntPtr engine, IntPtr pix);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TessBaseAPIGetUTF8Text(IntPtr engine);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessDeleteText(IntPtr text);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPIClear(IntPtr engine);
    [DllImport(TessLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void TessBaseAPIDelete(IntPtr engine);
    [DllImport(PixLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pixReadMem(IntPtr bytes, nuint length);
    [DllImport(PixLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pixDestroy(ref IntPtr pix);
}
