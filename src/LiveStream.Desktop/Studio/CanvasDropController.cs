using System.IO;
using LiveStream.Application.Editing;
using LiveStream.Application.Sources;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Desktop.Studio;

/// <summary>
/// Importa archivos arrastrados al lienzo: se copian a la biblioteca de recursos (imágenes y videos) o se
/// validan in situ (audio), se crea la fuente adecuada, se ajusta al punto de soltar con <see cref="DropPlacement"/>
/// usando el tamaño real que informe el motor y queda seleccionado el último importado.
/// </summary>
public sealed class CanvasDropController(MainViewModel vm)
{
    /// <summary>Extensiones admitidas → clase de fuente nativa de libobs.</summary>
    public static readonly IReadOnlyDictionary<string, SourceKind> ExtensionKinds =
        new Dictionary<string, SourceKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = SourceKind.Image, [".jpg"] = SourceKind.Image, [".jpeg"] = SourceKind.Image,
            [".gif"] = SourceKind.Image, [".webp"] = SourceKind.Image, [".bmp"] = SourceKind.Image,
            [".mp4"] = SourceKind.Media, [".mov"] = SourceKind.Media, [".webm"] = SourceKind.Media,
            [".mkv"] = SourceKind.Media, [".m4v"] = SourceKind.Media,
            [".mp3"] = SourceKind.Media, [".wav"] = SourceKind.Media, [".ogg"] = SourceKind.Media,
            [".m4a"] = SourceKind.Media, [".aac"] = SourceKind.Media, [".flac"] = SourceKind.Media,
        };

    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg", ".m4a", ".aac", ".flac"];

    public static string SupportedExtensionsText =>
        "imágenes: png, jpg, jpeg, gif, webp, bmp · video: mp4, mov, webm, mkv, m4v · audio: mp3, wav, ogg, m4a, aac, flac";

    private const double ScatterOffset = 24; // escalonado entre archivos soltados a la vez
    private readonly List<string> _errors = [];

    public async Task ImportFilesAsync(IReadOnlyList<string> paths, double canvasX, double canvasY, DropFit fit = DropFit.Natural)
    {
        if (paths.Count == 0)
            return;
        // Origen panel de fuentes (sin punto del lienzo): se centra el recurso en el lienzo.
        if (double.IsNaN(canvasX) || double.IsNaN(canvasY))
        {
            canvasX = vm.Project.Canvas.Width / 2.0;
            canvasY = vm.Project.Canvas.Height / 2.0;
        }
        if (vm.EditSceneId is not { } sceneId)
        {
            vm.StatusMessage = "No hay escena en edición para colocar el archivo.";
            return;
        }
        _errors.Clear();

        var added = 0;
        Guid? lastItem = null;
        foreach (var path in paths)
        {
            try
            {
                var itemId = await ImportOneAsync(path, sceneId, canvasX + added * ScatterOffset, canvasY + added * ScatterOffset, fit);
                lastItem = itemId;
                ++added;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                _errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (lastItem is { } selected)
            vm.SelectFromCanvas(selected, false);

        vm.StatusMessage = (added, _errors) switch
        {
            (0, var e) => $"No se pudo importar: {string.Join(" · ", e)}. Formatos válidos: {SupportedExtensionsText}.",
            (1, { Count: 0 }) => "Recurso añadido a la escena.",
            (_, { Count: 0 }) => $"{added} recursos añadidos a la escena.",
            (_, var e) => $"{added} añadidos · fallidos: {string.Join(" · ", e)}",
        };
    }

    /// <summary>Importa un archivo y devuelve el ID del elemento creado (lanza con el motivo si no pudo).</summary>
    private async Task<Guid> ImportOneAsync(string path, Guid sceneId, double canvasX, double canvasY, DropFit fit)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidDataException("el archivo no existe");
        if (info.Length == 0)
            throw new InvalidDataException("el archivo está vacío");
        if (!ExtensionKinds.TryGetValue(info.Extension, out var kind))
            throw new InvalidDataException($"formato «{info.Extension}» no admitido");

        // Las imágenes y videos pasan por la biblioteca (copia + validación del contenido); el audio se referencia.
        var settings = new System.Text.Json.Nodes.JsonObject();
        if (kind == SourceKind.Image)
            settings["file"] = vm.Library.Import(path, "General").Path;
        else if (AudioExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
        {
            // Sin tamaño real hasta que suene: validar contenido es cosa del decodificador; se referencia tal cual.
            if (!LooksLikeAudio(info.Extension))
                throw new InvalidDataException("formato de audio no admitido");
            settings["is_local_file"] = true;
            settings["local_file"] = info.FullName;
            settings["close_when_inactive"] = false;
        }
        else
        {
            settings["is_local_file"] = true;
            settings["local_file"] = vm.Library.Import(path, "General").Path;
            settings["looping"] = false;
            settings["close_when_inactive"] = false;
        }

        var baseName = Path.GetFileNameWithoutExtension(info.Name) is { Length: > 0 } name ? name : "Recurso";
        var command = new AddSourceCommand(sceneId, UniqueName(baseName), kind, settings.ToJsonString());
        var result = await vm.Run(command);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        if (command.ItemId is not { } itemId)
            throw new InvalidOperationException("no se creó la fuente");

        var (w, h) = (0, 0);
        if (vm.EngineReady && command.SourceId is { } sourceId)
        {
            try
            {
                (w, h) = await vm.Engine.GetSourceSizeAsync(sourceId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Native.Engine.NativeEngineException)
            {
                // Sin tamaño real (archivo aún decodificando, error del motor): queda a 1:1 y se ajusta a mano.
            }
        }
        var transform = DropPlacement.Place(canvasX, canvasY, w, h, vm.Project.Canvas, fit);
        await vm.Run(new SetItemTransformCommand(itemId, transform));
        return itemId;
    }

    /// <summary>Nombre de fuente único dentro del proyecto, como el resto de la UI.</summary>
    private string UniqueName(string baseName)
    {
        if (vm.Project.Sources.All(s => s.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (vm.Project.Sources.All(s => s.Name != $"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    private static bool LooksLikeAudio(string extension) => AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
