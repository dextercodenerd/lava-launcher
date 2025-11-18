using System;
using System.IO;
using System.Linq;
using System.Text;
using GenericLauncher.Misc;
using JetBrains.Annotations;
using Xunit;

namespace GenericLauncher.Tests.Misc;

[TestSubject(typeof(PathUtils))]
public class PathUtilsTest
{
    private static readonly char[] IllegalFolderChars = Path.GetInvalidPathChars()
        .Concat(Path.GetInvalidFileNameChars())
        .Distinct()
        .ToArray();

    private static NormalizationForm GetNormalizationForm(string s) =>
        s.IsNormalized(NormalizationForm.FormC) ? NormalizationForm.FormC :
        s.IsNormalized(NormalizationForm.FormD) ? NormalizationForm.FormD :
        s.IsNormalized(NormalizationForm.FormKC) ? NormalizationForm.FormKC :
        s.IsNormalized(NormalizationForm.FormKD) ? NormalizationForm.FormKD :
        throw new InvalidOperationException("String is not in a standard normalization form.");

    private static string ToNFC(string s) => s.Normalize(NormalizationForm.FormC);

    private static void AssertSameSanitizedResult(string input1, string input2, char replacement = '_')
    {
        var result1 = PathUtils.SanitizeDirectoryName(input1, replacement);
        var result2 = PathUtils.SanitizeDirectoryName(input2, replacement);

        var nfc1 = ToNFC(result1);
        var nfc2 = ToNFC(result2);

        Assert.Equal(nfc1, nfc2, false);
        Assert.Equal(result1, result2); // Should be identical *after* sanitization
    }

