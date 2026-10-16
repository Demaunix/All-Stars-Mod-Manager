using System.Text;

namespace AllStarsModManager;

public sealed class PlanFormatException(string message) : Exception(message);

public enum JKind { Null, String, Number, Bool, Object, Array }

/// <summary>JSON value as the loader's plan parser sees it: unsigned integers only, no duplicate keys.</summary>
public sealed class JVal
{
    public JKind Kind;
    public string S = "";
    public ulong N;
    public bool B;
    public Dictionary<string, JVal> O = new(StringComparer.Ordinal);
    public List<JVal> A = new();

    public bool Has(string k) => Kind == JKind.Object && O.ContainsKey(k);
    public JVal At(string k) => Kind == JKind.Object && O.TryGetValue(k, out var v) ? v : throw new PlanFormatException($"Missing plan field: {k}");
    public string Str() => Kind == JKind.String ? S : throw new PlanFormatException("Expected a string");
    public uint U32() => Kind == JKind.Number && N <= uint.MaxValue ? (uint)N : throw new PlanFormatException("Expected an unsigned 32-bit number");
    public List<JVal> List() => Kind == JKind.Array ? A : throw new PlanFormatException("Expected a list");
}

/// <summary>
/// Port of the loader's plan parser (json.h). It is deliberately strict: a plan the loader refuses must not look
/// fine here. Notably a UTF-8 byte-order mark, comments, negative or fractional numbers, duplicate keys,
/// trailing commas and nesting deeper than 32 are all rejected.
/// </summary>
public sealed class StrictJson
{
    readonly byte[] _s;
    int _p;
    static readonly UTF8Encoding Utf8Strict = new(false, true);

    StrictJson(byte[] bytes) { _s = bytes; }

    public static JVal Parse(byte[] bytes)
    {
        var p = new StrictJson(bytes);
        var v = p.Value(0);
        p.Ws();
        if (p._p != p._s.Length) p.Bad();
        return v;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    void Bad() => throw new PlanFormatException($"Invalid JSON plan at byte {_p}");

    void Ws() { while (_p < _s.Length && (_s[_p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) _p++; }

    int Hex()
    {
        if (_p >= _s.Length) Bad();
        char c = (char)_s[_p++];
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        Bad();
        return 0;
    }

    string ReadString()
    {
        if (_p >= _s.Length || _s[_p++] != '"') Bad();
        var raw = new List<byte>();
        while (_p < _s.Length)
        {
            byte c = _s[_p++];
            if (c == '"')
            {
                try { return Utf8Strict.GetString(raw.ToArray()); }
                catch (DecoderFallbackException) { throw new PlanFormatException($"Invalid UTF-8 in a plan string before byte {_p}"); }
            }
            if (c < 32) Bad();
            if (c != '\\') { raw.Add(c); continue; }
            if (_p >= _s.Length) Bad();
            c = _s[_p++];
            switch ((char)c)
            {
                case '"': case '\\': case '/': raw.Add(c); break;
                case 'b': raw.Add(8); break;
                case 'f': raw.Add(12); break;
                case 'n': raw.Add(10); break;
                case 'r': raw.Add(13); break;
                case 't': raw.Add(9); break;
                case 'u':
                    {
                        int u = 0;
                        for (int i = 0; i < 4; i++) u = u * 16 + Hex();
                        if (u >= 0xD800 && u <= 0xDFFF) Bad();
                        raw.AddRange(Encoding.UTF8.GetBytes(((char)u).ToString()));
                        break;
                    }
                default: Bad(); break;
            }
        }
        Bad();
        return "";
    }

    JVal Value(int depth)
    {
        if (depth > 32) Bad();
        Ws();
        if (_p >= _s.Length) Bad();
        var j = new JVal();
        char c = (char)_s[_p];
        if (c == '"') { j.Kind = JKind.String; j.S = ReadString(); return j; }
        if (c == '{' || c == '[')
        {
            bool obj = c == '{';
            _p++;
            j.Kind = obj ? JKind.Object : JKind.Array;
            Ws();
            if (_p < _s.Length && _s[_p] == (obj ? '}' : ']')) { _p++; return j; }
            for (; ; )
            {
                Ws();
                if (obj)
                {
                    var k = ReadString();
                    Ws();
                    if (_p >= _s.Length || _s[_p++] != ':') Bad();
                    if (!j.O.TryAdd(k, Value(depth + 1))) Bad();
                }
                else j.A.Add(Value(depth + 1));
                Ws();
                if (_p >= _s.Length) Bad();
                char t = (char)_s[_p++];
                if (t == (obj ? '}' : ']')) return j;
                if (t != ',') Bad();
            }
        }
        if (c >= '0' && c <= '9')
        {
            j.Kind = JKind.Number;
            int begin = _p;
            while (_p < _s.Length && _s[_p] >= '0' && _s[_p] <= '9')
            {
                ulong d = (ulong)(_s[_p++] - '0');
                if (j.N > (ulong.MaxValue - d) / 10) Bad();
                j.N = j.N * 10 + d;
            }
            if (_p - begin > 1 && _s[begin] == '0') Bad();
            return j;
        }
        if (Match("null")) { return j; }
        if (Match("true")) { j.Kind = JKind.Bool; j.B = true; return j; }
        if (Match("false")) { j.Kind = JKind.Bool; return j; }
        Bad();
        return j;
    }

    bool Match(string word)
    {
        if (_p + word.Length > _s.Length) return false;
        for (int i = 0; i < word.Length; i++) if (_s[_p + i] != word[i]) return false;
        _p += word.Length;
        return true;
    }
}
