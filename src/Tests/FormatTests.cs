using System.Text;

namespace AllStarsModManager.Tests;

public static class FormatTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public static void Register()
    {
        T.Area("formats");

        T.Run("StrictJson accepts the loader's grammar", () =>
        {
            var v = StrictJson.Parse(B("{\"schema\":1,\"archive_groups\":[{\"k\":\"\\u00e9\\n\",\"n\":4294967295,\"t\":true,\"f\":false,\"z\":null}]}"));
            T.Eq(1u, v.At("schema").U32(), "schema");
            T.Eq("é\n", v.At("archive_groups").List()[0].At("k").Str(), "unicode escape");
            T.Eq(uint.MaxValue, v.At("archive_groups").List()[0].At("n").U32(), "u32 max");
        });

        foreach (var (name, text) in new (string, string)[]
        {
            ("duplicate keys", "{\"a\":1,\"a\":2}"),
            ("trailing comma object", "{\"a\":1,}"),
            ("trailing comma array", "[1,]"),
            ("negative number", "{\"a\":-1}"),
            ("fractional number", "{\"a\":1.5}"),
            ("exponent number", "{\"a\":1e3}"),
            ("leading zero", "{\"a\":01}"),
            ("comment", "{\"a\":1 /*x*/}"),
            ("single quotes", "{'a':1}"),
            ("lone surrogate escape", "{\"a\":\"\\ud800\"}"),
            ("raw control char in string", "{\"a\":\"x\ty\"}"),
            ("unterminated", "{\"a\":1"),
            ("trailing garbage", "{\"a\":1} x"),
            ("number overflow", "{\"a\":18446744073709551616}"),
            ("bad escape", "{\"a\":\"\\q\"}"),
        })
            T.Run($"StrictJson rejects {name}", () => T.Throws<PlanFormatException>(() => StrictJson.Parse(B(text)), name));

        T.Run("StrictJson rejects nesting deeper than 32 and accepts 32", () =>
        {
            string Nest(int d) => new string('[', d) + new string(']', d);
            StrictJson.Parse(B(Nest(33)));   // depth counts from 0 at the top value: 33 brackets = depth index 32
            T.Throws<PlanFormatException>(() => StrictJson.Parse(B(Nest(34))), "34 levels");
        });

        T.Run("StrictJson rejects a UTF-8 byte-order mark and invalid UTF-8", () =>
        {
            T.Throws<PlanFormatException>(() => StrictJson.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(B("{}")).ToArray()), "BOM");
            T.Throws<PlanFormatException>(() => StrictJson.Parse(new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' }), "invalid utf8");
        });

        foreach (var ok in new[] { "Resource/Base.xpac", "Resource\\Base.xpac", "a/b/c", "a\\", "Scripts/x y.sbn" })
            T.Run($"LoaderPath.Relative accepts '{ok}'", () => LoaderPath.Relative(ok));
        foreach (var bad in new[] { "", "/abs", "\\abs", "C:\\x", "a:b", "a/../b", "..", ".", "a//b", "a/./b", "a.", "a /b", "a/b ", "x*y", "x?y", "x\"y", "x<y", "x>y", "x|y" })
            T.Run($"LoaderPath.Relative rejects '{bad}'", () => T.Throws<PathRuleException>(() => LoaderPath.Relative(bad), bad));

        T.Run("LoaderPath folds case so differently-written targets are the same key", () =>
        {
            T.Eq(LoaderPath.Fold(LoaderPath.Relative("Resource/BASE.xpac")), LoaderPath.Fold(LoaderPath.Relative("resource\\base.XPAC")), "fold");
        });

        T.Run("LoaderPath.SafeFolderName never yields reserved or hidden names", () =>
        {
            T.Eq("_CON", LoaderPath.SafeFolderName("CON"), "device");
            T.Eq("_nul.txt", LoaderPath.SafeFolderName("nul.txt"), "device with ext");
            T.Eq("x_y", LoaderPath.SafeFolderName("x:y"), "colon");
            T.False(LoaderPath.SafeFolderName("...").StartsWith('.'), "dots");
            T.True(LoaderPath.SafeFolderName("   ").Length > 0, "blank falls back");
            T.False(LoaderPath.SafeFolderName("a.").EndsWith('.'), "trailing dot");
            T.Eq("_hidden", LoaderPath.SafeFolderName(".hidden"), "leading dot");
        });

        T.Run("VersionSpec compares like semantic versions", () =>
        {
            T.Eq(-1, VersionSpec.Compare("0.1.0-alpha.6", "0.1.0-alpha.7")!.Value, "alpha 6<7");
            T.Eq(-1, VersionSpec.Compare("0.1.0-alpha.7", "0.1.0")!.Value, "pre < release");
            T.Eq(1, VersionSpec.Compare("1.10.0", "1.9.9")!.Value, "numeric not text");
            T.Eq(0, VersionSpec.Compare("v1.0", "1.0.0")!.Value, "v prefix, padded");
            T.True(VersionSpec.Compare("banana", "1.0") is null, "unparseable is unknown");
            T.Eq<bool?>(true, VersionSpec.Satisfies("0.1.0-alpha.7", ">=0.1.0-alpha.7"), ">=");
            T.Eq<bool?>(false, VersionSpec.Satisfies("0.1.0-alpha.6", ">=0.1.0-alpha.7"), "<");
            T.Eq<bool?>(true, VersionSpec.Satisfies("0.1.0-alpha.6", "<=0.1.0-alpha.6"), "<=");
            T.Eq<bool?>(null, VersionSpec.Satisfies("1.0", "UNKNOWN"), "unknown spec");
            T.Eq<bool?>(null, VersionSpec.Satisfies("odd", ">=1.0"), "unknown version");
            T.Eq<bool?>(true, VersionSpec.Satisfies("2.0", ">=1.0 <3.0"), "range");
            T.Eq<bool?>(false, VersionSpec.Satisfies("3.0", ">=1.0 <3.0"), "range outside");
        });

        T.Run("IniDocument round-trips every byte it does not edit", () =>
        {
            foreach (var bytes in new[]
            {
                B("[A]\r\nx=1\r\n; comment\r\n\r\n[B]\r\ny = two \r\n"),
                B("[A]\nx=1\n[B]\ny=2"),
                B("[A]\rx=1\r"),
                B("\uFEFF[A]\r\nx=é\r\n"),
                new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[A]\r\nx=1\r\n")).ToArray(),
                new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("[A]\r\nx=1\r\n")).ToArray(),
                new byte[] { (byte)'[', (byte)'A', (byte)']', 13, 10, (byte)'x', (byte)'=', 0xE9, 13, 10 },   // legacy 8-bit
                B("x=1\r\n[Late]\r\nk=v\n"),                                                               // mixed endings, key before any section
                Array.Empty<byte>(),
                B("   \r\n=novalue\r\n#hash\r\n[]\r\n"),
            })
            {
                var doc = IniDocument.Load(bytes);
                T.True(doc.ToBytes().SequenceEqual(bytes), "round trip differs for " + BitConverter.ToString(bytes.Take(12).ToArray()));
            }
        });

        T.Run("IniDocument.Set changes only the edited line (value, line ending, neighbours preserved)", () =>
        {
            var orig = B("[Quick]\r\n; keep me\r\nEnabled = 0 \r\nOther=a\r\n\r\n[Next]\r\nZ=1");
            var d = IniDocument.Load(orig);
            d.Set("Quick", "Enabled", "1");
            var text = Encoding.UTF8.GetString(d.ToBytes());
            T.Eq("[Quick]\r\n; keep me\r\nEnabled = 1\r\nOther=a\r\n\r\n[Next]\r\nZ=1", text, "single-line edit");
        });

        T.Run("IniDocument.Set appends new keys inside their section with the file's own line ending", () =>
        {
            var d = IniDocument.Load(B("[A]\nx=1\n\n[B]\ny=2\n"));
            d.Set("A", "n", "5");
            d.Set("C", "z", "9");
            T.Eq("[A]\nx=1\nn=5\n\n[B]\ny=2\n[C]\nz=9\n", Encoding.UTF8.GetString(d.ToBytes()), "append");
        });

        T.Run("IniDocument keeps the byte-order mark and encoding when editing", () =>
        {
            var u16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[A]\r\nx=1\r\n")).ToArray();
            var d = IniDocument.Load(u16);
            d.Set("A", "x", "2");
            var outBytes = d.ToBytes();
            T.True(outBytes[0] == 0xFF && outBytes[1] == 0xFE, "BOM kept");
            T.Eq("[A]\r\nx=2\r\n", Encoding.Unicode.GetString(outBytes, 2, outBytes.Length - 2), "utf16 body");
            var u8 = IniDocument.Load(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(B("[A]\r\nx=1\r\n")).ToArray());
            u8.Set("A", "x", "é");
            T.True(u8.ToBytes().Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "utf8 BOM kept");
        });

        T.Run("IniDocument refuses special characters in files without a byte-order mark", () =>
        {
            var d = IniDocument.Load(B("[A]\r\nx=1\r\n"));
            T.Throws<InvalidDataException>(() => d.Set("A", "x", "é"), "non-ascii in plain file");
            T.Throws<ArgumentException>(() => d.Set("A", "x", "a\nb"), "line break in value");
        });

        T.Run("LoaderIni reads mod fields like the loader (quotes, spaces, UTF-16 files, overlong values)", () =>
        {
            var dir = Path.Combine(Program.WorkRoot, "fixtures", "ini-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var p1 = Path.Combine(dir, "a.ini");
                File.WriteAllText(p1, "; c\r\n[MOD]\r\nID =  spaced-id  \r\nschema=\"1\"\r\nname=\r\n", new UTF8Encoding(false));
                T.Eq("spaced-id", LoaderIni.ReadModField(p1, "id").Value, "section/keys are case-insensitive, value trimmed");
                T.Eq("1", LoaderIni.ReadModField(p1, "schema").Value, "quoted value unquoted by the Win32 reader");
                T.False(LoaderIni.ReadModField(p1, "name").Ok, "empty value is 'missing' for the loader");
                T.False(LoaderIni.ReadModField(p1, "absent").Ok, "absent field");
                var p2 = Path.Combine(dir, "b.ini");
                File.WriteAllBytes(p2, new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[mod]\r\nid=wide-é\r\n")).ToArray());
                T.Eq("wide-é", LoaderIni.ReadModField(p2, "id").Value, "UTF-16 file");
                var p3 = Path.Combine(dir, "c.ini");
                File.WriteAllText(p3, "[mod]\r\nid=" + new string('x', 2100) + "\r\nok=" + new string('y', 2040) + "\r\n");
                T.False(LoaderIni.ReadModField(p3, "id").Ok, "value longer than the loader's buffer is rejected");
                T.True(LoaderIni.ReadModField(p3, "ok").Ok, "value just under the limit is accepted");
            }
            finally { Fs.DeleteTree(dir); }
        });

        T.Run("Hash pins must be lowercase 64-digit hex", () =>
        {
            T.True(Sha.IsLoaderHash(new string('a', 64)), "lower ok");
            T.False(Sha.IsLoaderHash(new string('A', 64)), "upper rejected like the loader");
            T.False(Sha.IsLoaderHash(new string('a', 63)), "short");
            T.False(Sha.IsLoaderHash(new string('g', 64)), "non-hex");
        });
    }
}
