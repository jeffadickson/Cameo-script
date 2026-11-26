namespace Cameo.Runtime.Syntax;

// `class`: a declaration that reads like a class and is rewritten, before parsing proper, into
// what a Teal programmer would write by hand — a record, its metatable, and its functions.
//
//     local class Dog is Animal
//        tricks: integer
//        function new(name: string, tricks: integer)   -- constructor: `self` is the new object
//           super(name)                                 -- the parent's constructor
//           self.tricks = tricks
//        end
//        function speak(self): string                   -- a method: its first parameter is self
//           return super.speak(self) .. "!"
//        end
//        function describe(n: integer): string ... end  -- no self: a function of the class itself
//        function __tostring(self): string ... end      -- a metamethod, by its name
//     end
//
// becomes
//
//     local record Dog is Animal
//        tricks: integer
//        speak: function(self): string
//        describe: function(n: integer): string
//        metamethod __tostring: function(self): string
//        new: function(name: string, tricks: integer): Dog
//        __init: function(self: Dog, name: string, tricks: integer)
//     end
//     local <metatable>: metatable<Dog> = { __index = Dog, __tostring = function(self: Dog): string ... end }
//     __class(Dog, <metatable>, Animal)                  -- Dog looks up what it lacks in Animal, and
//                                                        -- its objects get Animal's metamethods
//     function Dog:speak(): string return Animal.speak(self) .. "!" end
//     function Dog.describe(n: integer): string ... end
//     function Dog.__init(self: Dog, name: string, tricks: integer) Animal.__init(self, name) self.tricks = tricks end
//     function Dog.new(name: string, tricks: integer): Dog
//        local self = setmetatable({} as Dog, <metatable>)
//        Dog.__init(self, name, tricks)
//        return self
//     end
//
// Also:
//     local class Scope is Instrument implements Triggerable, Named uses Logging
//        private gainValue: number                      -- private: this class only; protected: and subclasses
//        get gain(self): number return self.gainValue end          -- a property: read as scope.gain
//        set gain(self, v: number) self.gainValue = v end          -- and assigned as scope.gain = 2
//     end
// `implements` lists interfaces (the record is declared `is` each of them, and the class must
// define their functions); `uses` lists mixins, whose functions are copied into the class.
//
// Because the result is ordinary Teal, the type checker checks it as usual and nothing about
// the VM or the compiler changes.
public sealed partial class Parser
{
    private bool AtClassDeclaration() =>
        (IsKw("local") || IsName("global")) && PeekTok().Kind == TokenKind.Name && PeekTok().Text == "class" && PeekTok(2).Kind == TokenKind.Name;

    // The record name token a class was rewritten to -> the source range of the class. Shared by
    // the parsers of one file (sub-parsers are made for lookahead).
    private sealed record ClassInfo((int, int, int, int) Range, HashSet<string> Defines, HashSet<string> Abstract, HashSet<string> ReadOnly, string Id);
    private Dictionary<Token, ClassInfo>? _classRanges;
    private Parser? _classOwner;
    private static Dictionary<Token, ClassInfo> ClassRanges(Parser p)
    {
        while (p._classOwner != null) p = p._classOwner;
        return p._classRanges ??= new(ReferenceEqualityComparer.Instance);
    }

    private sealed class ClassMember
    {
        public string Modifier = "";             // "", "private" or "protected"
        public string Kind = "function";         // "function", "get" or "set"
        public required Token Name;
        public required List<Token> Signature;   // "(" ... ")" [":" return types]
        public required List<Token> Body;        // everything after the signature, up to and including "end"
        public bool HasSelf;
    }

    private List<Token> Synthetic(string text, Token at)
    {
        List<Token> toks = Lexer.Tokenize(text, _file);
        toks.RemoveAt(toks.Count - 1); // its end-of-file marker
        foreach (Token t in toks) { t.Line = at.Line; t.Col = at.Col; t.Pos = at.Pos; t.End = at.End; }
        return toks;
    }

