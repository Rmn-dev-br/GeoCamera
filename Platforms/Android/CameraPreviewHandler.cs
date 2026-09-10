using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Provider;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;
using Camera = Android.Hardware.Camera;
using CameraFacing = Android.Hardware.CameraFacing;
using Paint = Android.Graphics.Paint;

namespace GeoCamera.Platforms.Android;

public sealed class CameraPreviewHandler : ViewHandler<CameraPreview, RecordingTextureView>
{
    public CameraPreviewHandler() : base(ViewMapper) { }
    protected override RecordingTextureView CreatePlatformView() => new(Context, VirtualView);
    protected override void DisconnectHandler(RecordingTextureView platformView)
    {
        platformView.Shutdown();
        base.DisconnectHandler(platformView);
    }
}

// The legacy camera API is still supported on Android and avoids adding a CameraX
// dependency for this deliberately small, rear-camera-only application.
#pragma warning disable CS0618
public sealed class RecordingTextureView : TextureView, TextureView.ISurfaceTextureListener
{
    const string ResolutionPreferenceKey = "camera.capture.resolution";

    readonly CameraPreview owner;
    Camera? camera;
    MediaRecorder? recorder;
    VideoSurfaceRenderer? renderer;
    Bitmap? frame;
    IDispatcherTimer? timer;
    string? outputPath;
    global::Android.Net.Uri? outputUri;
    global::Android.OS.ParcelFileDescriptor? outputDescriptor;
    bool requested, hasFrame;
    int frameCount;
    long lastPreviewFrame;
    List<CameraResolution> supportedResolutions = [];
    CameraResolution captureResolution = new(640, 480);

    public bool IsRecording => recorder is not null;
    public CameraResolution CaptureResolution => captureResolution;

    public RecordingTextureView(Context context, CameraPreview owner) : base(context)
    {
        this.owner = owner;
        SurfaceTextureListener = this;
    }

    public IReadOnlyList<CameraResolution> GetSupportedResolutions() => supportedResolutions;

    public void SetCaptureResolution(CameraResolution resolution)
    {
        if (resolution.Width <= 0 || resolution.Height <= 0) return;

        captureResolution = resolution;
        Preferences.Default.Set(ResolutionPreferenceKey, SerializeResolution(captureResolution));

        if (camera is null || IsRecording) return;

        var shouldResume = requested;
        StopPreview();
        if (shouldResume) StartPreview();
    }

