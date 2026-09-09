namespace GeoCamera;

public sealed class CameraPreview : View
{
    public string OverlayText { get; set; } = "GPS indisponível";
    public event Action<string>? Failed;
    internal void ReportFailure(string message) => Failed?.Invoke(message);
#if ANDROID
    Platforms.Android.CameraPreviewHandler NativeHandler => (Platforms.Android.CameraPreviewHandler)Handler!;
    public bool IsRecording => Handler is Platforms.Android.CameraPreviewHandler h && h.PlatformView.IsRecording;
    public void StartPreview() => NativeHandler.PlatformView.StartPreview();
    public void StopPreview() { if (Handler is Platforms.Android.CameraPreviewHandler h) h.PlatformView.StopPreview(); }
    public void StartRecording(string path, bool audio) => NativeHandler.PlatformView.StartRecording(path, audio);
    public string? StopRecording() => NativeHandler.PlatformView.StopRecording();
    public void CapturePhoto(string path) => NativeHandler.PlatformView.CapturePhoto(path);
#else
    public bool IsRecording => false;
    public void StartPreview() => throw new PlatformNotSupportedException("Use este aplicativo em um telefone Android.");
    public void StopPreview() { }
    public void StartRecording(string path, bool audio) => throw new PlatformNotSupportedException();
    public string? StopRecording() => null;
    public void CapturePhoto(string path) => throw new PlatformNotSupportedException();
#endif
}