    private static Token NameToken(string text, Token at) => new() { Kind = TokenKind.Name, Text = text, Line = at.Line, Col = at.Col, Pos = at.Pos, End = at.End };

    // How many tokens, starting at `from`, make up one type.
    private int TypeLength(int from, int limit)
    {
        var toks = _toks.GetRange(from, limit - from);
        toks.Add(new Token { Kind = TokenKind.Eof, Line = _toks[limit - 1].Line, Col = _toks[limit - 1].Col });
        var sub = new Parser(toks, this);
        sub.ParseType();
        return sub._p;
    }

    // Index just past the `end` that closes the block opened at `from` (a `function`), or -1.
    private int BlockEndIndex(int from, bool countOpener = false)
    {
        int depth = countOpener ? 1 : 0;
        if (countOpener) from++;
        for (int i = from; i < _toks.Count; i++)
        {
            Token t = _toks[i];
            if (t.Kind == TokenKind.Eof) return -1;
            Token? prev = i > 0 ? _toks[i - 1] : null;
            if (t.Kind == TokenKind.Keyword)
            {
                switch (t.Text)
                {
                    case "function":
                        // `x: function(...)`, `as function(...)`: a type, which has no `end`
                        bool isType = i > from && prev != null && ((prev.Kind == TokenKind.Op && prev.Text is ":" or "|" or "<") || (prev.Kind == TokenKind.Name && prev.Text is "as" or "is"));
                        if (!isType) depth++;
                        break;
                    case "do": case "if": case "repeat": depth++; break;
                    case "end": case "until":
                        if (--depth == 0) return i + 1;
                        break;
                }
            }
            else if (t.Kind == TokenKind.Name && t.Text is "record" or "interface" or "enum" && prev != null
                     && ((prev.Kind == TokenKind.Keyword && prev.Text == "local") || (prev.Kind == TokenKind.Name && prev.Text == "global") || (prev.Kind == TokenKind.Op && prev.Text == "=")))
                depth++;
        }
        return -1;
    }

