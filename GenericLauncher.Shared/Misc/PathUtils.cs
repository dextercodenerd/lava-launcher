using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericLauncher.Misc;

public static class PathUtils
{
    // Unicode control characters, format characters, and other problematic characters
    private static readonly char[] ProblematicUnicodeChars =
    [
        '\u00A0', // NO-BREAK SPACE (NBSP)
        '\u2000', // EN QUAD
        '\u2001', // EM QUAD
        '\u2002', // EN SPACE
        '\u2003', // EM SPACE
        '\u2004', // THREE-PER-EM SPACE
        '\u2005', // FOUR-PER-EM SPACE
        '\u2006', // SIX-PER-EM SPACE
        '\u2007', // FIGURE SPACE
        '\u2008', // PUNCTUATION SPACE
        '\u2009', // THIN SPACE
        '\u200A', // HAIR SPACE
        '\u200B', // ZERO WIDTH SPACE (ZWSP)
        '\u200C', // ZERO WIDTH NON-JOINER
        '\u200D', // ZERO WIDTH JOINER
        '\u200E', // LEFT-TO-RIGHT MARK (LRM)
        '\u200F', // RIGHT-TO-LEFT MARK (RLM)
        '\u202A', // LEFT-TO-RIGHT EMBEDDING (LRE)
        '\u202B', // RIGHT-TO-LEFT EMBEDDING (RLE)
        '\u202C', // POP DIRECTIONAL FORMATTING (PDF)
        '\u202D', // LEFT-TO-RIGHT OVERRIDE (LRO)
        '\u202E', // RIGHT-TO-LEFT OVERRIDE (RLO) - particularly dangerous!
        '\u2060', // WORD JOINER
        '\u2066', // LEFT-TO-RIGHT ISOLATE
        '\u2067', // RIGHT-TO-LEFT ISOLATE
        '\u2068', // FIRST STRONG ISOLATE
        '\u2069', // POP DIRECTIONAL ISOLATE
        '\uFEFF', // ZERO WIDTH NO-BREAK SPACE (BOM)
        '\uFFF9', // INTERLINEAR ANNOTATION ANCHOR
        '\uFFFA', // INTERLINEAR ANNOTATION SEPARATOR
        '\uFFFB', // INTERLINEAR ANNOTATION TERMINATOR
        '\uFFFC', // OBJECT REPLACEMENT CHARACTER
        '\uFFFD'
    ];

    // Non-characters (permanently reserved, invalid in any context)
    private static readonly (int Start, int End)[] NonCharacterRanges =
    [
        (0xFDD0, 0xFDEF), // Non-characters (Arabic presentation forms)
        (0xFFFE, 0xFFFF), // Non-characters (BMP non-characters)
        (0x1FFFE, 0x1FFFF), // Non-characters (Plane 1)
        (0x2FFFE, 0x2FFFF), // Non-characters (Plane 2)
        (0x3FFFE, 0x3FFFF), // Non-characters (Plane 3)
        (0x4FFFE, 0x4FFFF), // Non-characters (Plane 4)
        (0x5FFFE, 0x5FFFF), // Non-characters (Plane 5)
        (0x6FFFE, 0x6FFFF), // Non-characters (Plane 6)
        (0x7FFFE, 0x7FFFF), // Non-characters (Plane 7)
        (0x8FFFE, 0x8FFFF), // Non-characters (Plane 8)
        (0x9FFFE, 0x9FFFF), // Non-characters (Plane 9)
        (0xAFFFE, 0xAFFFF), // Non-characters (Plane 10)
        (0xBFFFE, 0xBFFFF), // Non-characters (Plane 11)
        (0xCFFFE, 0xCFFFF), // Non-characters (Plane 12)
        (0xDFFFE, 0xDFFFF), // Non-characters (Plane 13)
        (0xEFFFE, 0xEFFFF), // Non-characters (Plane 14)
        (0xFFFFE, 0xFFFFF) // Non-characters (Plane 15)
    ];

