// Writes the tables of ManagedCompressions.Brotli from brotli 1.1.1's sources, so that every number in
// them comes from the reference implementation rather than from a hand copy:
//
//   dotnet run tools/brotli/generate-tables.cs -- <brotli-1.1.1 source tree>
//
// The source tree is the v1.1.1 release (tools/native-ref/brotli-source.sh downloads it). The files are
// written into src/ManagedCompressions.Brotli/Internal/, marked as generated; run this again rather than
// editing them.
#:property TargetFrameworks=
#:property TargetFramework=net11.0
#:property TreatWarningsAsErrors=false
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run tools/brotli/generate-tables.cs -- <brotli-1.1.1 source tree>");
    return 1;
}

string source = Path.Combine(args[0], "c");
string output = Path.Combine(RepositoryRoot(), "src", "ManagedCompressions.Brotli", "Internal");

// The static dictionary (RFC 7932, appendix A).
string dictionaryC = File.ReadAllText(Path.Combine(source, "common", "dictionary.c"));
long[] dictionary = Numbers(dictionaryC, "kBrotliDictionaryData[] =");
long[] sizeBits = Numbers(dictionaryC, "/* size_bits_by_length */");
long[] offsets = Numbers(dictionaryC, "/* offsets_by_length */");
Check(dictionary.Length == 122784, "the dictionary has 122784 bytes");
Check(sizeBits.Length == 32 && offsets.Length == 32, "the dictionary has 32 lengths");
var text = new StringBuilder();
Header(text, "c/common/dictionary.c");
text.AppendLine("internal static partial class StaticDictionary");
text.AppendLine("{");
text.AppendLine("    /// <summary>The 122,784 bytes of RFC 7932's static dictionary: its words, bucketed by length.</summary>");
Array(text, "public static ReadOnlySpan<byte> Data", dictionary, 24);
text.AppendLine();
text.AppendLine("    /// <summary>The number of bits of a word's index, for each length: 0 where no word has that length.</summary>");
Array(text, "public static ReadOnlySpan<byte> SizeBitsByLength", sizeBits, 16);
text.AppendLine();
text.AppendLine("    /// <summary>Where the words of each length start in <see cref=\"Data\"/>.</summary>");
Array(text, "public static ReadOnlySpan<int> OffsetsByLength", offsets, 8);
text.AppendLine("}");
Write("StaticDictionary.Data.cs", text);

// The context lookup table (RFC 7932, section 7.1), the four modes' halves for p1 and p2.
long[] context = Numbers(File.ReadAllText(Path.Combine(source, "common", "context.c")), "_kBrotliContextLookupTable[2048] =");
Check(context.Length == 2048, "the context table has 2048 entries");
text.Clear();
Header(text, "c/common/context.c");
text.AppendLine("internal static partial class Context");
text.AppendLine("{");
text.AppendLine("    /// <summary>");
text.AppendLine("    /// The context of a literal from the two bytes before it: for each mode (LSB6, MSB6, UTF8, signed),");
text.AppendLine("    /// 256 entries for the last byte, then 256 for the one before it, whose values are OR-ed.");
text.AppendLine("    /// </summary>");
Array(text, "public static ReadOnlySpan<byte> Lookup", context, 16);
text.AppendLine("}");
Write("Context.Data.cs", text);