    public void StartPreview()
    {
        requested = true;
        if (!IsAvailable || camera is not null) return;

        try
        {
            var info = new Camera.CameraInfo();
            var id = FindPreferredCameraId(info);
            if (id < 0) throw new InvalidOperationException("Nenhuma câmera disponível.");

            camera = Camera.Open(id) ?? throw new InvalidOperationException("Câmera indisponível.");
            using var parameters = camera.GetParameters()!;
            supportedResolutions = parameters.SupportedPreviewSizes!
                .Select(x => new CameraResolution(x.Width, x.Height))
                .Distinct()
                .OrderByDescending(x => x.Width * x.Height)
                .ThenByDescending(x => x.Width)
                .ToList();

            if (supportedResolutions.Count == 0)
                throw new InvalidOperationException("A câmera não informou resoluções suportadas.");

            var preferred = GetPreferredResolution(supportedResolutions[0]);
            captureResolution = SelectClosestResolution(preferred, supportedResolutions);

            parameters.SetPreviewSize(captureResolution.Width, captureResolution.Height);
            if (parameters.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousVideo) == true)
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
            camera.SetParameters(parameters);
            camera.SetDisplayOrientation(info.Orientation);
            camera.SetPreviewTexture(SurfaceTexture);
            camera.StartPreview();
        }
        catch
        {
            StopPreview();
            throw;
        }
    }

    public void StopPreview()
    {
        requested = false;
        hasFrame = false;
        if (camera is null) return;
        try { camera.StopPreview(); }
        finally { camera.Release(); camera.Dispose(); camera = null; }
    }

    public void StartRecording(string path, bool audio)
    {
        if (camera is null || !hasFrame) throw new InvalidOperationException("Aguarde a imagem da câmera e tente novamente.");
        if (IsRecording) return;

        frameCount = 0;
        outputPath = null;
        outputUri = null;
        outputDescriptor = null;

        try
        {
            (outputUri, outputDescriptor, outputPath) = CreateVideoMediaStoreTarget(path);

            recorder = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(Context!) : new MediaRecorder();
            if (audio) recorder.SetAudioSource(AudioSource.Mic);
            recorder.SetVideoSource(VideoSource.Surface);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetVideoEncoder(VideoEncoder.H264);
            recorder.SetVideoSize(captureResolution.Width, captureResolution.Height);
            recorder.SetVideoFrameRate(15);
            recorder.SetVideoEncodingBitRate(GetVideoBitrate(captureResolution));
            if (audio)
            {
                recorder.SetAudioEncoder(AudioEncoder.Aac);
                recorder.SetAudioEncodingBitRate(96_000);
                recorder.SetAudioSamplingRate(44_100);
            }

            recorder.SetOutputFile(outputDescriptor.FileDescriptor);
            recorder.Prepare();
            renderer = new VideoSurfaceRenderer(recorder.Surface!, captureResolution.Width, captureResolution.Height);
            frame = Bitmap.CreateBitmap(captureResolution.Width, captureResolution.Height, Bitmap.Config.Argb8888!)!;
            recorder.Error += OnRecorderError;
            recorder.Start();
            RenderFrame();
            timer = owner.Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000.0 / 15);
            timer.Tick += OnFrame;
            timer.Start();
        }
        catch
        {
            ReleaseRecorder();
            if (outputUri is not null) DeleteMediaStoreVideo(outputUri);
            outputPath = null;
            outputUri = null;
            throw;
        }
    }

    public void CapturePhoto(string path)
    {
        if (camera is null || !hasFrame || !IsAvailable)
            throw new InvalidOperationException("Aguarde a imagem da câmera e tente novamente.");

        using var bitmap = Bitmap.CreateBitmap(captureResolution.Width, captureResolution.Height, Bitmap.Config.Argb8888!)!;
        if (GetBitmap(bitmap) is null) throw new InvalidOperationException("Imagem da câmera indisponível.");

        using var canvas = new Canvas(bitmap);
        DrawOverlay(canvas, captureResolution.Width, captureResolution.Height, owner.OverlayText);

        using var stream = System.IO.File.Create(path);
        if (!bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream))
            throw new InvalidOperationException("Não foi possível salvar a foto.");
    }

    void OnRecorderError(object? sender, MediaRecorder.ErrorEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => owner.ReportFailure("Falha no gravador. Verifique espaço livre e tente novamente."));

    void OnFrame(object? sender, EventArgs e)
    {
        try { RenderFrame(); }
        catch (Exception ex) { timer?.Stop(); owner.ReportFailure("Gravação interrompida: " + ex.Message); }
    }

    void RenderFrame()
    {
        if (!IsAvailable || !hasFrame || frame is null || renderer is null ||
            global::Android.OS.SystemClock.ElapsedRealtime() - lastPreviewFrame > 2000)
            throw new InvalidOperationException("A câmera perdeu a imagem.");
        // Capture only the camera texture; draw the label directly into the pixels
        // submitted to the encoder, independent of the MAUI preview label.
        if (GetBitmap(frame) is null) throw new InvalidOperationException("Imagem da câmera indisponível.");
        using var canvas = new Canvas(frame);
        DrawOverlay(canvas, captureResolution.Width, captureResolution.Height, owner.OverlayText);
        renderer.Draw(frame);
        frameCount++;
    }

    static void DrawOverlay(Canvas canvas, int width, int height, string overlayText)
    {
        using var paint = new Paint(PaintFlags.AntiAlias);
        var lines = overlayText.Split('\n');
        const int lineHeight = 26;
        var top = height - (lines.Length * lineHeight + 20);
        paint.Color = global::Android.Graphics.Color.Black;
        canvas.DrawRect(0, top, width, height, paint);
        paint.Color = global::Android.Graphics.Color.White;
        paint.TextSize = 20;
        paint.SetTypeface(Typeface.Monospace);
        for (var i = 0; i < lines.Length; i++) canvas.DrawText(lines[i], 10, top + 28 + i * lineHeight, paint);
    }

    public string? StopRecording()
    {
        if (recorder is null) return null;
        var path = outputPath;
        var uri = outputUri;
        try
        {
            timer?.Stop();
            recorder.Stop();
            if (frameCount == 0) throw new InvalidOperationException("Nenhum quadro gravado.");
            if (uri is not null) FinalizeMediaStoreVideo(uri);
            return path;
        }
        catch (Exception ex)
        {
            if (uri is not null) DeleteMediaStoreVideo(uri);
            throw new InvalidOperationException("Não foi possível salvar. Grave por alguns segundos e verifique o espaço livre.", ex);
        }
        finally
        {
            ReleaseRecorder();
            outputPath = null;
            outputUri = null;
        }
    }

    void ReleaseRecorder()
    {
        if (timer is not null) { timer.Stop(); timer.Tick -= OnFrame; timer = null; }
        renderer?.Dispose(); renderer = null;
        frame?.Dispose(); frame = null;
        if (recorder is not null)
        {
            recorder.Error -= OnRecorderError;
            recorder.Reset(); recorder.Release(); recorder.Dispose(); recorder = null;
        }
        outputDescriptor?.Close();
        outputDescriptor?.Dispose();
        outputDescriptor = null;
    }

    public void Shutdown()
    {
        try { StopRecording(); } catch (Exception ex) { owner.ReportFailure(ex.Message); }
        StopPreview();
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        if (!requested) return;
        try { StartPreview(); } catch (Exception ex) { owner.ReportFailure(ex.Message); }
    }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) { Shutdown(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
        hasFrame = true;
        lastPreviewFrame = global::Android.OS.SystemClock.ElapsedRealtime();
    }

    static int GetVideoBitrate(CameraResolution resolution)
    {
        var bitrate = resolution.Width * resolution.Height * 4;
        return Math.Clamp(bitrate, 2_000_000, 20_000_000);
    }

    (global::Android.Net.Uri Uri, global::Android.OS.ParcelFileDescriptor Descriptor, string Label) CreateVideoMediaStoreTarget(string requestedPath)
    {
        var fileName = System.IO.Path.GetFileName(requestedPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"GeoCamera_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.mp4";
        if (!fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            fileName += ".mp4";

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, "video/mp4");
        values.Put(MediaStore.IMediaColumns.RelativePath, System.IO.Path.Combine(global::Android.OS.Environment.DirectoryMovies!, "GeoCamera"));
        values.Put(MediaStore.IMediaColumns.DateAdded, Java.Lang.JavaSystem.CurrentTimeMillis() / 1000);
        values.Put(MediaStore.Video.VideoColumns.IsPending, 1);

        var resolver = Context!.ContentResolver!;
        var uri = resolver.Insert(MediaStore.Video.Media.ExternalContentUri!, values)
            ?? throw new InvalidOperationException("Não foi possível criar o arquivo no Movies/GeoCamera.");

        var descriptor = resolver.OpenFileDescriptor(uri, "w")
            ?? throw new InvalidOperationException("Não foi possível abrir o arquivo de vídeo para gravação.");

        return (uri, descriptor, $"Movies/GeoCamera/{fileName}");
    }

    void FinalizeMediaStoreVideo(global::Android.Net.Uri uri)
    {
        var values = new ContentValues();
        values.Put(MediaStore.Video.VideoColumns.IsPending, 0);
        Context!.ContentResolver!.Update(uri, values, null, null);
    }

    void DeleteMediaStoreVideo(global::Android.Net.Uri uri)
    {
        try { Context!.ContentResolver!.Delete(uri, null, null); }
        catch { }
    }

    static string SerializeResolution(CameraResolution resolution) => $"{resolution.Width}x{resolution.Height}";

    static bool TryParseResolution(string? value, out CameraResolution resolution)
    {
        resolution = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('x');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return false;
        if (width <= 0 || height <= 0) return false;

        resolution = new CameraResolution(width, height);
        return true;
    }

    static CameraResolution SelectClosestResolution(CameraResolution preferred, IReadOnlyList<CameraResolution> available)
    {
        if (available.Count == 0) return preferred;

        return available
            .OrderBy(x => Math.Abs(x.Width - preferred.Width) + Math.Abs(x.Height - preferred.Height))
            .ThenByDescending(x => x.Width * x.Height)
            .First();
    }

    CameraResolution GetPreferredResolution(CameraResolution fallback)
    {
        if (!TryParseResolution(Preferences.Default.Get(ResolutionPreferenceKey, string.Empty), out var saved))
            return fallback;

        return saved;
    }

    static int FindPreferredCameraId(Camera.CameraInfo info)
    {
        var firstBack = -1;
        var firstAny = -1;

        for (var i = 0; i < Camera.NumberOfCameras; i++)
        {
            Camera.GetCameraInfo(i, info);
            if (firstAny < 0) firstAny = i;
            if (info.Facing == CameraFacing.Front) return i;
            if (firstBack < 0 && info.Facing == CameraFacing.Back) firstBack = i;
        }

        return firstBack >= 0 ? firstBack : firstAny;
    }
}
#pragma warning restore CS0618
