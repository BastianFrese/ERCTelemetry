using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ERCTelemetry.App.Clips;

/// <summary>HDR-correct screen capture via Windows.Graphics.Capture. The frame pool
/// requests SDR (B8G8R8A8UIntNormalized), so the system tone-maps HDR content to SDR
/// automatically — no more overexposed clips (GDI CopyFromScreen reads HDR backbuffer
/// values as SDR and blows out the highlights). Frames are read to CPU via
/// SoftwareBitmap.CreateCopyFromSurfaceAsync and emitted as JPEG. Pure capture: no
/// storage or encode-to-file logic, so it is easy to swap for a different source.</summary>
public sealed partial class HdrFrameSource : IDisposable
{
    private const int JpegQuality = 80;

    /// <summary>Nach so vielen aufeinanderfolgenden Frame-Fehlern gilt das Capture-Device
    /// als verloren und die Session wird neu gestartet (statt stumm weiterzufallen). Ein
    /// einzelner Fehler (kurzzeitiger Locked Desktop) soll NICHT neu starten — erst eine
    /// Serie zeigt ein echtes Problem (GPU-TDR / RDP-Disconnect).</summary>
    private const int MaxConsecutiveCaptureFailures = 3;

    private readonly object _gate = new();
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private bool _started;
    private int _consecutiveFailures;
    private int _disposed;

    private volatile int _maxWidth = 1280;

    /// <summary>Maximum clip width in pixels; the aspect ratio is preserved. Changing
    /// this while capturing requires a Stop + Start (the frame pool is sized from it).
    /// Volatile: written by the manage loop, read on the frame-arrived thread.</summary>
    public int MaxWidth
    {
        get => _maxWidth;
        set => _maxWidth = value;
    }

    /// <summary>Aktuelle Item-Breite in Pixeln, oder null solange nicht gestartet. Der
    /// ManageLoop vergleicht sie periodisch, um einen Auflösungswechsel mitten in der
    /// Session zu erkennen (Pool-Neustart, siehe ScreenCaptureService).</summary>
    public int? ItemWidth
    {
        get
        {
            lock (_gate)
            {
                return _item?.Size.Width;
            }
        }
    }

    /// <summary>Raised on a thread-pool thread for each captured frame.</summary>
    public event Action<DateTimeOffset, byte[]>? FrameCaptured;

