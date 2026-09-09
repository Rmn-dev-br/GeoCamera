using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Microsoft.Maui.Handlers;
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
    readonly CameraPreview owner;
    Camera? camera;
    MediaRecorder? recorder;
    VideoSurfaceRenderer? renderer;
    Bitmap? frame;
    IDispatcherTimer? timer;
    string? outputPath;
    bool requested, hasFrame;
    int frameCount;
    long lastPreviewFrame;
    public bool IsRecording => recorder is not null;

    public RecordingTextureView(Context context, CameraPreview owner) : base(context)
    {
        this.owner = owner;
        SurfaceTextureListener = this;
    }

    public void StartPreview()
    {
        requested = true;
        if (!IsAvailable || camera is not null) return;
        try
        {
            var info = new Camera.CameraInfo();
            var id = -1;
            for (var i = 0; i < Camera.NumberOfCameras; i++)
            {
                Camera.GetCameraInfo(i, info);
                if (info.Facing == CameraFacing.Back) { id = i; break; }
            }
            if (id < 0) throw new InvalidOperationException("Nenhuma câmera traseira disponível.");
            camera = Camera.Open(id) ?? throw new InvalidOperationException("Câmera indisponível.");
            using var parameters = camera.GetParameters()!;
            var size = parameters.SupportedPreviewSizes!
                .OrderBy(s => Math.Abs((double)s.Width / s.Height - 4.0 / 3.0) * 10000 + Math.Abs(s.Width - 640))
                .First();
            parameters.SetPreviewSize(size.Width, size.Height);
            if (parameters.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousVideo) == true)
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
            camera.SetParameters(parameters);
            camera.SetDisplayOrientation(info.Orientation);
            camera.SetPreviewTexture(SurfaceTexture);
            camera.StartPreview();
        }
        catch { StopPreview(); throw; }
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
        outputPath = path;
        frameCount = 0;
        try
        {
            recorder = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(Context!) : new MediaRecorder();
            if (audio) recorder.SetAudioSource(AudioSource.Mic);
            recorder.SetVideoSource(VideoSource.Surface);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetVideoEncoder(VideoEncoder.H264);
            recorder.SetVideoSize(480, 640);
            recorder.SetVideoFrameRate(15);
            recorder.SetVideoEncodingBitRate(2_000_000);
            if (audio)
            {
                recorder.SetAudioEncoder(AudioEncoder.Aac);
                recorder.SetAudioEncodingBitRate(96_000);
                recorder.SetAudioSamplingRate(44_100);
            }
            recorder.SetOutputFile(path);
            recorder.Prepare();
            renderer = new VideoSurfaceRenderer(recorder.Surface!, 480, 640);
            frame = Bitmap.CreateBitmap(480, 640, Bitmap.Config.Argb8888!)!;
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
            if (File.Exists(path)) File.Delete(path);
            outputPath = null;
            throw;
        }
    }

    public void CapturePhoto(string path)
    {
        if (camera is null || !hasFrame || !IsAvailable)
            throw new InvalidOperationException("Aguarde a imagem da câmera e tente novamente.");

        using var bitmap = Bitmap.CreateBitmap(480, 640, Bitmap.Config.Argb8888!)!;
        if (GetBitmap(bitmap) is null) throw new InvalidOperationException("Imagem da câmera indisponível.");

        using var canvas = new Canvas(bitmap);
        DrawOverlay(canvas, 480, 640, owner.OverlayText);

        using var stream = File.Create(path);
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
        DrawOverlay(canvas, 480, 640, owner.OverlayText);
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
        try
        {
            timer?.Stop();
            recorder.Stop();
            if (frameCount == 0) throw new InvalidOperationException("Nenhum quadro gravado.");
            return path;
        }
        catch (Exception ex)
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
            throw new InvalidOperationException("Não foi possível salvar. Grave por alguns segundos e verifique o espaço livre.", ex);
        }
        finally { ReleaseRecorder(); outputPath = null; }
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
}
#pragma warning restore CS0618
