using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Graphics;

/// <summary>
/// Compone los gráficos en el motor. Cada gráfico es una escena propia (rectángulos y textos) anidada en una
/// escena "capa de gráficos" que va en un canal de salida por encima del programa, como el downstream keyer
/// de un mezclador de TV: los gráficos siguen en pantalla aunque cambie la escena de programa.
/// Los ids del motor se derivan de los del gráfico, así no hay que persistir nada más.
/// </summary>
public static class GraphicLayer
{
    /// <summary>Canal de salida de la capa (1..5 son el audio global).</summary>
    public const int OverlayChannel = 8;

    public static Guid OverlaySceneId(StudioProject project) => Derive(project.Id, "graphics-overlay");

    public static Guid OverlayItemId(Guid graphicId) => Derive(graphicId, "overlay-item");

    public static Guid ElementSourceId(Guid graphicId, string key) => Derive(graphicId, "source:" + key);

    public static Guid ElementItemId(Guid graphicId, string key) => Derive(graphicId, "item:" + key);

    public static Guid ScrollFilterId(Guid graphicId, string key) => Derive(graphicId, "scroll:" + key);

    public static Guid OpacityFilterId(Guid graphicId, string key) => Derive(graphicId, "opacity:" + key);

    /// <summary>Id determinista (estilo UUID v5) a partir de otro id y una clave.</summary>
    public static Guid Derive(Guid baseId, string key)
    {
        var bytes = baseId.ToByteArray().Concat(Encoding.UTF8.GetBytes(key)).ToArray();
        var hash = SHA1.HashData(bytes);
        var g = hash.AsSpan(0, 16).ToArray();
        g[7] = (byte)((g[7] & 0x0F) | 0x50); // versión 5 (en el orden de bytes de Guid)
        g[8] = (byte)((g[8] & 0x3F) | 0x80); // variante RFC 4122
        return new Guid(g);
    }

    public static async Task CreateOverlayAsync(IStreamingEngine engine, StudioProject project, CancellationToken ct)
    {
        var overlay = OverlaySceneId(project);
        await engine.CreateSceneAsync(overlay, "LiveStream · Capa de gráficos", ct).ConfigureAwait(false);
        await engine.SetGlobalAudioChannelAsync(overlay, OverlayChannel, ct).ConfigureAwait(false);
    }

