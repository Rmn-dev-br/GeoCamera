using System.Globalization;
#if ANDROID
using Android.Content;
using Android.Provider;
#endif

namespace GeoCamera;

public partial class MainPage : ContentPage
{
    enum CaptureMode { Video, Picture }
    enum CoordinateFormat { DecimalDegrees, DegreesMinutesSeconds }

    CancellationTokenSource? locationCancellation;
    bool active, busy, microphone;
    Location? fix;
    DateTimeOffset started;
    IDispatcherTimer? timer;
    CaptureMode captureMode = CaptureMode.Video;
    CoordinateFormat coordinateFormat = CoordinateFormat.DecimalDegrees;
    bool showDate = true;
    bool showTime = true;

    public static string RecordingsDirectory => Path.Combine(FileSystem.AppDataDirectory, "Recordings");

    public MainPage()
    {
        InitializeComponent();
        Preview.Loaded += (_, _) => _ = ActivateAsync();
        Directory.CreateDirectory(RecordingsDirectory);
        Preview.Failed += message =>
        {
            try { if (Preview.IsRecording) StopRecording(); } catch { }
            StatusLabel.Text = message;
            RecordButton.IsEnabled = false;
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Window is { } window) { window.Stopped += OnStopped; window.Resumed += OnResumed; }
        _ = ActivateAsync();
    }

    protected override void OnDisappearing()
    {
        if (Window is { } window) { window.Stopped -= OnStopped; window.Resumed -= OnResumed; }
        Deactivate();
        base.OnDisappearing();
    }

    void OnStopped(object? sender, EventArgs e) => Deactivate();
    void OnResumed(object? sender, EventArgs e) => _ = ActivateAsync();

