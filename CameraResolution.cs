namespace GeoCamera;

public readonly record struct CameraResolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}
