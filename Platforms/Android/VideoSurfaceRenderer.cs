using Android.Graphics;
using Android.Opengl;
using Android.Views;
using Java.Nio;

namespace GeoCamera.Platforms.Android;

// EGL provides the supported GPU input path to MediaRecorder's encoder surface.
// Every frame already includes its opaque GPS box before it reaches this class.
internal sealed class VideoSurfaceRenderer : IDisposable
{
    readonly EGLDisplay display;
    EGLContext? context;
    EGLSurface? surface;
    readonly Surface input;
    readonly int width, height;
    int program, texture;
    FloatBuffer? vertices, coordinates;

    public VideoSurfaceRenderer(Surface input, int width, int height)
    {
        this.input = input;
        this.width = width; this.height = height;
        display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay)!;
        try
        {
            var version = new int[2];
            Check(EGL14.EglInitialize(display, version, 0, version, 1), "EGL initialize");
            int[] attributes = [EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit, EGL14.EglSurfaceType, EGL14.EglWindowBit,
                0x3142, 1, EGL14.EglNone]; // EGL_RECORDABLE_ANDROID
            var configs = new EGLConfig[1];
            var count = new int[1];
            Check(EGL14.EglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) && count[0] > 0, "EGL config");
            context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext,
                [EGL14.EglContextClientVersion, 2, EGL14.EglNone], 0);
            surface = EGL14.EglCreateWindowSurface(display, configs[0], input, [EGL14.EglNone], 0);
            MakeCurrent();
            program = GLES20.GlCreateProgram();
            var vertex = Compile(GLES20.GlVertexShader, "attribute vec2 p; attribute vec2 t; varying vec2 uv; void main(){gl_Position=vec4(p,0.0,1.0);uv=t;}");
            var fragment = Compile(GLES20.GlFragmentShader, "precision mediump float; varying vec2 uv; uniform sampler2D image; void main(){gl_FragColor=texture2D(image,uv);}");
            GLES20.GlAttachShader(program, vertex); GLES20.GlAttachShader(program, fragment);
            GLES20.GlLinkProgram(program);
            GLES20.GlDeleteShader(vertex); GLES20.GlDeleteShader(fragment);
            var linked = new int[1];
            GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linked, 0);
            if (linked[0] == 0) throw new InvalidOperationException(GLES20.GlGetProgramInfoLog(program));
            vertices = Buffer([-1, -1, 1, -1, -1, 1, 1, 1]);
            // Bitmap rows run top-to-bottom; flip V so the saved video is upright.
            coordinates = Buffer([0, 1, 1, 1, 0, 0, 1, 0]);
            var names = new int[1]; GLES20.GlGenTextures(1, names, 0); texture = names[0];
            GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            Unbind();
        }
        catch { Dispose(); throw; }
    }

    public void Draw(Bitmap bitmap)
    {
        MakeCurrent();
        try
        {
            GLES20.GlViewport(0, 0, width, height);
            GLES20.GlUseProgram(program);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, texture);
            GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bitmap, 0);
            GLES20.GlUniform1i(GLES20.GlGetUniformLocation(program, "image"), 0);
            var p = GLES20.GlGetAttribLocation(program, "p");
            var t = GLES20.GlGetAttribLocation(program, "t");
            GLES20.GlEnableVertexAttribArray(p); GLES20.GlEnableVertexAttribArray(t);
            GLES20.GlVertexAttribPointer(p, 2, GLES20.GlFloat, false, 0, vertices);
            GLES20.GlVertexAttribPointer(t, 2, GLES20.GlFloat, false, 0, coordinates);
            GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
            if (GLES20.GlGetError() != GLES20.GlNoError) throw new InvalidOperationException("Falha ao desenhar vídeo.");
            Check(EGLExt.EglPresentationTimeANDROID(display, surface, Java.Lang.JavaSystem.NanoTime()), "Video timestamp");
            Check(EGL14.EglSwapBuffers(display, surface), "Video frame");
        }
        finally { Unbind(); }
    }

    static FloatBuffer Buffer(float[] data)
    {
        var buffer = ByteBuffer.AllocateDirect(data.Length * sizeof(float))!.Order(ByteOrder.NativeOrder()!)!.AsFloatBuffer()!;
        buffer.Put(data); buffer.Position(0); return buffer;
    }
    static int Compile(int type, string source)
    {
        var shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source); GLES20.GlCompileShader(shader);
        var status = new int[1]; GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
        if (status[0] != 0) return shader;
        var error = GLES20.GlGetShaderInfoLog(shader); GLES20.GlDeleteShader(shader);
        throw new InvalidOperationException(error);
    }
    void MakeCurrent() => Check(EGL14.EglMakeCurrent(display, surface, surface, context), "EGL current");
    void Unbind() => EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
    static void Check(bool success, string operation)
    {
        if (!success) throw new InvalidOperationException($"{operation}: 0x{EGL14.EglGetError():X}");
    }
    public void Dispose()
    {
        if (context is not null && surface is not null && EGL14.EglMakeCurrent(display, surface, surface, context))
        {
            if (texture != 0) GLES20.GlDeleteTextures(1, [texture], 0);
            if (program != 0) GLES20.GlDeleteProgram(program);
        }
        Unbind();
        if (surface is not null) { EGL14.EglDestroySurface(display, surface); surface.Dispose(); surface = null; }
        if (context is not null) { EGL14.EglDestroyContext(display, context); context.Dispose(); context = null; }
        EGL14.EglTerminate(display);
        vertices?.Dispose(); coordinates?.Dispose();
        input.Dispose();
    }
}