    /// <summary>Crea el gráfico en el motor, oculto, en su posición de la pila.</summary>
    public static async Task CreateAsync(IStreamingEngine engine, StudioProject project, Graphic graphic, int index,
        Func<Graphic, string, string?>? text, CancellationToken ct)
    {
        var layout = GraphicTemplates.Layout(graphic, project.Canvas, key => text?.Invoke(graphic, key));
        await engine.CreateSceneAsync(graphic.Id, $"Gráfico · {graphic.Name}", ct).ConfigureAwait(false);
        try
        {
            foreach (var element in layout.Elements)
                await CreatePieceAsync(engine, graphic, element, ct).ConfigureAwait(false);

            var overlayItem = OverlayItemId(graphic.Id);
            await engine.AddItemAsync(OverlaySceneId(project), overlayItem, graphic.Id, ct).ConfigureAwait(false);
            await engine.SetItemVisibleAsync(overlayItem, false, ct).ConfigureAwait(false);
            await engine.SetItemOrderAsync(overlayItem, index, ct).ConfigureAwait(false);
        }
        catch
        {
            await RemoveAsync(engine, project, graphic, CancellationToken.None, ignoreErrors: true).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Aplica en vivo campos, estilo, posición y escala. Las plantillas tienen siempre las mismas piezas; en un diseño
    /// libre, con <paramref name="previous"/> (lo que hay ahora en el motor) solo se crean o retiran las piezas que
    /// cambian y se reordenan las capas: el resto sigue igual y la emisión no se interrumpe.
    /// </summary>
    public static async Task UpdateAsync(IStreamingEngine engine, StudioProject project, Graphic graphic,
        Func<Graphic, string, string?>? text, CancellationToken ct, Graphic? previous = null)
    {
        var layout = GraphicTemplates.Layout(graphic, project.Canvas, key => text?.Invoke(graphic, key));
        var before = previous is null ? null
            : GraphicTemplates.Layout(previous, project.Canvas).Elements.ToDictionary(e => e.Key, e => e.Kind);
        var changedPieces = false;
        foreach (var element in layout.Elements)
        {
            var sourceId = ElementSourceId(graphic.Id, element.Key);
            if (before is not null && (!before.TryGetValue(element.Key, out var oldKind) || oldKind != element.Kind))
            {
                if (before.ContainsKey(element.Key))
                    await RemovePieceAsync(engine, graphic.Id, element.Key, ct).ConfigureAwait(false);
                await CreatePieceAsync(engine, graphic, element, ct).ConfigureAwait(false);
                changedPieces = true;
                continue;
            }
            await engine.UpdateSourceSettingsAsync(sourceId, SettingsJson(element, graphic.Style), ct).ConfigureAwait(false);
            if (element.Kind == GraphicElementKind.Ticker)
                await engine.UpdateFilterAsync(ScrollFilterId(graphic.Id, element.Key), ScrollSettings(graphic, element), ct).ConfigureAwait(false);
            if (element.Design is { Kind: DesignElementKind.Image or DesignElementKind.Video } design)
            {
                await engine.UpdateFilterAsync(OpacityFilterId(graphic.Id, element.Key), OpacitySettings(design), ct).ConfigureAwait(false);
                if (design.Kind == DesignElementKind.Video)
                    await engine.SetMutedAsync(sourceId, design.Muted, ct).ConfigureAwait(false);
            }
            await engine.SetItemTransformAsync(ElementItemId(graphic.Id, element.Key),
                await ElementTransformAsync(engine, sourceId, element, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        if (before is null)
            return;
        var keys = layout.Elements.Select(e => e.Key).ToHashSet();
        foreach (var removed in before.Keys.Where(k => !keys.Contains(k)))
        {
            await RemovePieceAsync(engine, graphic.Id, removed, ct).ConfigureAwait(false);
            changedPieces = true;
        }
        var oldOrder = before.Keys.Where(keys.Contains).ToList();
        var newOrder = layout.Elements.Select(e => e.Key).Where(before.ContainsKey).ToList();
        if (changedPieces || !oldOrder.SequenceEqual(newOrder))
            for (var i = 0; i < layout.Elements.Count; i++)
                await engine.SetItemOrderAsync(ElementItemId(graphic.Id, layout.Elements[i].Key), i, ct).ConfigureAwait(false);
    }

    private static async Task CreatePieceAsync(IStreamingEngine engine, Graphic graphic, GraphicElement element, CancellationToken ct)
    {
        var sourceId = ElementSourceId(graphic.Id, element.Key);
        var itemId = ElementItemId(graphic.Id, element.Key);
        var kind = element.Kind switch
        {
            GraphicElementKind.Rect => SourceKind.ColorSource,
            GraphicElementKind.Image => SourceKind.Image,
            GraphicElementKind.Video => SourceKind.Media,
            _ => SourceKind.Text,
        };
        await engine.CreateSourceAsync(new SourceDefinition(sourceId, $"{graphic.Name} · {element.Key}", kind,
            SettingsJson(element, graphic.Style)), ct).ConfigureAwait(false);
        await engine.AddItemAsync(graphic.Id, itemId, sourceId, ct).ConfigureAwait(false);
        if (element.Kind == GraphicElementKind.Ticker)
            await engine.AddFilterAsync(sourceId, ScrollFilterId(graphic.Id, element.Key), "scroll_filter", "Desplazamiento",
                ScrollSettings(graphic, element), ct).ConfigureAwait(false);
        if (element.Design is { Kind: DesignElementKind.Image or DesignElementKind.Video } design)
        {
            // Las imágenes y los videos no tienen opacidad propia en libobs: se aplica con un filtro de color.
            await engine.AddFilterAsync(sourceId, OpacityFilterId(graphic.Id, element.Key), "color_filter_v2", "Opacidad",
                OpacitySettings(design), ct).ConfigureAwait(false);
            if (design.Kind == DesignElementKind.Video)
                await engine.SetMutedAsync(sourceId, design.Muted, ct).ConfigureAwait(false);
        }
        await engine.SetItemTransformAsync(itemId, await ElementTransformAsync(engine, sourceId, element, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
    }

    private static async Task RemovePieceAsync(IStreamingEngine engine, Guid graphicId, string key, CancellationToken ct)
    {
        await engine.RemoveItemAsync(ElementItemId(graphicId, key), ct).ConfigureAwait(false);
        await engine.RemoveSourceAsync(ElementSourceId(graphicId, key), ct).ConfigureAwait(false);
    }

    private static string OpacitySettings(DesignElement design) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["opacity"] = Math.Clamp(design.Opacity, 0, 1) });

    /// <summary>Cambia solo el texto de una pieza (cuenta atrás, reloj).</summary>
    public static Task UpdateTextAsync(IStreamingEngine engine, StudioProject project, Graphic graphic, string key, string value,
        CancellationToken ct)
    {
        var layout = GraphicTemplates.Layout(graphic, project.Canvas, k => k == key ? value : null);
        var element = layout.Elements.First(e => e.Key == key);
        return engine.UpdateSourceSettingsAsync(ElementSourceId(graphic.Id, key), SettingsJson(element, graphic.Style), ct);
    }

    public static async Task RemoveAsync(IStreamingEngine engine, StudioProject project, Graphic graphic, CancellationToken ct,
        bool ignoreErrors = false)
    {
        // Retirar la escena del gráfico suelta su elemento en la capa y los elementos de sus piezas.
        await Try(() => engine.RemoveSceneAsync(graphic.Id, ct)).ConfigureAwait(false);
        foreach (var element in GraphicTemplates.Layout(graphic, project.Canvas).Elements)
            await Try(() => engine.RemoveSourceAsync(ElementSourceId(graphic.Id, element.Key), ct)).ConfigureAwait(false);

        async Task Try(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception) when (ignoreErrors)
            {
                // Limpieza tras un fallo parcial: parte del gráfico nunca llegó a crearse.
            }
        }
    }

    private static async Task<Transform> ElementTransformAsync(IStreamingEngine engine, Guid sourceId, GraphicElement element, CancellationToken ct)
    {
        if (element.Design is { Kind: DesignElementKind.Image or DesignElementKind.Video } d)
        {
            // Diseño libre: la imagen o el video llenan su caja (tras el recorte), girados como el elemento.
            var (sw, sh) = await engine.GetSourceSizeAsync(sourceId, ct).ConfigureAwait(false);
            var placed = Transform.Identity.WithPosition(Math.Round(element.X), Math.Round(element.Y)) with { RotationDegrees = element.Rotation };
            var visibleW = sw - d.CropLeft - d.CropRight;
            var visibleH = sh - d.CropTop - d.CropBottom;
            // shortcut: un video aún sin primer fotograma mide 0x0 y queda a escala 1 hasta la siguiente actualización.
            if (visibleW <= 0 || visibleH <= 0)
                return placed;
            return placed.WithScale(element.Width / visibleW, element.Height / visibleH).WithCrop(d.CropLeft, d.CropTop, d.CropRight, d.CropBottom);
        }
        if (element.Design is not null)
            return Transform.Identity.WithPosition(Math.Round(element.X), Math.Round(element.Y)) with { RotationDegrees = element.Rotation };
        if (element.Kind == GraphicElementKind.Image)
        {
            // Encaja la imagen en su caja conservando la proporción, centrada.
            var (w, h) = await engine.GetSourceSizeAsync(sourceId, ct).ConfigureAwait(false);
            if (w <= 0 || h <= 0)
                return Transform.Identity.WithPosition(Math.Round(element.X), Math.Round(element.Y));
            var fit = Math.Min(element.Width / w, element.Height / h);
            return Transform.Identity.WithScale(fit, fit)
                .WithPosition(Math.Round(element.X + (element.Width - w * fit) / 2), Math.Round(element.Y + (element.Height - h * fit) / 2));
        }
        if (element.Kind != GraphicElementKind.Ticker)
            return Transform.Identity.WithPosition(Math.Round(element.X), Math.Round(element.Y));
        // El cintillo tiene el alto natural del texto: se centra verticalmente en su banda.
        var (_, height) = await engine.GetSourceSizeAsync(sourceId, ct).ConfigureAwait(false);
        var y = element.Y + Math.Max(0, (element.Height - height) / 2);
        return Transform.Identity.WithPosition(Math.Round(element.X), Math.Round(y));
    }

    private static string ScrollSettings(Graphic graphic, GraphicElement element)
    {
        var speed = double.TryParse(graphic.Field("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 120;
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["speed_x"] = Math.Clamp(speed, 10, 500),
            ["speed_y"] = 0.0,
            ["limit_cx"] = true,
            ["cx"] = (int)Math.Round(element.Width),
            ["limit_cy"] = false,
            ["loop"] = true,
        });
    }

    /// <summary>Ajustes nativos de la pieza: color_source_v3, image_source o text_gdiplus_v3.</summary>
    public static string SettingsJson(GraphicElement element, GraphicStyle style)
    {
        if (element.Kind == GraphicElementKind.Image)
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = element.Text });
        if (element.Kind == GraphicElementKind.Video)
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["is_local_file"] = true,
                ["local_file"] = element.Text,
                ["looping"] = true,
                ["restart_on_activate"] = false,
                ["close_when_inactive"] = false,
            });
        var d = element.Design;
        var abgr = WithOpacity(GraphicColor.ToAbgr(element.Color), d?.Opacity ?? 1);
        if (element.Kind == GraphicElementKind.Rect)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["color"] = (long)abgr,
                ["width"] = (int)Math.Round(element.Width),
                ["height"] = (int)Math.Round(element.Height),
            });
        }

        var settings = new Dictionary<string, object>
        {
            ["text"] = element.Text,
            ["font"] = new Dictionary<string, object>
            {
                ["face"] = d?.FontFace ?? style.FontFace,
                ["size"] = Math.Max(6, (int)Math.Round(element.FontSize)),
                // Banderas de libobs: 1 = negrita, 2 = cursiva.
                ["flags"] = (element.Bold ? 1 : 0) | (d?.Italic == true ? 2 : 0),
                ["style"] = (element.Bold, d?.Italic == true) switch
                {
                    (true, true) => "Bold Italic",
                    (true, false) => "Bold",
                    (false, true) => "Italic",
                    _ => "Regular",
                },
            },
            // text_gdiplus guarda el color sin alfa (0xBBGGRR) y la opacidad aparte (0..100).
            ["color"] = (long)(abgr & 0xFFFFFF),
            ["opacity"] = (int)Math.Round((abgr >> 24) * 100 / 255.0),
            ["antialiasing"] = true,
            ["align"] = element.Align,
            ["valign"] = "center",
        };
        if (element.Kind == GraphicElementKind.Text)
        {
            settings["extents"] = true;
            settings["extents_cx"] = Math.Max(1, (int)Math.Round(element.Width));
            settings["extents_cy"] = Math.Max(1, (int)Math.Round(element.Height));
            // Los textos de un diseño libre se ajustan a su caja en varias líneas.
            settings["extents_wrap"] = d is not null;
        }
        else
        {
            settings["extents"] = false;
        }
        if (d?.GradientColor is { } gradient)
        {
            var g = WithOpacity(GraphicColor.ToAbgr(gradient), d.Opacity);
            settings["gradient"] = true;
            settings["gradient_color"] = (long)(g & 0xFFFFFF);
            settings["gradient_opacity"] = (int)Math.Round((g >> 24) * 100 / 255.0);
            settings["gradient_dir"] = 90.0;
        }
        if (d is { OutlineColor: { } outline, OutlineSize: > 0 })
        {
            var o = WithOpacity(GraphicColor.ToAbgr(outline), d.Opacity);
            settings["outline"] = true;
            settings["outline_size"] = d.OutlineSize;
            settings["outline_color"] = (long)(o & 0xFFFFFF);
            settings["outline_opacity"] = (int)Math.Round((o >> 24) * 100 / 255.0);
        }
        return JsonSerializer.Serialize(settings);
    }

    /// <summary>Multiplica el alfa de un color ABGR por la opacidad del elemento.</summary>
    private static uint WithOpacity(uint abgr, double opacity) =>
        (uint)Math.Round((abgr >> 24) * Math.Clamp(opacity, 0, 1)) << 24 | (abgr & 0xFFFFFF);
}
