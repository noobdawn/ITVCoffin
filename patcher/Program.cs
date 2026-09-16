using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace ITVCoffin.Patcher;

// ITVCoffin.Patcher — client Assembly-CSharp.dll patcher.
//
// Rewrites the game's remote endpoints to the local proxy and skips the login signature
// check, so a patched client talks to the local capture proxy instead of the official server.
// The patch logic is intentionally identical to the verified original; only the namespace
// changed for packaging.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--dump")
        {
            return Dump(args);
        }

        if (args.Length < 2)
        {
            Console.WriteLine("usage: ITVCoffin.Patcher <in.dll> <out.dll>");
            Console.WriteLine("       ITVCoffin.Patcher --dump <dll> <typeFullName> <methodName>");
            return 1;
        }

        string inPath = args[0];
        string outPath = args[1];

        var mod = ModuleDefMD.Load(inPath);

        // ---- string replacements (remote endpoints -> localhost) ----
        var strMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // TCP login IPs (AddressConfig.loginIpDic) -> 127.0.0.1
            ["1.13.127.58"]     = "127.0.0.1",   // OpenBeta (official) TCP
            ["121.5.42.238"]    = "127.0.0.1",   // OutIP / fallback
            ["119.45.11.195"]   = "127.0.0.1",
            ["119.45.43.137"]   = "127.0.0.1",
            ["146.56.243.46"]   = "127.0.0.1",
            ["119.45.173.225"]  = "127.0.0.1",
            ["119.45.28.183"]   = "127.0.0.1",
            ["146.56.228.187"]  = "127.0.0.1",
            ["119.45.5.139"]    = "127.0.0.1",
            ["119.45.229.160"]  = "127.0.0.1",
            // HTTP / official / system hosts -> local HTTP server (18080 to avoid the Windows
            // reserved-port range 7998-8097 which blocks 8080).
            ["https://cweb.jinzhangshu.com"]      = "http://127.0.0.1:18080",
            ["https://official.jinzhangshu.com"]  = "http://127.0.0.1:18080",
            ["https://query.jinzhangshu.com"]     = "http://127.0.0.1:18080",
            ["http://121.5.42.238:18083"]         = "http://127.0.0.1:18080",
        };

        int strCount = 0, fldCount = 0;
        foreach (var t in mod.GetTypes())
        {
            foreach (var f in t.Fields)
            {
                if (f.Constant != null && f.Constant.Value is string s && strMap.TryGetValue(s, out var nv))
                {
                    f.Constant.Value = nv;
                    fldCount++;
                }
            }
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var instr in m.Body.Instructions)
                {
                    if (instr.OpCode == OpCodes.Ldstr && instr.Operand is string s2 && strMap.TryGetValue(s2, out var nv2))
                    {
                        instr.Operand = nv2;
                        strCount++;
                    }
                }
            }
        }

        // ---- port int literals -> 25313 ----
        int intCount = 0;
        foreach (var t in mod.GetTypes())
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var instr in m.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Ldc_I4 && instr.Operand is int v && (v == 15313 || v == 30531 || v == 30532))
                {
                    instr.Operand = 25313;
                    intCount++;
                }
            }
        }

        // ---- RSA verify -> return true (skip login signature check) ----
        int rsaCount = 0;
        foreach (var t in mod.GetTypes())
        {
            if (t.Name != "RSAFromPkcs8") continue;
            foreach (var m in t.Methods)
            {
                if (m.Name == "verify" && m.ReturnType != null && m.ReturnType.FullName == "System.Boolean" && m.HasBody)
                {
                    m.Body.Instructions.Clear();
                    m.Body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction());
                    m.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
                    m.Body.ExceptionHandlers.Clear();
                    m.Body.Variables.Clear();
                    m.Body.MaxStack = 1;
                    rsaCount++;
                }
            }
        }

        // ---- FileLoader.Load: disk-first Lua override ----
        int fileLoaderCount = PatchFileLoader(mod);

        mod.Write(outPath);
        Console.WriteLine($"patched: {strCount} string literals, {fldCount} field constants, {intCount} int literals, {rsaCount} RSA methods, fileloader: {fileLoaderCount} -> {outPath}");
        return 0;
    }

    // Rewrites FileLoader.Load(ref string path) so that a modified Lua file on disk is
    // loaded before falling back to the packaged bundle. The disk path is:
    //   <gameroot>\LuaMods\<text>   where text = GetFullname(path) minus the ".lua.txt" suffix.
    private static int PatchFileLoader(ModuleDefMD mod)
    {
        int count = 0;
        foreach (var t in mod.GetTypes())
        {
            if (t.FullName != "TPS.Framework.XLua.Loaders.FileLoader") continue;
            foreach (var m in t.Methods)
            {
                if (m.Name != "Load") continue;
                if (!m.HasBody) continue;
                if (m.ReturnType == null || m.ReturnType.FullName != "System.Byte[]") continue;
                if (m.MethodSig.Params.Count != 1) continue;
                if (m.MethodSig.Params[0].FullName != "System.String&") continue;
                RewriteFileLoaderLoad(mod, m);
                count++;
            }
        }
        return count;
    }

    private static void RewriteFileLoaderLoad(ModuleDefMD mod, MethodDef m)
    {
        var oldBody = m.Body;
        var oldInstrs = oldBody.Instructions;

        // Reuse the existing member references from the original body.
        IMethodDefOrRef Find(string name)
        {
            foreach (var i in oldInstrs)
            {
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand is IMethodDefOrRef mr && mr.Name == name)
                {
                    return mr;
                }
            }
            throw new InvalidOperationException($"FileLoader.Load: cannot find method '{name}' in original body");
        }

        var endsWith      = Find("EndsWith");
        var startsWith    = Find("StartsWith");
        var getFullname   = Find("GetFullname");
        var lastIndexOf   = Find("LastIndexOf");
        var substring     = Find("Substring");
        var loadText      = Find("LoadText");
        var opInequality  = Find("op_Inequality");
        var getBytes      = Find("get_bytes");
        var stringConcat  = Find("Concat");
        var logError      = Find("LogError");
        var stringFormat  = Find("Format");
        var luaLogError   = Find("LuaLogError");

        var corlib = mod.CorLibTypes;

        // New member references (System.IO.File / System.IO.Path are in mscorlib for this
        // Unity/IL2CPP profile; UnityEngine.Application is in UnityEngine.CoreModule).
        var fileTypeRef = new TypeRefUser(mod, "System.IO", "File", corlib.AssemblyRef);
        var pathTypeRef = new TypeRefUser(mod, "System.IO", "Path", corlib.AssemblyRef);

        var fileExists = new MemberRefUser(mod, "Exists",
            MethodSig.CreateStatic(corlib.Boolean, corlib.String), fileTypeRef);
        var fileReadAllBytes = new MemberRefUser(mod, "ReadAllBytes",
            MethodSig.CreateStatic(new SZArraySig(corlib.Byte), corlib.String), fileTypeRef);
        var pathCombine = new MemberRefUser(mod, "Combine",
            MethodSig.CreateStatic(corlib.String, corlib.String, corlib.String), pathTypeRef);
        var pathGetDirectoryName = new MemberRefUser(mod, "GetDirectoryName",
            MethodSig.CreateStatic(corlib.String, corlib.String), pathTypeRef);

        var unityAsm = mod.GetAssemblyRef("UnityEngine.CoreModule") ?? new AssemblyRefUser("UnityEngine.CoreModule");
        var applicationTypeRef = new TypeRefUser(mod, "UnityEngine", "Application", unityAsm);
        var getDataPath = new MemberRefUser(mod, "get_dataPath",
            MethodSig.CreateStatic(corlib.String), applicationTypeRef);

        // New locals (root, disk) appended after the original four locals.
        var rootLocal = new Local(corlib.String, "root");
        var diskLocal = new Local(corlib.String, "disk");

        // Label instruction objects (created before the branches that reference them).
        var luaGuardStart = new Instruction(OpCodes.Ldarg_1);
        var tryStart      = new Instruction(OpCodes.Ldnull);          // array = null (outside try)
        var tryBodyStart  = new Instruction(OpCodes.Ldarg_1);         // first instruction inside try
        var elseStart     = new Instruction(OpCodes.Ldarg_0);         // else: LoadText branch
        var logErrStart   = new Instruction(OpCodes.Ldstr, "[LuaLoad]=>dont find lua file:");
        var handlerStart  = new Instruction(OpCodes.Stloc_3);         // catch: ex = ...
        var after         = new Instruction(OpCodes.Ldloc_0);         // return array

        var emmyBrfalse    = new Instruction(OpCodes.Brfalse, luaGuardStart);
        var luaBrtrue      = new Instruction(OpCodes.Brtrue,  tryStart);
        var existsBrfalse  = new Instruction(OpCodes.Brfalse, elseStart);
        var leave          = new Instruction(OpCodes.Leave,   after);
        var catchLeave     = new Instruction(OpCodes.Leave,   after);
        var readBr         = new Instruction(OpCodes.Br,      leave);
        var elseBrfalse    = new Instruction(OpCodes.Brfalse, logErrStart);
        var elseBr         = new Instruction(OpCodes.Br,      leave);

        var list = new List<Instruction>();

        // if (path.EndsWith("emmy_core")) return null;
        list.Add(new Instruction(OpCodes.Ldarg_1));
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Ldstr, "emmy_core"));
        list.Add(new Instruction(OpCodes.Callvirt, endsWith));
        list.Add(emmyBrfalse);
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Ret));

        // if (!path.StartsWith("Lua")) return null;
        list.Add(luaGuardStart);
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Ldstr, "Lua"));
        list.Add(new Instruction(OpCodes.Callvirt, startsWith));
        list.Add(luaBrtrue);
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Ret));

        // byte[] array = null;
        list.Add(tryStart);
        list.Add(new Instruction(OpCodes.Stloc_0));

        // path = this.GetFullname(path);
        list.Add(tryBodyStart);
        list.Add(new Instruction(OpCodes.Ldarg_0));
        list.Add(new Instruction(OpCodes.Ldarg_1));
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Callvirt, getFullname));
        list.Add(new Instruction(OpCodes.Stind_Ref));

        // string text = path.Substring(0, path.LastIndexOf('.'));
        list.Add(new Instruction(OpCodes.Ldarg_1));
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Ldc_I4_0));
        list.Add(new Instruction(OpCodes.Ldarg_1));
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Ldc_I4_S, (sbyte)46));
        list.Add(new Instruction(OpCodes.Callvirt, lastIndexOf));
        list.Add(new Instruction(OpCodes.Callvirt, substring));
        list.Add(new Instruction(OpCodes.Stloc_1));

        // string root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "LuaMods");
        list.Add(new Instruction(OpCodes.Call, getDataPath));
        list.Add(new Instruction(OpCodes.Call, pathGetDirectoryName));
        list.Add(new Instruction(OpCodes.Ldstr, "LuaMods"));
        list.Add(new Instruction(OpCodes.Call, pathCombine));
        list.Add(new Instruction(OpCodes.Stloc_S, rootLocal));

        // string disk = Path.Combine(root, text);
        list.Add(new Instruction(OpCodes.Ldloc_S, rootLocal));
        list.Add(new Instruction(OpCodes.Ldloc_1));
        list.Add(new Instruction(OpCodes.Call, pathCombine));
        list.Add(new Instruction(OpCodes.Stloc_S, diskLocal));

        // if (File.Exists(disk))
        list.Add(new Instruction(OpCodes.Ldloc_S, diskLocal));
        list.Add(new Instruction(OpCodes.Call, fileExists));
        list.Add(existsBrfalse);

        //   array = File.ReadAllBytes(disk);
        list.Add(new Instruction(OpCodes.Ldloc_S, diskLocal));
        list.Add(new Instruction(OpCodes.Call, fileReadAllBytes));
        list.Add(new Instruction(OpCodes.Stloc_0));
        list.Add(readBr);

        // else { TextAsset textAsset = this.LoadText(text);
        list.Add(elseStart);
        list.Add(new Instruction(OpCodes.Ldloc_1));
        list.Add(new Instruction(OpCodes.Call, loadText));
        list.Add(new Instruction(OpCodes.Stloc_2));

        //   if (textAsset != null) array = textAsset.bytes;
        list.Add(new Instruction(OpCodes.Ldloc_2));
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Call, opInequality));
        list.Add(elseBrfalse);
        list.Add(new Instruction(OpCodes.Ldloc_2));
        list.Add(new Instruction(OpCodes.Callvirt, getBytes));
        list.Add(new Instruction(OpCodes.Stloc_0));
        list.Add(elseBr);

        //   else LogManager.LogError("[LuaLoad]=>dont find lua file:" + path); }
        list.Add(logErrStart);
        list.Add(new Instruction(OpCodes.Ldarg_1));
        list.Add(new Instruction(OpCodes.Ldind_Ref));
        list.Add(new Instruction(OpCodes.Call, stringConcat));
        list.Add(new Instruction(OpCodes.Call, logError));

        list.Add(leave);

        // catch (Exception ex) { LogManager.LuaLogError(string.Format("LuaLoader error :{0}", ex)); }
        list.Add(handlerStart);
        list.Add(new Instruction(OpCodes.Ldstr, "LuaLoader error :{0}"));
        list.Add(new Instruction(OpCodes.Ldloc_3));
        list.Add(new Instruction(OpCodes.Call, stringFormat));
        list.Add(new Instruction(OpCodes.Call, luaLogError));
        list.Add(catchLeave);

        // return array;
        list.Add(after);
        list.Add(new Instruction(OpCodes.Ret));

        var newBody = new CilBody
        {
            InitLocals = true,
            MaxStack = 8,
        };
        foreach (var v in oldBody.Variables)
        {
            newBody.Variables.Add(v);
        }
        newBody.Variables.Add(rootLocal);
        newBody.Variables.Add(diskLocal);
        foreach (var i in list)
        {
            newBody.Instructions.Add(i);
        }

        var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryBodyStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = after,
            CatchType = oldBody.ExceptionHandlers[0].CatchType,
        };
        newBody.ExceptionHandlers.Add(handler);

        m.Body = newBody;
    }

    // ---- dump mode: prints signature, locals, handlers and IL of one method ----
    private static int Dump(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: ITVCoffin.Patcher --dump <dll> <typeFullName> <methodName>");
            return 1;
        }
        var mod = ModuleDefMD.Load(args[1]);
        string typeName = args[2];
        string methodName = args[3];

        var type = mod.GetTypes().FirstOrDefault(t => t.FullName == typeName);
        if (type == null)
        {
            Console.WriteLine($"type not found: {typeName}");
            return 1;
        }
        var m = type.Methods.FirstOrDefault(mm => mm.Name == methodName);
        if (m == null)
        {
            Console.WriteLine($"method not found: {methodName}");
            return 1;
        }
        if (!m.HasBody)
        {
            Console.WriteLine("no body");
            return 1;
        }

        Console.WriteLine($".method {m.FullName}");
        Console.WriteLine($"signature: {m.MethodSig}");
        Console.WriteLine($"locals: {m.Body.Variables.Count}");
        for (int i = 0; i < m.Body.Variables.Count; i++)
        {
            Console.WriteLine($"  [{i}] {m.Body.Variables[i].Type}");
        }
        Console.WriteLine($"exception handlers: {m.Body.ExceptionHandlers.Count}");
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            Console.WriteLine($"  {eh.HandlerType} try=[{Index(m.Body.Instructions, eh.TryStart)}..{Index(m.Body.Instructions, eh.TryEnd)}) handler=[{Index(m.Body.Instructions, eh.HandlerStart)}..{Index(m.Body.Instructions, eh.HandlerEnd)}) catchType={eh.CatchType}");
        }
        Console.WriteLine("instructions:");
        for (int i = 0; i < m.Body.Instructions.Count; i++)
        {
            var ins = m.Body.Instructions[i];
            Console.WriteLine($"  {i:D3}: {ins.OpCode.Name,-12} {FormatOperand(ins, m.Body.Instructions)}");
        }
        return 0;
    }

    private static int Index(IList<Instruction> instrs, Instruction target)
    {
        return target == null ? -1 : instrs.IndexOf(target);
    }

    private static string FormatOperand(Instruction ins, IList<Instruction> instrs)
    {
        if (ins.Operand == null) return "";
        switch (ins.Operand)
        {
            case Instruction target:
                int idx = Index(instrs, target);
                return $"-> IL_{idx:D3}";
            case string s:
                return "\"" + s + "\"";
            case IMethodDefOrRef mr:
                return mr.FullName;
            case Local l:
                return $"V_{l.Index}";
            case Parameter p:
                return $"arg_{p.Index}";
            default:
                return ins.Operand.ToString() ?? "";
        }
    }
}