    /// <summary>Replaces the class declaration at the current position with the equivalent record,
    /// metatable and functions, and leaves the position at their first token.</summary>
    private void RewriteClass()
    {
        int start = _p;
        Token prefix = _toks[_p];
        Token classTok = _toks[_p + 1];
        Token nameTok = _toks[_p + 2];
        string name = nameTok.Text;
        int i = _p + 3;
        Token? parent = null;
        var interfaces = new List<List<Token>>();   // each a dotted name
        var mixins = new List<List<Token>>();
        if (_toks[i].Kind == TokenKind.Name && _toks[i].Text is "is" or "implements" or "uses" && _toks[i + 1].Kind != TokenKind.Name && !TokIsOp(_toks[i + 1], ":"))
            throw Error(_toks[i].Text == "is" ? "class name expected after 'is'" : $"a name expected after '{_toks[i].Text}'", _toks[i + 1]);
        while (_toks[i].Kind == TokenKind.Name && _toks[i].Text is "is" or "implements" or "uses" && _toks[i + 1].Kind == TokenKind.Name
               && !TokIsOp(_toks[i + 1], ":") && !TokIsOp(_toks[i + 2], ":") && !TokIsOp(_toks[i + 2], "("))
        {
            string word = _toks[i].Text;
            if (word == "is")
            {
                if (parent != null) throw Error($"class '{name}' already has a parent class (a class has one; use 'implements' for interfaces and 'uses' for mixins)", _toks[i]);
                parent = _toks[i + 1];
                i += 2;
                if (TokIsOp(_toks[i], ",")) throw Error($"a class has one parent class; list interfaces after 'implements' and mixins after 'uses'", _toks[i]);
                continue;
            }
            i++;
            while (true)
            {
                if (_toks[i].Kind != TokenKind.Name) throw Error($"a name expected after '{word}'", _toks[i]);
                var dotted = new List<Token> { _toks[i++] };
                while (TokIsOp(_toks[i], ".") && _toks[i + 1].Kind == TokenKind.Name) { dotted.Add(_toks[i]); dotted.Add(_toks[i + 1]); i += 2; }
                (word == "implements" ? interfaces : mixins).Add(dotted);
                if (!TokIsOp(_toks[i], ",")) break;
                i++;
            }
        }

        var fields = new List<Token>();
        var methods = new List<ClassMember>();
        var properties = new List<ClassMember>();
        ClassMember? ctor = null;
        while (true)
        {
            Token t = _toks[i];
            // private / protected in front of a field, a function or a property
            string modifier = "";
            if (t.Kind == TokenKind.Name && t.Text is "private" or "protected" && !TokIsOp(_toks[i + 1], ":")
                && (_toks[i + 1].Kind == TokenKind.Name || (_toks[i + 1].Kind == TokenKind.Keyword && _toks[i + 1].Text == "function")))
            {
                modifier = t.Text;
                t = _toks[++i];
            }
            // abstract function name(self, ...): T      -- no body: subclasses define it
            if (t.Kind == TokenKind.Name && t.Text == "abstract" && _toks[i + 1].Kind == TokenKind.Keyword && _toks[i + 1].Text == "function")
            {
                Token aname = _toks[i + 2];
                if (aname.Kind != TokenKind.Name) throw Error("method name expected", aname);
                if (!TokIsOp(_toks[i + 3], "(")) throw Error($"'(' expected after '{aname.Text}'", _toks[i + 3]);
                var probe = new List<Token> { _toks[i + 1] };
                probe.AddRange(_toks.GetRange(i + 3, _toks.Count - (i + 3)));
                var sub = new Parser(probe, this);
                try { sub.ParseType(); }
                catch (CameoSyntaxException e) { throw Error($"in the parameters of '{aname.Text}' (a class member needs typed parameters): {e.Diagnostic.Message}", aname); }
                int sigLen = sub._p - 1;
                if (!(_toks[i + 4].Kind == TokenKind.Name && _toks[i + 4].Text == "self")) throw Error($"an abstract function is a method: 'abstract function {aname.Text}(self, ...)'", aname);
                if (aname.Text == "new" || aname.Text.StartsWith("__", StringComparison.Ordinal)) throw Error("a constructor or metamethod cannot be abstract", aname);
                if (modifier == "private") throw Error("an abstract function cannot be private (a subclass must be able to define it)", aname);
                if (methods.Exists(m => m.Name.Text == aname.Text) || properties.Exists(q => q.Name.Text == aname.Text)) throw Error($"'{aname.Text}' is defined twice in class '{name}'", aname);
                methods.Add(new ClassMember { Name = aname, Modifier = modifier, Kind = "abstract", Signature = _toks.GetRange(i + 3, sigLen), Body = new List<Token>(), HasSelf = true });
                i += 3 + sigLen;
                continue;
            }
            // get name(self): T ... end   /   set name(self, v: T) ... end
            if (t.Kind == TokenKind.Name && t.Text is "get" or "set" && _toks[i + 1].Kind == TokenKind.Name && TokIsOp(_toks[i + 2], "("))
            {
                Token pname = _toks[i + 1];
                int stop = BlockEndIndex(i + 1 - 1, countOpener: true);
                if (stop < 0) throw Error($"'end' expected (to close the property '{pname.Text}' at line {t.Line})", t);
                var probe = new List<Token> { new Token { Kind = TokenKind.Keyword, Text = "function", Line = t.Line, Col = t.Col, Pos = t.Pos, End = t.End } };
                probe.AddRange(_toks.GetRange(i + 2, stop - (i + 2)));
                probe.Add(new Token { Kind = TokenKind.Eof, Line = _toks[stop - 1].Line, Col = _toks[stop - 1].Col });
                var sub = new Parser(probe, this);
                try { sub.ParseType(); }
                catch (CameoSyntaxException e) { throw Error($"in the property '{pname.Text}': {e.Diagnostic.Message}", pname); }
                int sigLen = sub._p - 1;
                var prop = new ClassMember
                {
                    Name = pname, Modifier = modifier, Kind = t.Text,
                    Signature = _toks.GetRange(i + 2, sigLen),
                    Body = _toks.GetRange(i + 2 + sigLen, stop - (i + 2 + sigLen)),
                    HasSelf = _toks[i + 3].Kind == TokenKind.Name && _toks[i + 3].Text == "self",
                };
                List<Token> sig = prop.Signature;
                bool shape = t.Text == "get"
                    ? sig.Count >= 5 && prop.HasSelf && TokIsOp(sig[2], ")") && TokIsOp(sig[3], ":")
                    : sig.Count >= 7 && prop.HasSelf && TokIsOp(sig[2], ",") && sig[3].Kind == TokenKind.Name && TokIsOp(sig[4], ":") && TokIsOp(sig[^1], ")");
                if (!shape)
                    throw Error(t.Text == "get" ? $"a getter is written 'get {pname.Text}(self): Type'" : $"a setter is written 'set {pname.Text}(self, value: Type)' and returns nothing", pname);
                if (properties.Exists(q => q.Name.Text == pname.Text && q.Kind == t.Text)) throw Error($"property '{pname.Text}' has two {t.Text}ters", pname);
                if (methods.Exists(m => m.Name.Text == pname.Text)) throw Error($"'{pname.Text}' is defined twice in class '{name}'", pname);
                properties.Add(prop);
                i = stop;
                continue;
            }
            if (t.Kind == TokenKind.Eof) throw Error($"'end' expected (to close 'class' at line {classTok.Line})", t);
            if (t.Kind == TokenKind.Keyword && t.Text == "end") { i++; break; }
            if (t.Kind == TokenKind.Keyword && t.Text == "function")
            {
                Token mname = _toks[i + 1];
                if (mname.Kind != TokenKind.Name) throw Error("method name expected", mname);
                if (!TokIsOp(_toks[i + 2], "(")) throw Error($"'(' expected after '{mname.Text}'", _toks[i + 2]);
                int stop = BlockEndIndex(i);
                if (stop < 0) throw Error($"'end' expected (to close function '{mname.Text}' at line {t.Line})", t);
                // the signature is a function type: "function" + "(" ... ")" [":" returns]
                var probe = new List<Token> { t };
                probe.AddRange(_toks.GetRange(i + 2, stop - (i + 2)));
                probe.Add(new Token { Kind = TokenKind.Eof, Line = _toks[stop - 1].Line, Col = _toks[stop - 1].Col });
                var sub = new Parser(probe, this);
                try { sub.ParseType(); }
                catch (CameoSyntaxException e)
                {
                    throw Error($"in the parameters of '{mname.Text}' (a class member needs typed parameters): {e.Diagnostic.Message}", mname);
                }
                int sigLen = sub._p - 1;
                // In a type, a bare name is a type; in a class member it would be an untyped
                // parameter, which Teal does not allow. Say so here rather than as "unknown type x".
                for (int k = i + 3, depth = 1; k < i + 2 + sigLen && depth > 0; k++)
                {
                    Token s = _toks[k];
                    if (s.Kind == TokenKind.Op && s.Text is "(" or "{" or "<" or "[") depth++;
                    else if (s.Kind == TokenKind.Op && s.Text is ")" or "}" or ">" or "]") depth--;
                    else if (depth == 1 && s.Kind == TokenKind.Name && s.Text != "self" && (TokIsOp(_toks[k - 1], "(") || TokIsOp(_toks[k - 1], ","))
                             && (TokIsOp(_toks[k + 1], ",") || TokIsOp(_toks[k + 1], ")")))
                        throw Error($"parameter '{s.Text}' of '{mname.Text}' needs a type (a class member needs typed parameters)", s);
                }
                var member = new ClassMember
                {
                    Modifier = modifier,
                    Name = mname,
                    Signature = _toks.GetRange(i + 2, sigLen),
                    Body = _toks.GetRange(i + 2 + sigLen, stop - (i + 2 + sigLen)),
                    HasSelf = _toks[i + 3].Kind == TokenKind.Name && _toks[i + 3].Text == "self",
                };
                if (mname.Text == "new")
                {
                    if (ctor != null) throw Error("a class has one constructor", mname);
                    if (modifier != "") throw Error($"the constructor cannot be {modifier}", mname);
                    if (member.HasSelf) throw Error("the constructor does not declare 'self' (it is the object being created)", mname);
                    if (member.Signature.Count > 0 && TokIsOp(member.Signature[^1], ")") == false) throw Error("the constructor does not declare a return type", mname);
                    ctor = member;
                }
                else
                {
                    if (methods.Exists(m => m.Name.Text == mname.Text) || properties.Exists(q => q.Name.Text == mname.Text)) throw Error($"'{mname.Text}' is defined twice in class '{name}'", mname);
                    if (modifier != "" && mname.Text.StartsWith("__", StringComparison.Ordinal)) throw Error($"a metamethod cannot be {modifier}", mname);
                    methods.Add(member);
                }
                i = stop;
                continue;
            }
            if (t.Kind == TokenKind.Name && TokIsOp(_toks[i + 1], ":"))
            {
                int len;
                try { len = TypeLength(i + 2, _toks.Count); }
                catch (CameoSyntaxException e) { throw Error($"in the type of field '{t.Text}': {e.Diagnostic.Message}", t); }
                if (modifier != "") fields.Add(NameToken(modifier, t));
                fields.AddRange(_toks.GetRange(i, 2 + len));
                i += 2 + len;
                continue;
            }
            throw Error($"a field ('name: type') or a function expected in class '{name}' near '{t}'", t);
        }
        int end = i;

        // super(...) -> Parent.__init(self, ...)   and   super.m(...) -> Parent.m(...)
        List<Token> WithSuper(List<Token> body)
        {
            var result = new List<Token>(body.Count);
            for (int k = 0; k < body.Count; k++)
            {
                Token t = body[k];
                if (t.Kind != TokenKind.Name || t.Text != "super" || (k > 0 && TokIsOp(body[k - 1], "."))) { result.Add(t); continue; }
                if (parent == null) throw Error($"'super' in class '{name}', which has no parent class", t);
                result.Add(NameToken(parent.Text, t));
                if (k + 1 < body.Count && TokIsOp(body[k + 1], "("))
                {
                    bool empty = k + 2 < body.Count && TokIsOp(body[k + 2], ")");
                    result.AddRange(Synthetic(empty ? ". __init ( self" : ". __init ( self ,", t));
                    k++; // the "(" is now part of the text above
                }
            }
            return result;
        }

        // the parameters of the constructor, without the parentheses
        List<Token> ctorParams = ctor == null ? new List<Token>() : ctor.Signature.GetRange(1, ctor.Signature.Count - 2);
        var argNames = new List<string>();
        for (int k = 0, depth = 0; k < ctorParams.Count; k++)
        {
            Token t = ctorParams[k];
            if (t.Kind == TokenKind.Op && t.Text is "(" or "{" or "<" or "[") depth++;
            else if (t.Kind == TokenKind.Op && t.Text is ")" or "}" or ">" or "]") depth--;
            else if (depth == 0 && (k == 0 || TokIsOp(ctorParams[k - 1], ",")))
            {
                if (t.Kind == TokenKind.Name) argNames.Add(t.Text);
                else if (TokIsOp(t, "...")) argNames.Add("...");
            }
        }

        Token mt = NameToken($"({name} metatable)", nameTok);
        var o = new List<Token>();
        void Text(string text, Token? at = null) => o.AddRange(Synthetic(text, at ?? nameTok));

        // ---- the record
        o.Add(prefix);
        Text("record");
        Token recordName = NameToken(name, nameTok);
        var defines = new HashSet<string>(methods.Where(m => m.Kind != "abstract").Select(m => m.Name.Text).Concat(properties.Select(q => q.Name.Text)));
        var abstracts = new HashSet<string>(methods.Where(m => m.Kind == "abstract").Select(m => m.Name.Text));
        var readOnly = new HashSet<string>(properties.Where(q => q.Kind == "get" && !properties.Exists(o => o.Kind == "set" && o.Name.Text == q.Name.Text)).Select(q => q.Name.Text));
        // identifies the class at run time (for `is` and instanceof): unique per declaration
        string classId = $"{name}@{_file}:{nameTok.Line}:{nameTok.Col}".Replace("\\", "/").Replace("\"", "'");
        for (int k = 0; k + 1 < fields.Count; k++)
            if (fields[k].Kind == TokenKind.Name && TokIsOp(fields[k + 1], ":") && (k == 0 || !TokIsOp(fields[k - 1], ","))) defines.Add(fields[k].Text);
        ClassRanges(this)[recordName] = new ClassInfo((prefix.Line, prefix.Col, _toks[end - 1].Line, _toks[end - 1].Col + 3), defines, abstracts, readOnly, classId);
        o.Add(recordName);
        // the parent class, then the interfaces, then the mixins: the record is each of them
        var supers = new List<List<Token>>();
        if (parent != null) supers.Add(new List<Token> { parent });
        supers.AddRange(interfaces);
        supers.AddRange(mixins);
        for (int k = 0; k < supers.Count; k++)
        {
            Text(k == 0 ? "is" : ",");
            o.AddRange(supers[k]);
        }
        o.AddRange(fields);
        foreach (ClassMember m in methods)
        {
            Text((m.Modifier != "" ? m.Modifier + " " : "") + (m.Name.Text.StartsWith("__", StringComparison.Ordinal) ? "metamethod " : "") + m.Name.Text + " : function", m.Name);
            o.AddRange(m.Signature);
        }
        // a property is a field of its type, as far as the checker is concerned
        foreach (ClassMember q in properties)
        {
            if (q.Kind == "set" && properties.Exists(g => g.Kind == "get" && g.Name.Text == q.Name.Text)) continue;
            Text((q.Modifier != "" ? q.Modifier + " " : "") + q.Name.Text + " :", q.Name);
            o.AddRange(q.Kind == "get" ? q.Signature.GetRange(4, q.Signature.Count - 4) : q.Signature.GetRange(5, q.Signature.Count - 6));
        }
        if (ctor != null)
        {
            Text("new : function", ctor.Name);
            o.AddRange(ctor.Signature);
            Text($": {name}  __init : function ( self : {name}" + (ctorParams.Count > 0 ? " ," : ""), ctor.Name);
            o.AddRange(ctorParams);
            Text(")", ctor.Name);
        }
        else Text($"new : function ( ... : any ) : {name}  __init : function ( self : {name} , ... : any )");
        Text("end");

        // ---- the metatable, with the metamethods in it
        Text("local");
        o.Add(mt);
        Text($": metatable < {name} > = {{ __index = {name}");
        foreach (ClassMember m in methods)
        {
            if (!m.Name.Text.StartsWith("__", StringComparison.Ordinal)) continue;
            Text($", {m.Name.Text} = function", m.Name);
            o.AddRange(m.Signature);
            o.AddRange(WithSuper(m.Body));
        }
        Text("}");
        // ---- methods and class functions
        foreach (ClassMember m in methods)
        {
            if (m.Name.Text.StartsWith("__", StringComparison.Ordinal) || m.Kind == "abstract") continue;
            bool bareSelf = m.HasSelf && (TokIsOp(m.Signature[2], ",") || TokIsOp(m.Signature[2], ")"));
            if (bareSelf)
            {
                // `function f(self, x: T)`: written with a colon, so that self gets the class's type
                Text($"function {name} : {m.Name.Text} (", m.Name);
                o.AddRange(m.Signature.GetRange(TokIsOp(m.Signature[2], ",") ? 3 : 2, m.Signature.Count - (TokIsOp(m.Signature[2], ",") ? 3 : 2)));
            }
            else
            {
                Text($"function {name} . {m.Name.Text}", m.Name);
                o.AddRange(m.Signature);
            }
            o.AddRange(WithSuper(m.Body));
        }

        // ---- construction: __init fills in an object, new makes one
        if (ctor != null)
        {
            Text($"function {name} . __init ( self : {name}" + (ctorParams.Count > 0 ? " ," : ""), ctor.Name);
            o.AddRange(ctorParams);
            Text(")", ctor.Name);
            o.AddRange(WithSuper(ctor.Body));
            Text($"function {name} . new", ctor.Name);
            o.AddRange(ctor.Signature);
            Text($": {name}  local self = setmetatable ( {{ }} as {name} ,", ctor.Name);
            o.Add(mt);
            Text($")  {name} . __init ( self" + string.Concat(argNames.Select(a => " , " + a)) + ")  return self  end", ctor.Name);
        }
        else
        {
            // no constructor of its own: the parent's (if any) runs with whatever is passed
            Text($"function {name} . __init ( self : {name} , ... : any )" + (parent != null ? $" ( {parent.Text} . __init as function ( ... : any ) ) ( self , ... )" : " local _ = self") + " end");
            Text($"function {name} . new ( ... : any ) : {name}  local self = setmetatable ( {{ }} as {name} ,");
            o.Add(mt);
            Text($")  {name} . __init ( self , ... )  return self  end");
        }

        // ---- last, once the class's functions exist: inheritance, mixins, properties
        Text("__class (");
        o.AddRange(Synthetic(name + " ,", nameTok));
        o.Add(mt);
        Text(parent != null ? $", {parent.Text} , {{" : ", nil , {");
        for (int k = 0; k < mixins.Count; k++)
        {
            if (k > 0) Text(",");
            o.AddRange(mixins[k]);
        }
        foreach (string kind in new[] { "get", "set" })
        {
            Text("} , {");
            bool first = true;
            foreach (ClassMember q in properties)
            {
                if (q.Kind != kind) continue;
                Text((first ? "" : ", ") + $"{q.Name.Text} = function ( self : {name}", q.Name);
                o.AddRange(q.Signature.GetRange(2, q.Signature.Count - 2));
                o.AddRange(WithSuper(q.Body));
                first = false;
            }
        }
        // name, and what the run time needs to know: identity, abstract functions, hidden members
        string Names(IEnumerable<string> names) => "{ " + string.Join(" , ", names.Select(n => "\"" + n + "\"")) + " }";
        var hiddenFields = new Dictionary<string, string>();
        for (int k = 0; k + 2 < fields.Count; k++)
            if (fields[k].Kind == TokenKind.Name && fields[k].Text is "private" or "protected" && fields[k + 1].Kind == TokenKind.Name && TokIsOp(fields[k + 2], ":"))
                hiddenFields[fields[k + 1].Text] = fields[k].Text;
        IEnumerable<string> Hidden(string modifier) => methods.Where(m => m.Modifier == modifier).Select(m => m.Name.Text)
            .Concat(properties.Where(q => q.Modifier == modifier).Select(q => q.Name.Text))
            .Concat(hiddenFields.Where(f => f.Value == modifier).Select(f => f.Key)).Distinct();
        Text($"}} , \"{name}\" , {{ id = \"{classId}\" , abstract = {Names(abstracts)} , private = {Names(Hidden("private"))} , protected = {Names(Hidden("protected"))} }} )");

        PrepareTokenMutation();
        _toks.RemoveRange(start, end - start);
        _toks.InsertRange(start, o);
        // Class lowering replaces token positions; cached matching indices belong to
        // the old stream and must be rebuilt before looking for subsequent arrows.
        _parenthesisPairs = null;
        _p = start;
    }
}
