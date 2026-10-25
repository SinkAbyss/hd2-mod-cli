using System;
using System.Collections.Generic;
using System.Text;

namespace Hd2Mod
{
    /// <summary>Whether a script compiles on the game's Lua: plain PUC Lua 5.1 since the
    /// 2026-10-20 update, no LuaJIT (the user, 2026-10-26: say before a deploy which mods the
    /// game cannot run). The lexer and the parser follow PUC Lua 5.1's own llex.c and lparser.c,
    /// rule for rule, so a script is refused here exactly where the game's loadstring refuses
    /// its syntax: goto and labels, LuaJIT's 64-bit numbers (0x...ull), the 5.2+ operators, a
    /// call split from its function by a line break, nested [[ ]], and the rest. Compared with
    /// PUC 5.1.5 itself on the scripts of the user's library and on thousands of altered
    /// scripts (tests/lua_syntax_vs_puc.py). The parser's own limits are checked as 5.1 checks
    /// them (200 local variables and 60 upvalues a function, 200 levels of nesting); the code
    /// generator's (250 registers: "function or expression too complex") are not, and the
    /// game's own log tells those after a launch.</summary>
    public static class LuaSyntax
    {
        /// <summary>Null when the script compiles, else where and why it does not. The bytes as
        /// the game's loadstring gets them: a UTF-8 BOM is not skipped, and refused.</summary>
        public static (int Line, string Message)? Check(byte[] source)
        {
            try
            {
                new Parser(new Lexer(source)).Chunk();
                return null;
            }
            catch (LuaError e) { return (e.Line, e.Message); }
        }

        public static (int Line, string Message)? Check(string source) => Check(Encoding.UTF8.GetBytes(source ?? ""));

        /// <summary>The script's tokens, comments and strings resolved (for the LuaJIT checks);
        /// null when it does not get through the lexer.</summary>
        public static List<Token> Tokens(byte[] source)
        {
            var lx = new Lexer(source);
            var out_ = new List<Token>();
            try
            {
                for (;;)
                {
                    var t = lx.Lex();
                    out_.Add(t);
                    if (t.Kind == T.Eos) return out_;
                }
            }
            catch (LuaError) { return null; }
        }

        /// <summary>Text the lexer kept one char per byte, as the UTF-8 it was.</summary>
        static string Readable(string bytes)
        {
            var b = new byte[bytes.Length];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)bytes[i];
            return Encoding.UTF8.GetString(b);
        }

        internal sealed class LuaError : Exception
        {
            public readonly int Line;
            public LuaError(int line, string message) : base(message) { Line = line; }
        }

        // token kinds: a single character is its own code; the rest follow it
        public static class T
        {
            public const int And = 257, Break = 258, Do = 259, Else = 260, Elseif = 261, End = 262, False = 263, For = 264,
                Function = 265, If = 266, In = 267, Local = 268, Nil = 269, Not = 270, Or = 271, Repeat = 272, Return = 273,
                Then = 274, True = 275, Until = 276, While = 277, Concat = 278, Dots = 279, Eq = 280, Ge = 281, Le = 282, Ne = 283,
                Number = 284, Name = 285, String = 286, Eos = 287;
        }