    // Individual problematic control characters from C1 range -- some of them are valid Latin-1
    private static readonly char[] C1ControlCharacters =
    [
        '\u0080', '\u0081', '\u0082', '\u0083', '\u0084', '\u0085', '\u0086', '\u0087',
        '\u0088', '\u0089', '\u008A', '\u008B', '\u008C', '\u008D', '\u008E', '\u008F',
        '\u0090', '\u0091', '\u0092', '\u0093', '\u0094', '\u0095', '\u0096', '\u0097',
        '\u0098', '\u0099', '\u009A', '\u009B', '\u009C', '\u009D', '\u009E', '\u009F'
    ];

    // Characters that are illegal on *any* of the three OSes. Let's start with .NET's useless APIs.
    // Path.GetInvalidPathChars() and Path.GetInvalidFileNameChars() are funny and docs say they are
    // broken. They return different results on different platforms, but we use them to be extra
    // sure. Even though we are doubly adding some of their characters.
    private static readonly char[] IllegalChars =
        Path.GetInvalidPathChars()
            .Concat(Path.GetInvalidFileNameChars())
            // ' ' (space), | (pipe) and slashes are common sense; NUL is illegal everywhere; '\u007F' is DEL and ':'
            // is sometimes illegal; There are others, which we want to remove to be sure.
            .Concat([' ', '|', '/', '\\', '\0', '\u007F', ':', '"', '<', '>', '!', '?', '$', '&', '~', '#', '%', '^'])
            // all ASCII control characters (0x00-0x1F) -- this also contains the '\0' again, but it is more readable
            .Concat(Enumerable.Range(0, 32).Select(i => (char)i)) // C0 controls
            .Concat(C1ControlCharacters)
            .Concat(ProblematicUnicodeChars)
            .Distinct()
            .ToArray();