    /// <summary>Creates the D3D11 device, the capture item for the primary display and
    /// the frame pool, then starts capturing. Safe to call again after <see cref="Stop"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            _device = CreateWinrtDevice();
            _item = CreateCaptureItemForPrimaryDisplay();
            // Two buffers, not one: with a single buffer a frame whose surface copy is
            // still in flight blocks the next delivery, dropping frames at high refresh
            // rates and slowing the real capture rate below the configured FPS.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, ScaleSize(_item.Size));
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
        }
        catch (Exception ex)
        {
            App.Log($"Screen capture init failed: {ex}");
            Stop();
        }
    }

    /// <summary>Stops capturing and releases the frame pool, session and item. The D3D11
    /// device is released too; the next <see cref="Start"/> recreates everything.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _session?.Dispose();
            _session = null;
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }

            _item = null;
            _device = null;
        }
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            DateTimeOffset utc;
            SoftwareBitmap bitmap;
            // Copy the surface to a CPU bitmap, then release the pool frame immediately —
            // holding it through the JPEG encode blocks a pool buffer, so the next display
            // frame is dropped and the real capture rate falls below the configured FPS
            // (the clip then plays too fast). Encode after the frame is free so the pool
            // keeps delivering while the encode runs.
            using (var frame = sender.TryGetNextFrame())
            {
                if (frame is null)
                {
                    return;
                }

                utc = DateTimeOffset.UtcNow;
                bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            }

            using (bitmap)
            {
                var jpeg = EncodeJpeg(bitmap);
                FrameCaptured?.Invoke(utc, jpeg);
            }

            // Ein erfolgreicher Frame beendet die Fehlerserie.
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            // Locked desktop / RDP disconnect / device lost — skip the frame.
            App.Log($"Screen capture failed: {ex.Message}");
            // Device lost (GPU-TDR / RDP-Disconnect / Monitor-Wechsel): WGC wirft dann für
            // jeden Frame. Nach einer Serie Stop(), damit der nächste ManageLoop-Tick
            // (500 ms) Pool + Device idempotent neu erstellt — ohne das bliebe die
            // Aufnahme für die ganze Session stumm und alle späteren Collision-Clips
            // hätten kein Video (MEDIUM, 2026-09-16).
            if (Interlocked.Increment(ref _consecutiveFailures) >= MaxConsecutiveCaptureFailures)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                // Stop() im async-void-Handler defensiv abfangen: wirft _session.Dispose()
                // oder _framePool.Dispose() während der TDR-Teardown (genau der Zustand, für
                // den der Restart gedacht ist), würde die Exception den WPF-Prozess kriegen
                // (async void = kein awaitable) — der nächste ManageLoop-Tick startet das
                // Capture dann trotzdem neu (LOW, 2026-09-16).
                try
                {
                    Stop();
                }
                catch (Exception stopEx)
                {
                    App.Log($"Screen capture restart failed: {stopEx.Message}");
                }
            }
        }
    }

    private byte[] EncodeJpeg(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out _);
        var plane = buffer.GetPlaneDescription(0);

        using var bmp = new Bitmap(bitmap.PixelWidth, bitmap.PixelHeight, plane.Stride, PixelFormat.Format32bppArgb, data);
        using var scaled = ScaleToMaxWidth(bmp, MaxWidth);
        using var ms = new MemoryStream();
        // Fresh parameters per call: two pool buffers now allow concurrent encodes,
        // and Bitmap.Save is not safe on a single shared EncoderParameters instance.
        scaled.Save(ms, JpegCodec, CreateJpegParams());
        return ms.ToArray();
    }

    private static Bitmap ScaleToMaxWidth(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth)
        {
            return new Bitmap(source); // copy so the caller's using disposes the original
        }

        var height = (int)Math.Round(source.Height * (maxWidth / (double)source.Width));
        var scaled = new Bitmap(maxWidth, height);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, maxWidth, height);
        }

        return scaled;
    }

    private SizeInt32 ScaleSize(SizeInt32 size)
    {
        if (size.Width <= MaxWidth)
        {
            return size;
        }

        var height = (int)Math.Round(size.Height * (MaxWidth / (double)size.Width));
        return new SizeInt32(MaxWidth, height);
    }

    private static GraphicsCaptureItem CreateCaptureItemForPrimaryDisplay()
    {
        // DisplayArea / TryCreateFromDisplayId are not projected into .NET, so go through
        // the IGraphicsCaptureItemInterop COM interface: get the primary monitor's HMONITOR
        // and ask the GraphicsCaptureItem activation factory to create an item for it.
        var hmonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var factory = WinRT.ActivationFactory.Get(
            "Windows.Graphics.Capture.GraphicsCaptureItem", typeof(IGraphicsCaptureItemInterop).GUID);
        using (factory)
        {
            // GetRef() does not AddRef — AddRef manually so FromAbi can take ownership
            // without double-releasing the factory's own reference.
            var ptr = factory.GetRef();
            Marshal.AddRef(ptr);
            var interop = WinRT.MarshalInterface<IGraphicsCaptureItemInterop>.FromAbi(ptr);
            // typeof(GraphicsCaptureItem).GUID is a hash of the type name — the projected
            // class carries no [Guid] attribute — NOT the IGraphicsCaptureItem IID that
            // CreateForMonitor QIs the capture item for. The wrong GUID fails with
            // E_NOINTERFACE ("Specified cast is not valid"). Use the real IID.
            var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            interop.CreateForMonitor(hmonitor, ref itemGuid, out var itemPtr);
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
    }

    private static IDirect3DDevice CreateWinrtDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, D3D11_SDK_VERSION,
            out var devicePtr, out _, out var contextPtr);
        if (hr < 0)
        {
            // WARP fallback: RDP sessions / VMs have no hardware adapter.
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, D3D11_SDK_VERSION,
                out devicePtr, out _, out contextPtr);
        }

        if (hr < 0)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed (0x{hr:X8})");
        }

        try
        {
            var iidDxgiDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.QueryInterface(devicePtr, in iidDxgiDevice, out var dxgiDevicePtr);
            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var pDevice);
                if (hr < 0)
                {
                    throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed (0x{hr:X8})");
                }

                // FromAbi takes ownership of the reference — do not release pDevice.
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pDevice);
            }
            finally
            {
                Marshal.Release(dxgiDevicePtr);
            }
        }
        finally
        {
            Marshal.Release(devicePtr);
            Marshal.Release(contextPtr);
        }
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static EncoderParameters CreateJpegParams()
    {
        var parameters = new EncoderParameters(1);
        // .NET 10 System.Drawing regression: the int overload of EncoderParameter
        // throws "Parameter is not valid" on Save — the long overload works.
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);
        return parameters;
    }

    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        [In] int[]? featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>COM interop for the GraphicsCaptureItem activation factory. Lets us create
    /// a capture item for a monitor (or window) without the DisplayArea projection, which
    /// the .NET Windows SDK projections do not include. GeneratedComInterface (instead of
    /// the legacy ComImport) so CsWinRT's As&lt;T&gt; can QI the activation factory for it —
    /// Marshal.GetObjectForIUnknown + ComImport cast throws InvalidCastException here.</summary>
    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr hwnd, ref Guid riid, out IntPtr result);
        void CreateForMonitor(IntPtr hmonitor, ref Guid riid, out IntPtr result);
    }

    /// <summary>COM interop for the IMemoryBufferReference's raw pixel access. GeneratedComInterface
    /// (not the legacy ComImport) so the projected reference's As&lt;T&gt; can QI it — a direct
    /// cast from the CsWinRT IInspectable wrapper throws InvalidCastException.</summary>
    [GeneratedComInterface]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    internal partial interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
    }
}