        static readonly string[] Reserved =
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or",
            "repeat", "return", "then", "true", "until", "while",
        };

        static readonly Dictionary<string, int> ReservedKind = MakeReserved();

        static Dictionary<string, int> MakeReserved()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Reserved.Length; i++) d[Reserved[i]] = T.And + i;
            return d;
        }

        static string KindText(int k)
        {
            if (k < 257) return ((char)k).ToString();
            if (k <= T.While) return Reserved[k - T.And];
            switch (k)
            {
                case T.Concat: return "..";
                case T.Dots: return "...";
                case T.Eq: return "==";
                case T.Ge: return ">=";
                case T.Le: return "<=";
                case T.Ne: return "~=";
                case T.Number: return "<number>";
                case T.Name: return "<name>";
                case T.String: return "<string>";
                default: return "<eof>";
            }
        }

        public struct Token
        {
            public int Kind, Line;
            /// <summary>A name's text, a string's value, a number as written.</summary>
            public string Text;
        }

        // ------------------------------------------------------------ the lexer (llex.c)

        sealed class Lexer
        {
            readonly string s;          // the source, one char per byte (Latin-1), as C reads it
            int p;
            public int Line = 1;        // the line the lexer is on (ls->linenumber)
            public int LastLine = 1;    // the line of the last token taken (ls->lastline)
            public Token Cur;
            Token ahead;
            bool hasAhead;
            readonly StringBuilder buf = new StringBuilder();

            public Lexer(byte[] bytes)
            {
                var sb = new StringBuilder(bytes.Length);
                foreach (var b in bytes) sb.Append((char)b);
                s = sb.ToString();
            }

            int C => p < s.Length ? s[p] : -1;
            void Adv() => p++;
            void Save(int c) => buf.Append((char)c);
            void SaveAdv() { Save(C); Adv(); }
            bool IsNewline => C == '\n' || C == '\r';

            static bool IsDigit(int c) => c >= '0' && c <= '9';
            static bool IsAlpha(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            static bool IsAlnum(int c) => IsAlpha(c) || IsDigit(c);
            static bool IsSpace(int c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';

            public LuaError Error(string msg, string near) => new LuaError(Line, near != null ? msg + " near '" + Readable(near) + "'" : msg);

            /// <summary>luaX_next.</summary>
            public void Next()
            {
                LastLine = Line;
                if (hasAhead) { Cur = ahead; hasAhead = false; }
                else Cur = Lex();
            }

            /// <summary>luaX_lookahead: the lexer moves on, so Line moves with it.</summary>
            public Token Ahead()
            {
                ahead = Lex();
                hasAhead = true;
                return ahead;
            }

            void IncLine()
            {
                int old = C;
                Adv();
                if (IsNewline && C != old) Adv();      // \n\r or \r\n
                Line++;
            }

            int SkipSep()
            {
                int count = 0, open = C;
                SaveAdv();
                while (C == '=') { SaveAdv(); count++; }
                return C == open ? count : -count - 1;
            }

            void ReadLong(bool isString, int sep)
            {
                SaveAdv();                             // the second [
                if (IsNewline) IncLine();              // a newline right after it is not part of it
                for (;;)
                {
                    switch (C)
                    {
                        case -1:
                            throw Error(isString ? "unfinished long string" : "unfinished long comment", "<eof>");
                        case '[':
                            if (SkipSep() == sep)
                            {
                                SaveAdv();
                                // LUA_COMPAT_LSTR == 1, Lua 5.1's default
                                if (sep == 0) throw Error("nesting of [[...]] is deprecated", "[");
                            }
                            break;
                        case ']':
                            if (SkipSep() == sep) { SaveAdv(); return; }
                            break;
                        case '\n':
                        case '\r':
                            Save('\n');
                            IncLine();
                            break;
                        default:
                            SaveAdv();
                            break;
                    }
                }
            }

            string ReadString(int del)
            {
                var value = new StringBuilder();
                SaveAdv();
                while (C != del)
                {
                    switch (C)
                    {
                        case -1:
                            throw Error("unfinished string", "<eof>");
                        case '\n':
                        case '\r':
                            throw Error("unfinished string", buf.ToString());
                        case '\\':
                            Adv();
                            switch (C)
                            {
                                case 'a': value.Append('\a'); Adv(); continue;
                                case 'b': value.Append('\b'); Adv(); continue;
                                case 'f': value.Append('\f'); Adv(); continue;
                                case 'n': value.Append('\n'); Adv(); continue;
                                case 'r': value.Append('\r'); Adv(); continue;
                                case 't': value.Append('\t'); Adv(); continue;
                                case 'v': value.Append('\v'); Adv(); continue;
                                case '\n':
                                case '\r':
                                    value.Append('\n'); Save('\n'); IncLine(); continue;
                                case -1:
                                    continue;                  // "unfinished string" on the next turn
                                default:
                                    if (!IsDigit(C)) { value.Append((char)C); SaveAdv(); continue; }
                                    int n = 0, i = 0;
                                    do { n = 10 * n + (C - '0'); Adv(); } while (++i < 3 && IsDigit(C));
                                    if (n > 255) throw Error("escape sequence too large", buf.ToString());
                                    value.Append((char)n);
                                    continue;
                            }
                        default:
                            value.Append((char)C);
                            SaveAdv();
                            break;
                    }
                }
                SaveAdv();
                return value.ToString();
            }

            void ReadNumeral()
            {
                do SaveAdv(); while (IsDigit(C) || C == '.');
                if (C == 'E' || C == 'e')
                {
                    SaveAdv();
                    if (C == '+' || C == '-') SaveAdv();
                }
                while (IsAlnum(C) || C == '_') SaveAdv();
                if (!NumberOk(buf.ToString())) throw Error("malformed number", buf.ToString());
            }

            /// <summary>luaO_str2d: strtod, then strtoul in base 16 when it stopped at an x;
            /// nothing may follow. strtod as the Universal CRT has it (hexadecimal too).</summary>
            static bool NumberOk(string t)
            {
                int i = 0, n = t.Length;
                if (n > 1 && t[0] == '0' && (t[1] == 'x' || t[1] == 'X'))
                {
                    i = 2;
                    int digits = 0;
                    while (i < n && Uri.IsHexDigit(t[i])) { i++; digits++; }
                    if (i < n && t[i] == '.') { i++; while (i < n && Uri.IsHexDigit(t[i])) { i++; digits++; } }
                    if (digits == 0) return false;
                    if (i < n && (t[i] == 'p' || t[i] == 'P'))
                    {
                        int j = i + 1;
                        if (j < n && (t[j] == '+' || t[j] == '-')) j++;
                        int e = j;
                        while (j < n && IsDigit(t[j])) j++;
                        if (j > e) i = j;
                    }
                    return i == n;
                }
                int m = 0;
                while (i < n && IsDigit(t[i])) { i++; m++; }
                if (i < n && t[i] == '.') { i++; while (i < n && IsDigit(t[i])) { i++; m++; } }
                if (m == 0) return false;
                if (i < n && (t[i] == 'e' || t[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < n && (t[j] == '+' || t[j] == '-')) j++;
                    int e = j;
                    while (j < n && IsDigit(t[j])) j++;
                    if (j > e) i = j;
                }
                return i == n;
            }

            public Token Lex()
            {
                buf.Clear();
                for (;;)
                {
                    int line = Line;
                    switch (C)
                    {
                        case '\n':
                        case '\r':
                            IncLine();
                            continue;
                        case '-':
                            Adv();
                            if (C != '-') return Tok('-', line);
                            Adv();
                            if (C == '[')
                            {
                                int sep = SkipSep();
                                buf.Clear();
                                if (sep >= 0)
                                {
                                    ReadLong(false, sep);
                                    buf.Clear();
                                    continue;
                                }
                            }
                            while (!IsNewline && C != -1) Adv();
                            buf.Clear();
                            continue;
                        case '[':
                            {
                                int sep = SkipSep();
                                if (sep >= 0)
                                {
                                    ReadLong(true, sep);
                                    var text = buf.ToString();
                                    int cut = 2 + sep;
                                    var value = text.Length >= 2 * cut ? text.Substring(cut, text.Length - 2 * cut) : "";
                                    if (value.StartsWith("\n")) value = value.Substring(1);
                                    return new Token { Kind = T.String, Line = line, Text = value };
                                }
                                if (sep == -1) return Tok('[', line);
                                throw Error("invalid long string delimiter", buf.ToString());
                            }
                        case '=':
                            Adv();
                            if (C != '=') return Tok('=', line);
                            Adv();
                            return Tok(T.Eq, line);
                        case '<':
                            Adv();
                            if (C != '=') return Tok('<', line);
                            Adv();
                            return Tok(T.Le, line);
                        case '>':
                            Adv();
                            if (C != '=') return Tok('>', line);
                            Adv();
                            return Tok(T.Ge, line);
                        case '~':
                            Adv();
                            if (C != '=') return Tok('~', line);
                            Adv();
                            return Tok(T.Ne, line);
                        case '"':
                        case '\'':
                            {
                                var value = ReadString(C);
                                return new Token { Kind = T.String, Line = line, Text = value };
                            }
                        case '.':
                            SaveAdv();
                            if (C == '.')
                            {
                                Adv();
                                if (C == '.') { Adv(); return Tok(T.Dots, line); }
                                return Tok(T.Concat, line);
                            }
                            if (!IsDigit(C)) return Tok('.', line);
                            ReadNumeral();
                            return new Token { Kind = T.Number, Line = line, Text = buf.ToString() };
                        case -1:
                            return Tok(T.Eos, line);
                        default:
                            if (IsSpace(C)) { Adv(); continue; }
                            if (IsDigit(C))
                            {
                                ReadNumeral();
                                return new Token { Kind = T.Number, Line = line, Text = buf.ToString() };
                            }
                            if (IsAlpha(C) || C == '_')
                            {
                                do SaveAdv(); while (IsAlnum(C) || C == '_');
                                var word = buf.ToString();
                                if (ReservedKind.TryGetValue(word, out var k)) return new Token { Kind = k, Line = line, Text = word };
                                return new Token { Kind = T.Name, Line = line, Text = word };
                            }
                            {
                                int c = C;
                                Adv();
                                return new Token { Kind = c, Line = line, Text = ((char)c).ToString() };
                            }
                    }
                }
            }

            static Token Tok(int kind, int line) => new Token { Kind = kind, Line = line, Text = KindText(kind) };
        }

        // ------------------------------------------------------------ the parser (lparser.c)

        enum E { Other, Var, Call }

        sealed class Var { public string Name; }

        sealed class Func
        {
            public Func Parent;
            public bool Vararg;
            public int Loops;           // breakable blocks open in this function
            public int Line;            // where it was defined; 0: the main function
            public readonly List<Var> Actives = new List<Var>();
            public readonly List<Var> Upvalues = new List<Var>();
        }

        const int MaxVars = 200, MaxUpvalues = 60;   // LUAI_MAXVARS, LUAI_MAXUPVALUES

        sealed class Parser
        {
            readonly Lexer lx;
            readonly Stack<Func> funcs = new Stack<Func>();
            int level;

            public Parser(Lexer lexer) { lx = lexer; }

            Func F => funcs.Peek();
            int Tk => lx.Cur.Kind;

            LuaError Err(string msg)
            {
                var t = lx.Cur;
                string near = t.Kind == T.Eos ? "<eof>" : t.Kind == T.String || t.Kind == T.Name || t.Kind == T.Number ? t.Text : KindText(t.Kind);
                return new LuaError(lx.Line, msg + " near '" + Readable(near) + "'");
            }

            void Enter()
            {
                // LUAI_MAXCCALLS: the C calls already on the stack count too, a handful
                if (++level > 200) throw new LuaError(lx.Line, "chunk has too many syntax levels");
            }

            void Leave() => level--;

            bool TestNext(int k)
            {
                if (Tk != k) return false;
                lx.Next();
                return true;
            }

            void Check(int k)
            {
                if (Tk != k) throw Err("'" + KindText(k) + "' expected");
            }

            void CheckNext(int k)
            {
                Check(k);
                lx.Next();
            }

            void CheckMatch(int what, int who, int where)
            {
                if (TestNext(what)) return;
                if (where == lx.Line) throw Err("'" + KindText(what) + "' expected");
                throw Err("'" + KindText(what) + "' expected (to close '" + KindText(who) + "' at line " + where + ")");
            }

            void CheckName()
            {
                Check(T.Name);
                lx.Next();
            }

            static bool BlockFollow(int k) => k == T.Else || k == T.Elseif || k == T.End || k == T.Until || k == T.Eos;

            public void Chunk()
            {
                funcs.Push(new Func { Vararg = true });
                lx.Next();
                Statements();
                Check(T.Eos);
            }

            /// <summary>errorlimit: no "near".</summary>
            LuaError Limit(Func f, int limit, string what) =>
                new LuaError(lx.Line, (f.Line == 0 ? "main function" : "function at line " + f.Line) + " has more than " + limit + " " + what);

            /// <summary>new_localvar: the n-th name of this statement, not active yet.</summary>
            Var Declare(string name, int n)
            {
                if (F.Actives.Count + n + 1 > MaxVars) throw Limit(F, MaxVars, "local variables");
                return new Var { Name = name };
            }

            /// <summary>adjustlocalvars.</summary>
            void Activate(List<Var> vars) => F.Actives.AddRange(vars);

            string TakeName()
            {
                Check(T.Name);
                var name = lx.Cur.Text;
                lx.Next();
                return name;
            }

            /// <summary>singlevar: a name read as a local, an upvalue or a global.</summary>
            void UseName(string name) => Resolve(F, name, true);

            /// <summary>singlevaraux: the variable, or null for a global; an upvalue is counted in
            /// every function between the use and the local.</summary>
            Var Resolve(Func f, string name, bool here)
            {
                if (f == null) return null;
                for (int i = f.Actives.Count - 1; i >= 0; i--)
                    if (f.Actives[i].Name == name) return f.Actives[i];
                var v = Resolve(f.Parent, name, false);
                if (v == null) return null;
                if (!f.Upvalues.Contains(v))
                {
                    if (f.Upvalues.Count + 1 > MaxUpvalues) throw Limit(f, MaxUpvalues, "upvalues");
                    f.Upvalues.Add(v);
                }
                return v;
            }

            void Statements()
            {
                Enter();
                bool last = false;
                while (!last && !BlockFollow(Tk))
                {
                    last = Statement();
                    TestNext(';');
                }
                Leave();
            }

            void Block()
            {
                int mark = F.Actives.Count;
                Statements();
                F.Actives.RemoveRange(mark, F.Actives.Count - mark);
            }

            bool Statement()
            {
                int line = lx.Line;
                switch (Tk)
                {
                    case T.If: IfStat(line); return false;
                    case T.While:
                        lx.Next();
                        Expr();
                        F.Loops++;
                        CheckNext(T.Do);
                        Block();
                        CheckMatch(T.End, T.While, line);
                        F.Loops--;
                        return false;
                    case T.Do:
                        lx.Next();
                        Block();
                        CheckMatch(T.End, T.Do, line);
                        return false;
                    case T.For: ForStat(line); return false;
                    case T.Repeat:
                        {
                            F.Loops++;
                            int mark = F.Actives.Count;          // the condition sees the body's locals
                            lx.Next();
                            Statements();
                            CheckMatch(T.Until, T.Repeat, line);
                            Expr();
                            F.Actives.RemoveRange(mark, F.Actives.Count - mark);
                            F.Loops--;
                            return false;
                        }
                    case T.Function:
                        {
                            lx.Next();
                            UseName(TakeName());
                            bool self = false;
                            while (Tk == '.') { lx.Next(); CheckName(); }
                            if (Tk == ':') { lx.Next(); CheckName(); self = true; }
                            Body(line, self);
                            return false;
                        }
                    case T.Local:
                        lx.Next();
                        if (TestNext(T.Function))
                        {
                            var v = Declare(TakeName(), 0);
                            Activate(new List<Var> { v });         // seen by its own body
                            Body(lx.Line, false);
                        }
                        else
                        {
                            var vars = new List<Var>();
                            do vars.Add(Declare(TakeName(), vars.Count)); while (TestNext(','));
                            if (TestNext('=')) ExprList();
                            Activate(vars);
                        }
                        return false;
                    case T.Return:
                        lx.Next();
                        if (!BlockFollow(Tk) && Tk != ';') ExprList();
                        return true;
                    case T.Break:
                        lx.Next();
                        if (F.Loops == 0) throw Err("no loop to break");
                        return true;
                    default:
                        ExprStat();
                        return false;
                }
            }

            void IfStat(int line)
            {
                do
                {
                    lx.Next();
                    Expr();
                    CheckNext(T.Then);
                    Block();
                } while (Tk == T.Elseif);
                if (Tk == T.Else) { lx.Next(); Block(); }
                CheckMatch(T.End, T.If, line);
            }

            void ForStat(int line)
            {
                F.Loops++;
                int mark = F.Actives.Count;
                lx.Next();
                var first = TakeName();
                var hidden = new List<Var>();
                var names = new List<Var>();
                switch (Tk)
                {
                    case '=':
                        // (for index), (for limit), (for step), then the name
                        for (int i = 0; i < 3; i++) hidden.Add(Declare("(for)", i));
                        names.Add(Declare(first, 3));
                        lx.Next();
                        Expr();
                        CheckNext(',');
                        Expr();
                        if (TestNext(',')) Expr();
                        break;
                    case ',':
                    case T.In:
                        // (for generator), (for state), (for control), then the names
                        for (int i = 0; i < 3; i++) hidden.Add(Declare("(for)", i));
                        names.Add(Declare(first, 3));
                        while (TestNext(',')) names.Add(Declare(TakeName(), 3 + names.Count));
                        CheckNext(T.In);
                        ExprList();
                        break;
                    default:
                        throw Err("'=' or 'in' expected");
                }
                Activate(hidden);
                CheckNext(T.Do);
                Activate(names);
                Block();
                F.Actives.RemoveRange(mark, F.Actives.Count - mark);
                CheckMatch(T.End, T.For, line);
                F.Loops--;
            }

            void Body(int line, bool self)
            {
                var f = new Func { Parent = F, Line = line };
                funcs.Push(f);
                if (self) Activate(new List<Var> { Declare("self", 0) });
                CheckNext('(');
                var pars = new List<Var>();
                if (Tk != ')')
                {
                    do
                    {
                        if (Tk == T.Name) pars.Add(Declare(TakeName(), pars.Count));
                        else if (Tk == T.Dots)
                        {
                            lx.Next();
                            pars.Add(Declare("arg", pars.Count));    // LUA_COMPAT_VARARG
                            f.Vararg = true;
                        }
                        else throw Err("<name> or '...' expected");
                    } while (!f.Vararg && TestNext(','));
                }
                Activate(pars);
                CheckNext(')');
                Statements();
                CheckMatch(T.End, T.Function, line);
                funcs.Pop();
            }

            void ExprStat()
            {
                var k = PrimaryExp();
                if (k == E.Call) return;
                Assignment(k);
            }

            void Assignment(E k)
            {
                if (k != E.Var) throw Err("syntax error");
                if (TestNext(',')) Assignment(PrimaryExp());
                else
                {
                    CheckNext('=');
                    ExprList();
                }
            }

            void ExprList()
            {
                Expr();
                while (TestNext(',')) Expr();
            }

            E PrefixExp()
            {
                switch (Tk)
                {
                    case '(':
                        {
                            int line = lx.Line;
                            lx.Next();
                            Expr();
                            CheckMatch(')', '(', line);
                            return E.Other;
                        }
                    case T.Name:
                        UseName(TakeName());
                        return E.Var;
                    default:
                        throw Err("unexpected symbol");
                }
            }

            E PrimaryExp()
            {
                var k = PrefixExp();
                for (;;)
                {
                    switch (Tk)
                    {
                        case '.':
                            lx.Next();
                            CheckName();
                            k = E.Var;
                            break;
                        case '[':
                            lx.Next();
                            Expr();
                            CheckNext(']');
                            k = E.Var;
                            break;
                        case ':':
                            lx.Next();
                            CheckName();
                            FuncArgs();
                            k = E.Call;
                            break;
                        case '(':
                        case T.String:
                        case '{':
                            FuncArgs();
                            k = E.Call;
                            break;
                        default:
                            return k;
                    }
                }
            }

            void FuncArgs()
            {
                int line = lx.Line;
                switch (Tk)
                {
                    case '(':
                        if (line != lx.LastLine) throw Err("ambiguous syntax (function call x new statement)");
                        lx.Next();
                        if (Tk != ')') ExprList();
                        CheckMatch(')', '(', line);
                        break;
                    case '{':
                        Constructor();
                        break;
                    case T.String:
                        lx.Next();
                        break;
                    default:
                        throw Err("function arguments expected");
                }
            }

            void Constructor()
            {
                int line = lx.Line;
                CheckNext('{');
                do
                {
                    if (Tk == '}') break;
                    switch (Tk)
                    {
                        case T.Name:
                            if (lx.Ahead().Kind != '=') Expr();
                            else { lx.Next(); CheckNext('='); Expr(); }
                            break;
                        case '[':
                            lx.Next();
                            Expr();
                            CheckNext(']');
                            CheckNext('=');
                            Expr();
                            break;
                        default:
                            Expr();
                            break;
                    }
                } while (TestNext(',') || TestNext(';'));
                CheckMatch('}', '{', line);
            }

            void SimpleExp()
            {
                switch (Tk)
                {
                    case T.Number:
                    case T.String:
                    case T.Nil:
                    case T.True:
                    case T.False:
                        lx.Next();
                        return;
                    case T.Dots:
                        if (!F.Vararg) throw Err("cannot use '...' outside a vararg function");
                        lx.Next();
                        return;
                    case '{':
                        Constructor();
                        return;
                    case T.Function:
                        lx.Next();
                        Body(lx.Line, false);
                        return;
                    default:
                        PrimaryExp();
                        return;
                }
            }

            static bool Unary(int k) => k == T.Not || k == '-' || k == '#';

            static int Left(int k)
            {
                switch (k)
                {
                    case '+': case '-': return 6;
                    case '*': case '/': case '%': return 7;
                    case '^': return 10;
                    case T.Concat: return 5;
                    case T.Ne: case T.Eq: case '<': case T.Le: case '>': case T.Ge: return 3;
                    case T.And: return 2;
                    case T.Or: return 1;
                    default: return -1;
                }
            }

            static int Right(int k)
            {
                switch (k)
                {
                    case '^': return 9;
                    case T.Concat: return 4;
                    default: return Left(k);
                }
            }

            void Expr() => SubExpr(0);

            int SubExpr(int limit)
            {
                Enter();
                if (Unary(Tk))
                {
                    lx.Next();
                    SubExpr(8);
                }
                else SimpleExp();
                int op = Tk;
                while (Left(op) >= 0 && Left(op) > limit)
                {
                    lx.Next();
                    op = SubExpr(Right(op));
                }
                Leave();
                return op;
            }
        }
    }
}