// The 121 word transforms (RFC 7932, appendix B): the prefixes and suffixes, then each transform's
// triplet of prefix, type and suffix.
string transformC = File.ReadAllText(Path.Combine(source, "common", "transform.c"));
byte[] prefixSuffix = CString(transformC, "kPrefixSuffix[217] =");
long[] prefixSuffixMap = Numbers(transformC, "kPrefixSuffixMap[50] =");
string[] typeNames =
[
    "IDENTITY", "OMIT_LAST_1", "OMIT_LAST_2", "OMIT_LAST_3", "OMIT_LAST_4", "OMIT_LAST_5", "OMIT_LAST_6",
    "OMIT_LAST_7", "OMIT_LAST_8", "OMIT_LAST_9", "UPPERCASE_FIRST", "UPPERCASE_ALL", "OMIT_FIRST_1",
    "OMIT_FIRST_2", "OMIT_FIRST_3", "OMIT_FIRST_4", "OMIT_FIRST_5", "OMIT_FIRST_6", "OMIT_FIRST_7",
    "OMIT_FIRST_8", "OMIT_FIRST_9", "SHIFT_FIRST", "SHIFT_ALL",
];
string transformsBody = Body(transformC, "kTransformsData[] =");
for (int t = 0; t < typeNames.Length; t++)
{
    transformsBody = Regex.Replace(transformsBody, @"\bBROTLI_TRANSFORM_" + typeNames[t] + @"\b", t.ToString(CultureInfo.InvariantCulture));
}

long[] transforms = Numbers(transformsBody, "{");
Check(prefixSuffix.Length == 217, "the prefixes and suffixes take 217 bytes");
Check(prefixSuffixMap.Length == 50, "50 prefixes and suffixes");
Check(transforms.Length == 121 * 3, "121 transforms");
text.Clear();
Header(text, "c/common/transform.c");
text.AppendLine("internal static partial class Transforms");
text.AppendLine("{");
text.AppendLine("    /// <summary>The prefixes and suffixes, each its length then its bytes; the last one is empty.</summary>");
Array(text, "public static ReadOnlySpan<byte> PrefixSuffix", prefixSuffix.Select(b => (long)b).ToArray(), 24);
text.AppendLine();
text.AppendLine("    /// <summary>Where each prefix or suffix starts in <see cref=\"PrefixSuffix\"/>.</summary>");
Array(text, "public static ReadOnlySpan<byte> PrefixSuffixMap", prefixSuffixMap, 16);
text.AppendLine();
text.AppendLine("    /// <summary>Each transform's prefix index, type and suffix index.</summary>");
Array(text, "public static ReadOnlySpan<byte> Triplets", transforms, 18);
text.AppendLine("}");
Write("Transforms.Data.cs", text);

// The insert-and-copy command codes (RFC 7932, section 5), as the decoder's lookup table.
long[] commands = Numbers(File.ReadAllText(Path.Combine(source, "dec", "prefix.h")), "kCmdLut[BROTLI_NUM_COMMAND_SYMBOLS] =");
Check(commands.Length == 704 * 6, "704 command codes");
text.Clear();
Header(text, "c/dec/prefix.h");
text.AppendLine("internal static partial class Prefix");
text.AppendLine("{");
text.AppendLine("    /// <summary>");
text.AppendLine("    /// Each of the 704 insert-and-copy codes, as six values: the insert length's extra bits, the copy");
text.AppendLine("    /// length's extra bits, the distance code (0: the last distance, implicitly; -1: read one), the");
text.AppendLine("    /// distance context, the insert length's offset and the copy length's offset.");
text.AppendLine("    /// </summary>");
Array(text, "public static ReadOnlySpan<short> CommandCodes", commands, 18);
text.AppendLine("}");
Write("Prefix.Data.cs", text);

Console.WriteLine("done");
return 0;

// The body of the initializer after `marker`, braces included.
static string Body(string c, string marker)
{
    int at = c.IndexOf(marker, StringComparison.Ordinal);
    Check(at >= 0, "found " + marker);
    int open = c.IndexOf('{', at);
    int close = c.IndexOf("};", open, StringComparison.Ordinal);
    return c.Substring(open, close - open + 1);
}