    async Task ActivateAsync()
    {
        if (active || busy || Preview.Handler is null) return;
        active = true;
        try
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            {
                StatusLabel.Text = "Permita o acesso à câmera nas configurações do aplicativo.";
                active = false;
                return;
            }
            var locationAllowed = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            microphone = await Permissions.RequestAsync<Permissions.Microphone>() == PermissionStatus.Granted;
            if (!active) return;
            Preview.StartPreview();
            OnPreviewSizeChanged(this, EventArgs.Empty);
            RecordButton.IsEnabled = true;
            UpdateReadyStatus();
            locationCancellation = new CancellationTokenSource();
            if (locationAllowed == PermissionStatus.Granted) _ = UpdateLocationAsync(locationCancellation.Token);
            else fix = null;
            timer ??= Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick -= OnTick;
            timer.Tick += OnTick;
            timer.Start();
            OnTick(this, EventArgs.Empty);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; active = false; RecordButton.IsEnabled = false; }
    }

    async Task UpdateLocationAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var next = await Geolocation.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(8)), cancellation);
                if (cancellation.IsCancellationRequested) return;
                if (next is not null) fix = next;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (FeatureNotEnabledException) { fix = null; }
            catch (Exception) { fix = null; }
            try { await Task.Delay(1000, cancellation); }
            catch (OperationCanceledException) { return; }
        }
    }

    void OnTick(object? sender, EventArgs e)
    {
        Preview.OverlayText = BuildOverlayText(DateTime.Now);
        GpsLabel.Text = Preview.OverlayText;
        if (Preview.IsRecording)
            StatusLabel.Text = $"Gravando {(DateTimeOffset.Now - started):mm\\:ss} • {(microphone ? "com áudio" : "sem áudio")}";
    }

    string BuildOverlayText(DateTime now)
    {
        var age = fix is null ? double.MaxValue : (DateTimeOffset.UtcNow - fix.Timestamp).TotalSeconds;
        var hasFreshFix = fix is not null && age <= 15;
        var lat = hasFreshFix ? fix!.Latitude : (double?)null;
        var lon = hasFreshFix ? fix!.Longitude : (double?)null;

        var line1 = FormatCoordinates(lat, lon);
        var dateTimeLine = BuildDateTimeLine(now);
        return string.IsNullOrWhiteSpace(dateTimeLine) ? line1 : $"{line1}\n{dateTimeLine}";
    }

    string BuildDateTimeLine(DateTime now)
    {
        if (!showDate && !showTime) return string.Empty;
        if (showDate && showTime) return now.ToString("dd/MM/yyyy HH:mm:ss");
        if (showDate) return now.ToString("dd/MM/yyyy");
        return now.ToString("HH:mm:ss");
    }

    string FormatCoordinates(double? latitude, double? longitude)
    {
        if (latitude is null || longitude is null) return "Lat --  Lon --";
        return coordinateFormat == CoordinateFormat.DecimalDegrees
            ? string.Create(CultureInfo.InvariantCulture, $"Lat {latitude.Value:F6}  Lon {longitude.Value:F6}")
            : $"Lat {FormatDms(latitude.Value, true)}  Lon {FormatDms(longitude.Value, false)}";
    }

    static string FormatDms(double value, bool latitude)
    {
        var direction = latitude ? (value >= 0 ? "N" : "S") : (value >= 0 ? "E" : "W");
        var absolute = Math.Abs(value);
        var degrees = (int)absolute;
        var minutesValue = (absolute - degrees) * 60;
        var minutes = (int)minutesValue;
        var seconds = (minutesValue - minutes) * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{degrees}°{minutes:00}'{seconds:00.0}\"{direction}");
    }

    async void OnSettingsClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheetAsync("Configurações", "Fechar", null,
            "Modo: vídeo/foto",
            $"Mostrar data: {(showDate ? "ligado" : "desligado")}",
            $"Mostrar hora: {(showTime ? "ligado" : "desligado")}",
            $"Formato coordenadas: {(coordinateFormat == CoordinateFormat.DecimalDegrees ? "decimal" : "graus/min/seg")}");

        if (action == "Modo: vídeo/foto") await SelectCaptureModeAsync();
        else if (action.StartsWith("Mostrar data", StringComparison.Ordinal)) showDate = !showDate;
        else if (action.StartsWith("Mostrar hora", StringComparison.Ordinal)) showTime = !showTime;
        else if (action.StartsWith("Formato coordenadas", StringComparison.Ordinal)) await SelectCoordinateFormatAsync();

        OnTick(this, EventArgs.Empty);
        if (!Preview.IsRecording) UpdateReadyStatus();
    }

    async Task SelectCaptureModeAsync()
    {
        var selected = await DisplayActionSheetAsync("Modo de captura", "Cancelar", null, "Vídeo", "Foto");
        if (selected == "Vídeo") captureMode = CaptureMode.Video;
        else if (selected == "Foto") captureMode = CaptureMode.Picture;
    }

    async Task SelectCoordinateFormatAsync()
    {
        var selected = await DisplayActionSheetAsync("Formato de coordenadas", "Cancelar", null, "Decimal", "Graus/Min/Seg");
        if (selected == "Decimal") coordinateFormat = CoordinateFormat.DecimalDegrees;
        else if (selected == "Graus/Min/Seg") coordinateFormat = CoordinateFormat.DegreesMinutesSeconds;
    }

    void UpdateReadyStatus()
    {
        StatusLabel.Text = captureMode == CaptureMode.Video
            ? (microphone ? "Pronto • vídeo com áudio" : "Pronto • vídeo sem áudio")
            : "Pronto • foto";
        UpdateRecordButtonVisual(Preview.IsRecording);
    }

    void UpdateRecordButtonVisual(bool recording)
    {
        RecordButton.Text = captureMode == CaptureMode.Video && recording ? "■" : "●";
    }

    async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        RecordButton.IsEnabled = false;
        try
        {
            if (Preview.IsRecording)
            {
                StopRecording();
            }
            else if (captureMode == CaptureMode.Picture)
            {
                await CapturePictureAsync();
            }
            else
            {
                var path = Path.Combine(RecordingsDirectory, $"GeoCamera_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.mp4");
                OnTick(this, EventArgs.Empty);
                Preview.StartRecording(path, microphone);
                started = DateTimeOffset.Now;
                DeviceDisplay.Current.KeepScreenOn = true;
                FilesButton.IsEnabled = false;
                UpdateRecordButtonVisual(recording: true);
                OnTick(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) { await DisplayAlertAsync("Captura", ex.Message, "OK"); }
        finally { busy = false; RecordButton.IsEnabled = active; }
    }

    async Task CapturePictureAsync()
    {
        var path = Path.Combine(RecordingsDirectory, $"GeoCamera_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}.png");
        OnTick(this, EventArgs.Empty);
        Preview.CapturePhoto(path);
        StatusLabel.Text = $"Foto salva: {Path.GetFileName(path)}";
        await Task.CompletedTask;
    }

    void StopRecording()
    {
        try
        {
            var path = Preview.StopRecording();
            if (path is not null) StatusLabel.Text = $"Salvo: {Path.GetFileName(path)}";
        }
        finally
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            FilesButton.IsEnabled = true;
            UpdateReadyStatus();
        }
    }

    void Deactivate()
    {
        active = false;
        locationCancellation?.Cancel();
        locationCancellation?.Dispose();
        locationCancellation = null;
        timer?.Stop();
        try { if (Preview.IsRecording) StopRecording(); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        Preview.StopPreview();
        RecordButton.IsEnabled = false;
    }

    void OnPreviewSizeChanged(object? sender, EventArgs e)
    {
        var resolution = Preview.GetCaptureResolution();
        var widthToHeight = resolution.Width > 0 && resolution.Height > 0
            ? (double)resolution.Width / resolution.Height
            : 4.0 / 3.0;

        var width = Math.Min(PreviewBox.Width, PreviewBox.Height * widthToHeight);
        if (width <= 0) return;

        Preview.WidthRequest = width;
        Preview.HeightRequest = width / widthToHeight;
    }

    async void OnFilesClicked(object? sender, EventArgs e)
    {
        try
        {
            var items = new List<CaptureItem>();

            items.AddRange(Directory.EnumerateFiles(RecordingsDirectory)
                .Where(x => x.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x)
                .Select(x =>
                {
                    var isVideo = x.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                    return new CaptureItem(
                        DisplayName: Path.GetFileName(x),
                        SortKey: File.GetLastWriteTimeUtc(x),
                        LocalPath: x,
                        ContentUri: null,
                        MimeType: isVideo ? "video/mp4" : "image/png",
                        Title: isVideo ? "Vídeo" : "Foto");
                }));

#if ANDROID
            items.AddRange(GetAndroidSharedVideos());
#endif

            var ordered = items
                .OrderByDescending(x => x.SortKey)
                .ToArray();

            if (ordered.Length == 0)
            {
                await DisplayAlertAsync("Capturas", "Nenhum arquivo salvo ainda.", "OK");
                return;
            }

            var selected = await DisplayActionSheetAsync("Capturas", "Cancelar", null, ordered.Select(x => x.DisplayName).ToArray());
            var item = ordered.FirstOrDefault(x => x.DisplayName == selected);
            if (item is null) return;

            var actions = item.LocalPath is not null
                ? new[] { "Abrir", "Compartilhar / salvar cópia" }
                : new[] { "Abrir" };

            var action = await DisplayActionSheetAsync(selected, "Cancelar", null, actions);
            if (action != "Abrir" && action != "Compartilhar / salvar cópia") return;

            if (action == "Abrir")
            {
                if (item.LocalPath is not null)
                    await Launcher.OpenAsync(new OpenFileRequest(item.Title, new ReadOnlyFile(item.LocalPath, item.MimeType)));
                else if (item.ContentUri is not null)
                    await Launcher.OpenAsync(item.ContentUri);
            }
            else if (item.LocalPath is not null)
            {
                await Share.RequestAsync(new ShareFileRequest($"{item.Title} GeoCamera", new ShareFile(item.LocalPath, item.MimeType)));
            }
        }
        catch (Exception ex) { await DisplayAlertAsync("Capturas", ex.Message, "OK"); }
    }

    sealed record CaptureItem(
        string DisplayName,
        DateTime SortKey,
        string? LocalPath,
        Uri? ContentUri,
        string MimeType,
        string Title);

#if ANDROID
    static IEnumerable<CaptureItem> GetAndroidSharedVideos()
    {
        var result = new List<CaptureItem>();
        var context = Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver is null) return result;

        string[] projection =
        [
            BaseColumns.Id,
            MediaStore.IMediaColumns.DisplayName!,
            MediaStore.IMediaColumns.DateAdded!,
            MediaStore.IMediaColumns.RelativePath!
        ];

        using var cursor = resolver.Query(
            MediaStore.Video.Media.ExternalContentUri,
            projection,
            $"{MediaStore.IMediaColumns.RelativePath}=?",
            new[] { "Movies/GeoCamera/" },
            $"{MediaStore.IMediaColumns.DateAdded} DESC");

        if (cursor is null) return result;

        var idIndex = cursor.GetColumnIndexOrThrow(BaseColumns.Id);
        var nameIndex = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DisplayName);
        var dateIndex = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DateAdded);

        while (cursor.MoveToNext())
        {
            var id = cursor.GetLong(idIndex);
            var name = cursor.GetString(nameIndex) ?? $"Video_{id}.mp4";
            var seconds = cursor.GetLong(dateIndex);
            var date = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            var uri = ContentUris.WithAppendedId(MediaStore.Video.Media.ExternalContentUri!, id);
            result.Add(new CaptureItem(name, date, null, new Uri(uri!.ToString()!), "video/mp4", "Vídeo"));
        }

        return result;
    }
#endif
}