    [Fact]
    public void Allow_Parentheses()
    {
        const string input = "folder_(1)";
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SanitizeDirectoryName_NullInput_ThrowsArgumentNullException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        // This can still happen in runtime...
        Assert.Throws<ArgumentException>(() =>
            PathUtils.SanitizeDirectoryName(null));
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n\r")]
    [InlineData(" \t \n ")]
    public void SanitizeDirectoryName_EmptyInput_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() =>
            PathUtils.SanitizeDirectoryName(input));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(". ")]
    public void SanitizeDirectoryName_ErasedInput_Throws(string input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PathUtils.SanitizeDirectoryName(input));
    }

    [Theory]
    [InlineData("ValidName", "ValidName")]
    [InlineData("My Folder 123", "My_Folder_123")]
    [InlineData("Überstraße", "Überstraße")]
    [InlineData("こんにちは世界", "こんにちは世界")]
    public void SanitizeDirectoryName_ValidCharacters_PassesThrough(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Normal\u0085Text", "Normal_Text")]
    public void SanitizeDirectoryName_InvalidCharacters_Replaced(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("  LeadingSpace", "LeadingSpace")]
    [InlineData("TrailingSpace  ", "TrailingSpace")]
    [InlineData("  Both  ", "Both")]
    public void SanitizeDirectoryName_ValidCharacters_TrimLeadingSpaces(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("lpt1.txt", "lpt1.txt")]
    [InlineData("NuL.", "NuL_")]
    public void SanitizeDirectoryName_ReservedDeviceNames_AllowedAsIs(string input, string expected)
    {
        // Note: Some sanitizers allow these if not exactly reserved; test behavior.
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Bad<char>Name", "Bad_char_Name")]
    [InlineData("Dir*?", "Dir__")]
    [InlineData("Hello|World", "Hello_World")]
    [InlineData("Quotes\"Here", "Quotes_Here")]
    [InlineData("Colon:Bad", "Colon_Bad")]
    [InlineData("<>:/\\|?*", "________")]
    public void SanitizeDirectoryName_InvalidPathChars_Replaced(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input, '_');
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_AllInvalidChars_ReplacedWithReplacement()
    {
        var input = new string(IllegalFolderChars);
        var expected = new string('_', IllegalFolderChars.Length);
        var result = PathUtils.SanitizeDirectoryName(input, '_');
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" . ")]
    public void SanitizeDirectoryName_FullyErasedPaths(string input)
    {
        Assert.Throws<InvalidOperationException>(() => { PathUtils.SanitizeDirectoryName(input, '_'); });
    }

    [Theory]
    [InlineData(".dir", "dir")]
    [InlineData("..dir", "dir")]
    [InlineData("dir.", "dir")]
    public void SanitizeDirectoryName_DotOnlyOrDotEnding_HandledSafely(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input, '_');
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\0Embedded\0Null", "_Embedded_Null")]
    [InlineData("\u0001\u0002\u0003", "___")]
    [InlineData("\u007F\u0080\u009F", "___")] // DEL + C1 controls
    public void SanitizeDirectoryName_ControlCharacters_Replaced(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_PathTraversalAttempt_Replaced()
    {
        const string input = @"..\secret\..\config";
        const string expected = "_secret_.._config";
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_VeryLongName_TruncatesOrHandlesGracefully()
    {
        var longName = new string('a', 300) + "<>*";
        var result = PathUtils.SanitizeDirectoryName(longName, '_');

        // Should not throw, should replace invalid chars
        Assert.DoesNotContain(result, c => c == '<' || c == '>' || c == '*' || c == ':');
        Assert.True(result.Length <= 300 + 3); // reasonable upper bound
    }

    [Theory]
    [InlineData("Bad*Name", '-', "Bad-Name")]
    public void SanitizeDirectoryName_CustomReplacement_UsedCorrectly(string input, char replacement, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input, replacement);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ReplacementIsDollar$", '$')]
    [InlineData("ReplacementIsDollar ", ' ')]
    [InlineData("ReplacementIsDollar\0", '\0')]
    public void SanitizeDirectoryName_CustomReplacement_Illegal(string input, char replacement)
    {
        Assert.Throws<ArgumentException>(() => { PathUtils.SanitizeDirectoryName(input, replacement); });
    }

    [Theory]
    [InlineData("Folder\u200BName", "Folder_Name")] // Zero-width space
    [InlineData("Invisible\u2060Joiner", "Invisible_Joiner")] // Word joiner
    [InlineData("RTL\u202EOverride", "RTL_Override")]
    public void SanitizeDirectoryName_UnicodeZeroWidthAndBidi_Replaced(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input, '_');
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_GarbageClipboardInput_Sanitized()
    {
        // Simulate copy-paste from Word/email/web with junk
        const string garbage = "  My\tDoc*\nWith\"Quotes\"&\r\n<evil>tag</evil>\u00A0\u200B..\u202Ahidden\u202B";
        var result = PathUtils.SanitizeDirectoryName(garbage);

        Assert.Equal("My_Doc__With_Quotes_____evil_tag__evil___.._hidden_", result);
    }

    [Theory]
    [InlineData("COM1", "COM1_")]
    [InlineData("com9.txt", "com9.txt")]
    [InlineData("Aux", "Aux_")]
    public void SanitizeDirectoryName_ReservedNames_CaseInsensitiveCheck(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_EmojiAndSymbols_ReplacedOrKept()
    {
        // emojis are usually allowed in modern filesystems...
        const string input = "My 📁 Folder 🎉";
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Contains("📁", result);
        Assert.Contains("🎉", result);
    }

    [Fact]
    public void SanitizeDirectoryName_SurrogatePairs_Handled()
    {
        const string input = "Ancient𐍈Text";
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Contains("𐍈", result);
    }

    [Theory]
    [InlineData("\uFFFE\uFFFF", "__")] // Byte Order Marks / non-characters
    [InlineData("\uFDD0\uFDEF", "__")] // Non-printable Arabic
    public void SanitizeDirectoryName_NonCharacters_Replaced(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeDirectoryName_ConsecutiveInvalidChars_CollapsedOrReplacedIndividually()
    {
        const string input = "!!!***???";
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal("_________", result);
    }

    [Theory]
    [InlineData("café", "cafe\u0301")] // NFC vs NFD
    [InlineData(" naïve ", " nai\u0308ve ")] // diaeresis
    [InlineData("Ångström", "A\u030Angstro\u0308m")]
    public void SanitizeDirectoryName_EquivalentUnicodeForms_YieldSameResult(string nfc, string nfd)
    {
        AssertSameSanitizedResult(nfc, nfd);
    }

    [Fact]
    public void SanitizeDirectoryName_WithInvalidChars_StillNormalizesToSameNFC()
    {
        // both have invalid chars and different normalization
        const string input1 = "café*bad"; // NFC + invalid
        const string input2 = "cafe\u0301*bad"; // NFD + invalid

        AssertSameSanitizedResult(input1, input2);
    }

    [Theory]
    [InlineData("가", "가")] // Hangul: composed vs jamo
    [InlineData("힣", "힣")] // Final Hangul syllable
    [InlineData("각", "각")] // LV syllable
    public void SanitizeDirectoryName_Hangul_ComposedVsJamo_SameResult(string composed, string jamo)
    {
        AssertSameSanitizedResult(composed, jamo);
    }

    [Fact]
    public void SanitizeDirectoryName_OutputIsAlwaysNFC()
    {
        const string input = "cafe\u0301*naïve"; // NFD + invalid char
        var result = PathUtils.SanitizeDirectoryName(input);

        Assert.Equal(NormalizationForm.FormC, GetNormalizationForm(result));
        Assert.True(result.IsNormalized(NormalizationForm.FormC));
    }

    [Theory]
    [InlineData("Schön", "Scho\u0308n")]
    [InlineData("Überstraße", "U\u0308berstraße")]
    [InlineData("Ĺatvian", "L\u0301atvian")] // Combining acute
    public void SanitizeDirectoryName_AccentCombinations_SameSanitizedNFC(string nfc, string nfd)
    {
        AssertSameSanitizedResult(nfc, nfd);
    }

    [Fact]
    public void SanitizeDirectoryName_MultipleCombiningMarks_OrderPreservedButNormalized()
    {
        // U+1E69 = s + dot below + dot above
        const string composed = "ṩ"; // NFC: single codepoint
        const string decomposed = "s\u0323\u0307"; // NFD: s + dot below + dot above

        AssertSameSanitizedResult(composed, decomposed);
    }

    [Fact]
    public void SanitizeDirectoryName_GarbageWithCombiningAndInvalid_SameResult()
    {
        const string input1 = "cafe\u0301*naïve\t\r\n"; // NFD + invalid + whitespace
        const string input2 = "café*naïve\t\r\n"; // NFC + same

        AssertSameSanitizedResult(input1, input2);
    }

    [Theory]
    [InlineData("ﷺ", "صلى الله عليه وسلم")] // Arabic presentation form vs full text
    [InlineData("㈜", "(주)")] // Korean parenthesized Hangul
    public void SanitizeDirectoryName_PresentationForms_HandledConsistently(string presentation, string full)
    {
        // These are *not* normalization equivalents, but often pasted interchangeably
        // We at least assert they don't crash and output is NFC
        var r1 = PathUtils.SanitizeDirectoryName(presentation);
        var r2 = PathUtils.SanitizeDirectoryName(full);

        Assert.True(r1.IsNormalized(NormalizationForm.FormC));
        Assert.True(r2.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void SanitizeDirectoryName_StressTest_MixedNormalizationAndGarbage()
    {
        var sb1 = new StringBuilder();
        var sb2 = new StringBuilder();

        var pairs = new[]
        {
            ("é", "e\u0301"),
            ("ñ", "n\u0303"),
            ("ü", "u\u0308"),
            ("ṩ", "s\u0323\u0307")
        };

        const string garbage = "*<>:\"/\\|?*\0\t\r\n .\u200B..";

        foreach (var (nfc, nfd) in pairs)
        {
            sb1.Append(nfc).Append(garbage);
            sb2.Append(nfd).Append(garbage);
        }

        AssertSameSanitizedResult(sb1.ToString(), sb2.ToString());
    }

    [Theory]
    [InlineData("\uD83D\uDE00", "\uD83D\uDE00")] // Valid: 😀 (U+1F600)
    [InlineData("\uD83C\uDF89", "\uD83C\uDF89")] // Valid: 🎉 (U+1F389)
    [InlineData("A\uD83D\uDE00B", "A\uD83D\uDE00B")] // A😀B
    [InlineData("\uD83D\uDE00\uD83C\uDF89", "\uD83D\uDE00\uD83C\uDF89")] // 😀🎉
    [InlineData("Hello\uD83D\uDE00World", "Hello\uD83D\uDE00World")] // Mixed with text
    public void PreserveValidSurrogatePairs(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Testing non-Unicode legal strings with xUnit is tricky, because they will get "mangled" when
    /// converted to Unicode during the serialization process because they convert to UTF-8.
    /// A single \uD800 is, by itself, not legal Unicode.
    ///
    /// An easy workaround is to use char[]
    ///
    /// https://github.com/xunit/xunit/issues/2024
    /// </summary>
    /// <param name="input"></param>
    /// <param name="expected"></param>
    [Theory]
    [InlineData(new[] { '\uDC00' }, "_")] // Isolated low surrogate
    [InlineData(new[] { '\uD800', '\uD800' }, "__")] // Two high surrogates
    [InlineData(new[] { '\uDC00', '\uDC00' }, "__")] // Two low surrogates
    [InlineData(new[] { '\uD800', '\uDC00' }, "\uD800\uDC00")] // Valid pair
    [InlineData(new[] { '\uD800', 'N', 'o', 'r', 'm', 'a', 'l' }, "_Normal")] // High surrogate + text
    [InlineData(new[] { 'N', 'o', 'r', 'm', 'a', 'l', '\uDC00' }, "Normal_")] // Text + low surrogate
    [InlineData(new[] { 'M', 'i', 'd', '\uD800', 'd', 'l', 'e' },
        "Mid_dle")] // Text + high surrogate + text
    [InlineData(new[] { 'M', 'i', 'd', '\uDC00', 'd', 'l', 'e' },
        "Mid_dle")] // Text + low surrogate + text
    [InlineData(new[] { '\uD800', '\uDC00', '\uD800' }, "\uD800\uDC00_")] // Valid pair + high surrogate
    [InlineData(new[] { '\uD800', '\uDC00', '\uDC00' }, "\uD800\uDC00_")] // Valid pair + low surrogate
    [InlineData(new[]
        {
            'n', 'o', 'r', 'm', 'a', 'l', '\uDC00', 'i', 'n', 'v', 'a', 'l', 'i', 'd', '\u0000', 'c', 'h', 'a', 'r',
            's', '\uD800'
        },
        "normal_invalid_chars_")]
    public void RemoveInvalidSurrogates(char[] input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(new string(input));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("abc\u0000", "abc_")] // NULL
    [InlineData("abc\u0007", "abc_")] // BELL
    [InlineData("abc\u0008", "abc_")] // BACKSPACE
    [InlineData("abc\u001F", "abc_")] // UNIT SEPARATOR
    [InlineData("abc\u007F", "abc_")] // DELETE
    [InlineData("abc\u0085", "abc_")] // NEXT LINE (C1)
    [InlineData("abc\u009C", "abc_")] // STRING TERMINATOR (C1)
    [InlineData("Text\u0007With\u001FControls", "Text_With_Controls")] // Mixed
    public void RemoveControlCharacters(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\uFFFE", "_")] // Non-character
    [InlineData("\uFFFF", "_")] // Non-character  
    [InlineData("\uFDD0", "_")] // Non-character
    [InlineData("\uFDEF", "_")] // Non-character
    [InlineData("Valid\uFFFENonChar", "Valid_NonChar")] // Mixed
    [InlineData("\uFDD0\uFDEF\uFFFE\uFFFF", "____")] // Multiple non-characters
    public void RemoveNonCharacters(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("\u200B", "_")] // Zero-width space
    [InlineData("\u200C", "_")] // Zero-width non-joiner
    [InlineData("\u200D", "_")] // Zero-width joiner
    [InlineData("\u200E", "_")] // Left-to-right mark
    [InlineData("\u200F", "_")] // Right-to-left mark
    [InlineData("\u202A", "_")] // Left-to-right embedding
    [InlineData("\u202B", "_")] // Right-to-left embedding
    [InlineData("\u202C", "_")] // Pop directional formatting
    [InlineData("\u202D", "_")] // Left-to-right override
    [InlineData("\u202E", "_")] // Right-to-left override (dangerous!)
    [InlineData("Invisible\u200BHere", "Invisible_Here")] // Mixed
    public void RemoveFormatAndBidirectionalCharacters(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Überstraße", "Überstraße")] // German
    [InlineData("Café", "Café")] // French
    [InlineData("naïve", "naïve")] // French/English
    [InlineData("jalapeño", "jalapeño")] // Spanish
    [InlineData("São Paulo", "São_Paulo")] // Portuguese
    [InlineData("Malmö", "Malmö")] // Swedish
    [InlineData("Żółć", "Żółć")] // Polish
    [InlineData("Ærøskøbing", "Ærøskøbing")] // Danish
    [InlineData("Reykjavík", "Reykjavík")] // Icelandic
    [InlineData("北京", "北京")] // Chinese
    [InlineData("東京", "東京")] // Japanese
    [InlineData("서울", "서울")] // Korean
    [InlineData("Дмитрий", "Дмитрий")] // Russian
    [InlineData("Ελλάδα", "Ελλάδα")] // Greek
    public void PreserveValidUnicodeCharacters(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("folder\uD83D\uDE00name\u0085with\u200Bmixed\uFFFEissues", "folder\uD83D\uDE00name_with_mixed_issues")]
    [InlineData("  \u0007start\u202Ewith\uFDD0problems  ", "_start_with_problems")]
    [InlineData("file/with\\multiple:invalid*chars?\"and<controls>\u0007",
        "file_with_multiple_invalid_chars__and_controls__")]
    public void ComplexMixedScenarios(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(
        "ThisIsAVeryLongFolderNameThatExceedsTheNormalLengthLimitAndShouldBeTruncatedToPreventFilesystemIssuesAndEnsureCrossPlatformCompatibilityWithVariousOperatingSystemsAndTheirRespectivePathLengthLimitations",
        "ThisIsAVeryLongFolderNameThatExceedsTheNormalLengthLimitAndShouldBeTruncatedToPreventFilesystemIssuesAndEnsureCrossPlatformCompatibilityWithVariousOperatingSystemsAndTheirRespectivePathLengthLimitations")]
    public void LengthTruncation(string input, string expected)
    {
        var result = PathUtils.SanitizeDirectoryName(input);
        Assert.True(result.Length <= 255);
        if (input.Length <= 255)
        {
            Assert.Equal(expected, result);
        }
        else
        {
            Assert.Equal(255, result.Length);
        }
    }

    [Fact]
    public void Test_Numbered_Folder_Name_Simple()
    {
        const string folderName = "vanilla";
        string[] existingFolders = ["vanilla"];

        var result = PathUtils.IncrementNumberedFolderNameIfExistsAndSanitize(folderName, existingFolders);
        Assert.Equal("vanilla_(1)", result);
    }

    [Fact]
    public void Test_Numbered_Folder_Name_Comple()
    {
        const string folderName = "vanilla";
        string[] existingFolders = ["vanilla", "vanilla (1)", "vanilla (3)"];

        var result = PathUtils.IncrementNumberedFolderNameIfExistsAndSanitize(folderName, existingFolders);
        Assert.Equal("vanilla_(4)", result);
    }
}