// The bytes of the concatenated string literals after `marker`, up to the semicolon, with C's escapes
// and the implicit trailing zero.
static byte[] CString(string c, string marker)
{
    int at = c.IndexOf(marker, StringComparison.Ordinal);
    Check(at >= 0, "found " + marker);
    int end = c.IndexOf(';', at);
    string literals = Regex.Replace(c.Substring(at + marker.Length, end - at - marker.Length), @"/\*.*?\*/", " ", RegexOptions.Singleline);
    var bytes = new List<byte>();
    foreach (Match m in Regex.Matches(literals, "\"((?:[^\"\\\\]|\\\\.)*)\""))
    {
        string s = m.Groups[1].Value;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\')
            {
                bytes.Add(checked((byte)s[i]));
                continue;
            }

            char e = s[++i];
            if (e is >= '0' and <= '7')
            {
                int value = 0;
                int digits = 0;
                while (i < s.Length && digits < 3 && s[i] is >= '0' and <= '7')
                {
                    value = (value * 8) + (s[i++] - '0');
                    digits++;
                }

                i--;
                bytes.Add(checked((byte)value));
            }
            else if (e == 'x')
            {
                bytes.Add(byte.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                bytes.Add(e switch { 'n' => (byte)'\n', 't' => (byte)'\t', 'r' => (byte)'\r', '0' => 0, _ => checked((byte)e) });
            }
        }
    }

    bytes.Add(0);
    return bytes.ToArray();
}

static void Header(StringBuilder text, string file)
{
    text.AppendLine("// <auto-generated>");
    text.AppendLine($"// Written by tools/brotli/generate-tables.cs from brotli 1.1.1's {file}. Do not edit:");
    text.AppendLine("// run the generator again. The tables are brotli's, under its MIT license (see NOTICE).");
    text.AppendLine("// </auto-generated>");
    text.AppendLine();
    text.AppendLine("using System;");
    text.AppendLine();
    text.AppendLine("namespace ManagedCompressions.Brotli.Internal;");
    text.AppendLine();
}

static void Array(StringBuilder text, string declaration, long[] values, int perLine)
{
    text.AppendLine($"    {declaration} =>");
    text.AppendLine("    [");
    for (int i = 0; i < values.Length; i += perLine)
    {
        text.Append("        ");
        int end = Math.Min(values.Length, i + perLine);
        for (int j = i; j < end; j++)
        {
            text.Append(values[j].ToString(CultureInfo.InvariantCulture)).Append(',');
            if (j + 1 < end)
            {
                text.Append(' ');
            }
        }

        text.AppendLine();
    }

    text.AppendLine("    ];");
}

// The numbers of the first brace-delimited initializer after `marker`, nested braces flattened,
// comments removed.
static long[] Numbers(string c, string marker)
{
    int at = c.IndexOf(marker, StringComparison.Ordinal);
    Check(at >= 0, "found " + marker);
    int open = c.IndexOf('{', at);
    int depth = 0;
    int end = open;
    for (; end < c.Length; end++)
    {
        if (c[end] == '{')
        {
            depth++;
        }
        else if (c[end] == '}' && --depth == 0)
        {
            break;
        }
    }

    string body = Regex.Replace(c.Substring(open, end - open + 1), @"/\*.*?\*/", " ", RegexOptions.Singleline);
    var numbers = new List<long>();
    foreach (Match m in Regex.Matches(body, @"-?(0[xX][0-9a-fA-F]+|\d+)[uUlL]*"))
    {
        string token = m.Value.TrimEnd('u', 'U', 'l', 'L');
        bool negative = token.StartsWith('-');
        token = token.TrimStart('-');
        long value = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(token, CultureInfo.InvariantCulture);
        numbers.Add(negative ? -value : value);
    }

    return numbers.ToArray();
}

void Write(string name, StringBuilder text)
{
    string path = Path.Combine(output, name);
    File.WriteAllText(path, text.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
    Console.WriteLine("wrote " + path);
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException("expected: " + what);
    }
}

static string RepositoryRoot([CallerFilePath] string here = "")
{
    string? directory = Path.GetDirectoryName(here);
    while (directory is not null && !File.Exists(Path.Combine(directory, "ManagedCompressions.slnx")))
    {
        directory = Path.GetDirectoryName(directory);
    }

    return directory ?? throw new InvalidOperationException("ManagedCompressions.slnx not found");
}