    // Regex that matches any illegal char (fast pre-filter)
    private static readonly Regex IllegalCharRegex = new(
        $"[{Regex.Escape(new string(IllegalChars))}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // Reserved names on Windows (case-insensitive)
    private static readonly HashSet<string> ReservedFoldersNames = new(
        [
            ".", "..", "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    // Max length for a single path component (POSIX: 255 bytes, Windows: 255 chars)
    private const int MaxComponentLength = 255;

    /// <summary>
    /// There is a .NET method Path.GetInvalidPathChars() which is broker. Even the docs say "The array returned from
    /// this method is not guaranteed to contain the complete set of characters that are invalid in file and directory
    /// names. The full set of invalid characters can vary by file system."; I get it, but common MS, it's your job. It
    /// doesn't even have a parameter for file system, so it just pulls the array out of thin air...
    ///
    /// And thus we have our own method, that will allow valid characters shared across Windows, Linux and macOS. Very
    /// limited subset, but valid.
    ///
    /// !!! We are NOT handling ligatures e.g., "ﬃ" and "ffi" are different things !!!
    ///
    /// </summary>
    /// <param name="name">Directory name to sanitize</param>
    /// <param name="replacement">String to replace illegal characters with (default: "_")</param>
    /// <returns>Sanitized directory name safe for Windows, Linux, and macOS</returns>
    /// <exception cref="ArgumentException">Thrown when input is null/whitespace or results is null or an empty string</exception>
    public static string SanitizeDirectoryName(string name, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name cannot be null nor just whitespace");
        }

        if (IllegalChars.Contains(replacement))
        {
            throw new ArgumentException("Replacement character is an illegal character");
        }

        // fix incomplete Unicode glyphs
        var cleaned = ReplaceInvalidUnicodeSequences(name, replacement);

        // normalize Unicode characters first, because there ary different ways to get/display the
        // same UTF glyph
        cleaned = cleaned.Normalize(NormalizationForm.FormC);

        // Trim leading/trailing spaces and dots. A leading dot means a hidden folder on
        // Linux/macOS/POSIX systems.
        cleaned = cleaned.Trim('.', ' ');

        // replace illegal characters
        cleaned = IllegalCharRegex.Replace(cleaned, replacement.ToString());

        // sanitize reserved names
        var upper = cleaned.ToUpperInvariant();
        if (ReservedFoldersNames.Contains(upper) ||
            (upper.EndsWith('.') && ReservedFoldersNames.Contains(upper.TrimEnd('.'))))
        {
            // e.g. "CON" -> "CON_"
            cleaned += replacement;
        }

        // and enforce the length limit at last
        if (cleaned.Length > MaxComponentLength)
        {
            cleaned = cleaned[..MaxComponentLength];

            // This can potentially create a collision with reserved folder names, but the lenght here is 255 chars and
            // the reserved names are at most 4 characters long.
        }

        return string.IsNullOrEmpty(cleaned)
            ? throw new InvalidOperationException("Sanitized name is null or empty")
            : cleaned;
    }

    public static string IncrementNumberedFolderNameIfExistsAndSanitize(string rawFolderName,
        string[] rawExistingFolders)
    {
        var sanitizedFolderName = SanitizeDirectoryName(rawFolderName);
        if (rawExistingFolders.Length == 0)
        {
            return sanitizedFolderName;
        }

        var collidingFolders = rawExistingFolders
            .Select(f => SanitizeDirectoryName(f))
            .Where(name => name.StartsWith(rawFolderName))
            .ToList();


        // Case-insensitive because of some file systems
        if (!collidingFolders.Any(f => string.Equals(f, sanitizedFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            return sanitizedFolderName;
        }

        // Regex to match folders with suffix _(number)
        var pattern = new Regex($@"^{Regex.Escape(rawFolderName)}_\((\d+)\)$", RegexOptions.IgnoreCase);

        var maxNumber = collidingFolders
            .Select(folder => pattern.Match(folder))
            .Where(match => match.Success)
            .Select(match => long.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        var nextNumber = maxNumber + 1;
        return $"{sanitizedFolderName}_({nextNumber})";
    }

    private static string ReplaceInvalidUnicodeSequences(string input, char replacement)
    {
        // Remove isolated high surrogates without their low surrogates
        var result = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c < '\ud800')
            {
                result.Append(c);
                continue;
            }

            if (IsNonCharacter(c))
            {
                result.Append(replacement);
                continue;
            }

            // If we see low surrogate before a high one, the string is invalid
            if (char.IsLowSurrogate(c))
            {
                result.Append(replacement);
                continue;
            }

            if (!char.IsHighSurrogate(c))
            {
                result.Append(c);
                continue;
            }

            // Here we have a high surrogate and check if the next one is a low surrogate
            if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(c, input[i + 1]);

                if (IsNonCharacterCodePoint(codePoint))
                {
                    result.Append(replacement);
                    result.Append(replacement);
                }
                else
                {
                    result.Append(c);
                    result.Append(input[i + 1]);
                }

                // Consume the low surrogate
                i++;
            }
            else
            {
                // A high surrogate at the end of the string or a high surrogate
                // not followed by a low surrogate
                result.Append(replacement);
            }
        }

        return result.ToString();
    }

    private static bool IsNonCharacter(char c)
    {
        foreach (var range in NonCharacterRanges)
        {
            if (c >= range.Start && c <= range.End)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonCharacterCodePoint(int codePoint)
    {
        // Check BMP non-characters
        foreach (var range in NonCharacterRanges)
        {
            if (codePoint >= range.Start && codePoint <= range.End)
            {
                return true;
            }
        }

        // Check higher plane non-characters (end with FFFE or FFFF)
        if (codePoint >= 0x10000)
        {
            var lastBits = codePoint & 0xFFFF;
            // TODO: Maybe check all the NonCharacterRanges?
            if (lastBits == 0xFFFE || lastBits == 0xFFFF)
            {
                return true;
            }
        }

        return false;
    }
}
