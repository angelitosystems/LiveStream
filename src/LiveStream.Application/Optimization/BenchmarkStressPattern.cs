namespace LiveStream.Application.Optimization;

/// <summary>
/// Genera patrones gráficos sintéticos de alto estrés entrópico (ruido pseudoaleatorio, barras SMPTE,
/// rejillas diagonales de alta frecuencia y dianas concéntricas) para someter al codificador de video
/// a cargas reales de cuantización DCT y estimación de movimiento.
/// Se guarda en formato BMP estándar sin compresión, sin dependencias externas y con carga nativa directa en libobs.
/// </summary>
public static class BenchmarkStressPattern
{
    public static byte[] GenerateBmpBytes(int width = 960, int height = 540)
    {
        if (width <= 0) width = 960;
        if (height <= 0) height = 540;

        var stride = (width * 3 + 3) & ~3;
        var imageSize = stride * height;
        var totalSize = 54 + imageSize;
        var buffer = new byte[totalSize];

        // BITMAPFILEHEADER (14 bytes)
        buffer[0] = 0x42; // 'B'
        buffer[1] = 0x4D; // 'M'
        BitConverter.TryWriteBytes(buffer.AsSpan(2, 4), totalSize);
        buffer[6] = 0; buffer[7] = 0; // Reserved1
        buffer[8] = 0; buffer[9] = 0; // Reserved2
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 4), 54); // Offset a bits

        // BITMAPINFOHEADER (40 bytes)
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 4), 40); // biSize
        BitConverter.TryWriteBytes(buffer.AsSpan(18, 4), width); // biWidth
        BitConverter.TryWriteBytes(buffer.AsSpan(22, 4), height); // biHeight (positivo = bottom-up)
        BitConverter.TryWriteBytes(buffer.AsSpan(26, 2), (short)1); // biPlanes
        BitConverter.TryWriteBytes(buffer.AsSpan(28, 2), (short)24); // biBitCount (24bpp BGR)
        BitConverter.TryWriteBytes(buffer.AsSpan(30, 4), 0); // biCompression (BI_RGB)
        BitConverter.TryWriteBytes(buffer.AsSpan(34, 4), imageSize); // biSizeImage
        BitConverter.TryWriteBytes(buffer.AsSpan(38, 4), 2835); // 72 DPI
        BitConverter.TryWriteBytes(buffer.AsSpan(42, 4), 2835);
        BitConverter.TryWriteBytes(buffer.AsSpan(46, 4), 0);
        BitConverter.TryWriteBytes(buffer.AsSpan(50, 4), 0);

        // SMPTE 7-bar colors (B, G, R)
        var smpteBars = new (byte B, byte G, byte R)[]
        {
            (220, 220, 220), // Blanco/Gris claro
            (0, 220, 220),   // Amarillo
            (220, 220, 0),   // Cian
            (0, 220, 0),     // Verde
            (220, 0, 220),   // Magenta
            (0, 0, 220),     // Rojo
            (220, 0, 0),     // Azul
        };

        var rng = new Random(42); // Semilla determinista para ruido de alto contraste constante
        var noiseRow = new byte[width * 3];
        rng.NextBytes(noiseRow);

        var dataOffset = 54;
        for (var visualY = 0; visualY < height; visualY++)
        {
            // Bottom-up: visualY=0 es la fila superior visible, corresponde a fileY = height - 1 - visualY
            var fileY = height - 1 - visualY;
            var rowStart = dataOffset + fileY * stride;

            for (var x = 0; x < width; x++)
            {
                byte b, g, r;

                if (visualY < (int)(height * 0.35))
                {
                    // Zona 1: Barras SMPTE de calibración broadcast
                    var barIndex = Math.Clamp(x * smpteBars.Length / width, 0, smpteBars.Length - 1);
                    (b, g, r) = smpteBars[barIndex];

                    // Borde divisor oscuro entre barras para contraste de bordes verticales
                    if (x % (width / smpteBars.Length) < 2)
                    {
                        b = 20; g = 20; r = 20;
                    }
                }
                else if (visualY < (int)(height * 0.72))
                {
                    // Zona 2: Estrés de alta frecuencia
                    if (x < width / 2)
                    {
                        // Izquierda: Rejilla diagonal de alto contraste + diana concéntrica
                        var cx = width * 0.25;
                        var cy = height * 0.535;
                        var dx = x - cx;
                        var dy = visualY - cy;
                        var dist = Math.Sqrt(dx * dx + dy * dy);

                        if (dist < 60)
                        {
                            // Diana central circular
                            var ring = ((int)dist / 8) % 2 == 0;
                            b = ring ? (byte)255 : (byte)0;
                            g = ring ? (byte)255 : (byte)0;
                            r = ring ? (byte)255 : (byte)0;
                        }
                        else
                        {
                            // Rejilla diagonal (forza predicción angular en H.264 intra y vectores inter)
                            var diag = ((x + visualY) / 14) % 2 == 0;
                            b = diag ? (byte)230 : (byte)40;
                            g = diag ? (byte)140 : (byte)20;
                            r = diag ? (byte)30 : (byte)200;
                        }
                    }
                    else
                    {
                        // Derecha: Ruido pseudoaleatorio denso (máxima entropía para cuantización DCT)
                        // Alterna muestras para generar cambios bruscos en todos los canales
                        var noiseIdx = (x * 3 + visualY * 17) % noiseRow.Length;
                        var sample = noiseRow[noiseIdx];
                        b = sample;
                        g = (byte)((sample * 3) ^ visualY);
                        r = (byte)((sample * 7) ^ x);
                    }
                }
                else
                {
                    // Zona 3: Patrones Nyquist (frecuencia límite de pixel a pixel) y degradados
                    if (x < width / 3)
                    {
                        // Líneas verticales alternas de 1 pixel blanco/negro
                        var line = (x % 2) == 0;
                        b = line ? (byte)255 : (byte)15;
                        g = line ? (byte)255 : (byte)15;
                        r = line ? (byte)255 : (byte)15;
                    }
                    else if (x < (width * 2 / 3))
                    {
                        // Líneas horizontales alternas de 1 pixel blanco/negro
                        var line = (visualY % 2) == 0;
                        b = line ? (byte)255 : (byte)15;
                        g = line ? (byte)255 : (byte)15;
                        r = line ? (byte)255 : (byte)15;
                    }
                    else
                    {
                        // Gradiente continuo RGB de transición suave
                        var progress = (x - (width * 2 / 3)) * 255 / (width / 3);
                        b = (byte)Math.Clamp(255 - progress, 0, 255);
                        g = (byte)Math.Clamp(progress, 0, 255);
                        r = (byte)Math.Clamp((progress * 2) % 256, 0, 255);
                    }
                }

                var pxOffset = rowStart + x * 3;
                buffer[pxOffset] = b;
                buffer[pxOffset + 1] = g;
                buffer[pxOffset + 2] = r;
            }
        }

        return buffer;
    }

    public static void SaveToFile(string path, int width = 960, int height = 540)
    {
        var bytes = GenerateBmpBytes(width, height);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }
}
