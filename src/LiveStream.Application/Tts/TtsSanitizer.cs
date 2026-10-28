using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveStream.Application.Tts;

/// <summary>
/// Módulo de sanitización y protección contra comentarios maliciosos o spam para síntesis de voz (TTS).
/// Neutraliza HTML, acorta enlaces web, contrae repeticiones de caracteres y aplica filtros de moderación.
/// </summary>
public static partial class TtsSanitizer
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RepeatedCharRegex = new(@"(.)\1{3,}", RegexOptions.Compiled);
    private static readonly Regex ControlCharRegex = new(@"[\u0000-\u001F\u007F-\u009F\u200B-\u200D\uFEFF]", RegexOptions.Compiled);
    private static readonly Regex MultipleSpacesRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Limpia y normaliza el texto de un comentario antes de enviarlo al motor TTS.
    /// Devuelve null o vacío si el mensaje fue totalmente filtrado o bloqueado.
    /// </summary>
    public static string Sanitize(
        string input,
        int maxLength = 160,
        bool filterUrls = true,
        bool reduceRepeatedChars = true,
        IReadOnlyList<string>? bannedWords = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // 1. Eliminar caracteres de control y secuencias no imprimibles
        var clean = ControlCharRegex.Replace(input, " ");

        // 2. Desactivar etiquetas HTML / XML
        clean = HtmlTagRegex.Replace(clean, string.Empty);

        // 3. Filtrar o acortar enlaces web para que no se lean cadenas URL ininteligibles
        if (filterUrls)
        {
            clean = UrlRegex.Replace(clean, match =>
            {
                try
                {
                    var uri = new Uri(match.Value);
                    return $"enlace a {uri.Host}";
                }
                catch
                {
                    return "enlace web";
                }
            });
        }

        // 4. Reducir spam de caracteres repetidos (ej. "holaaaaaaaa" -> "holaa")
        if (reduceRepeatedChars)
        {
            clean = RepeatedCharRegex.Replace(clean, "$1$1");
        }

        // 5. Filtrar palabras prohibidas (moderación)
        if (bannedWords is { Count: > 0 })
        {
            foreach (var word in bannedWords)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                var pattern = $@"\b{Regex.Escape(word)}\b";
                clean = Regex.Replace(clean, pattern, "***", RegexOptions.IgnoreCase);
            }
        }

        // 6. Normalizar espacios múltiples
        clean = MultipleSpacesRegex.Replace(clean, " ").Trim();

        // 7. Evitar que un comentario comience con caracteres que simulen comandos (/, !, .)
        if (clean.StartsWith('/') || clean.StartsWith('!') || clean.StartsWith('.'))
        {
            clean = clean.TrimStart('/', '!', '.', ' ');
        }

        // 8. Limitar la longitud máxima para evitar monopolización del audio
        if (clean.Length > maxLength && maxLength > 0)
        {
            clean = clean[..maxLength].TrimEnd() + "…";
        }

        return clean;
    }

    /// <summary>Comprueba si un texto contiene lenguaje prohibido explícito.</summary>
    public static bool ContainsBannedWords(string text, IReadOnlyList<string> bannedWords)
    {
        if (string.IsNullOrWhiteSpace(text) || bannedWords.Count == 0)
            return false;

        foreach (var word in bannedWords)
        {
            if (string.IsNullOrWhiteSpace(word))
                continue;

            if (Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }
}
